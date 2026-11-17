using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Warp.Core;
using Warp.Core.Interceptors;
using Warp.Core.Metrics;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Core;

/// <summary>
/// R30 / SC6 and §G: a host's own <see cref="DbContext"/> keeps behaving the same once it adds Warp. The host
/// context is deliberately unlike <see cref="TestContext"/>: its own <see cref="SaveChangesInterceptor"/> (sync and
/// async) stamps an audit column, it uses <c>UseSnakeCaseNamingConvention()</c>, and it maps one auditable entity in
/// the default schema. Each save test runs inside one transaction on one connection that is rolled back at the end,
/// so the host table never outlives the test and the two contexts (with and without Warp) see the same table.
/// Serialized in <c>HeavyIntegration</c> (§4.7.1): it runs DDL inside a transaction on the shared database, which
/// takes schema locks a parallel neighbour can queue behind.
/// </summary>
[GenerateDatabaseTests(SerializeInCollection = "HeavyIntegration")]
public abstract class HostDbContextCoexistenceTestsBase : IAsyncLifetime
{
    private static readonly DateTime Stamp = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

    private readonly IDatabaseFixture _fixture;

    protected HostDbContextCoexistenceTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task SaveChanges_HostInterceptor_SameCountsAndStampsWithAndWithoutWarp()
    {
        var (connectionString, isPostgres) = await ProbeAsync();
        await using var connection = CreateConnection(connectionString, isPostgres);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);

        var plainInterceptor = new AuditInterceptor();
        await using (var plain = CreatePlainContext(connection, isPostgres, plainInterceptor))
        {
            await plain.Database.UseTransactionAsync(transaction, Ct);
            await CreateHostTableAsync(plain);
        }

        var plainOutcome = await SaveTwiceAsync(CreatePlainContext(connection, isPostgres, plainInterceptor), transaction, "plain");

        var warpInterceptor = new AuditInterceptor();
        await using var provider = BuildWarpProvider(connection, isPostgres, warpInterceptor);
        await using var scope = provider.CreateAsyncScope();
        var warpOutcome = await SaveTwiceAsync(scope.ServiceProvider.GetRequiredService<HostDbContext>(), transaction, "warp");

        // Non-vacuous: the host interceptor really ran on both paths, once per save, on each of its two overrides.
        plainOutcome.ShouldBe(new SaveOutcome(SyncCalls: 1, AsyncCalls: 1, Stamped: 2, AuditedAt: Stamp.AddMinutes(2)));
        warpOutcome.ShouldBe(plainOutcome);

        await transaction.RollbackAsync(Ct);
    }

    [TimedFact]
    public async Task SaveChangesAsync_EntityWithoutConcurrencyToken_IsUntouchedByWarpInterceptor()
    {
        var (connectionString, isPostgres) = await ProbeAsync();
        await using var connection = CreateConnection(connectionString, isPostgres);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var plain = CreatePlainContext(connection, isPostgres, new AuditInterceptor()))
        {
            await plain.Database.UseTransactionAsync(transaction, Ct);
            await CreateHostTableAsync(plain);
        }

        await using var provider = BuildWarpProvider(connection, isPostgres, new AuditInterceptor());
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<HostDbContext>();
        await context.Database.UseTransactionAsync(transaction, Ct);

        // Warp's interceptor is on this context (so the assertion below is not vacuous)…
        context.GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()!
            .Interceptors!
            .OfType<SaveChangesConcurrencyTokenInterceptor>()
            .ShouldHaveSingleItem();

        // …and an entity that merely has a Guid Version property, without implementing IConcurrencyToken, keeps it.
        var version = Guid.NewGuid();
        var order = new HostOrder { Reference = "versioned", Amount = 1, Version = version };
        context.Set<HostOrder>().Add(order);
        await context.SaveChangesAsync(Ct);
        order.Amount = 2;
        await context.SaveChangesAsync(Ct);

        order.Version.ShouldBe(version);
        var persisted = await context.Set<HostOrder>()
            .AsNoTracking()
            .Where(x => x.Id == order.Id)
            .Select(x => x.Version)
            .FirstAsync(Ct);
        persisted.ShouldBe(version);

        await transaction.RollbackAsync(Ct);
    }

    [TimedFact]
    public async Task GetDifferences_WarpEnabledHostModel_CreatesEveryWarpSchemaTableAndTheSloMetricColumns()
    {
        var (connectionString, isPostgres) = await ProbeAsync();
        await using var connection = CreateConnection(connectionString, isPostgres);
        await using var provider = BuildWarpProvider(connection, isPostgres, new AuditInterceptor());
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<HostDbContext>();
        var relationalModel = context.GetService<IDesignTimeModel>().Model.GetRelationalModel();

        // What the host's `dotnet ef migrations add` emits from an empty snapshot.
        var operations = context.GetService<IMigrationsModelDiffer>().GetDifferences(null, relationalModel);

        var creates = operations
            .OfType<CreateTableOperation>()
            .ToList();
        var tables = creates.ConvertAll(x => $"{x.Schema}.{x.Name}");
        tables.ShouldContain("warp.metric_point");
        tables.ShouldContain("warp.metric_instrument");
        tables.ShouldContain("warp.metric_board_tile");
        tables.ShouldContain("warp.metric_board");
        tables.ShouldContain("warp.metric_annotation");
        tables.ShouldContain("warp.saved_view");
        tables.ShouldContain(".host_order");
        operations.OfType<EnsureSchemaOperation>().ShouldContain(x => string.Equals(x.Name, "warp", StringComparison.Ordinal));

        // §G: every table the model declares in the warp schema is created, not just the two new ones.
        var warpTables = relationalModel.Tables
            .Where(x => string.Equals(x.Schema, "warp", StringComparison.Ordinal))
            .Select(x => $"warp.{x.Name}")
            .ToList();
        warpTables.ShouldNotBeEmpty();
        foreach (var table in warpTables)
        {
            tables.ShouldContain(table);
        }

        // The nine nullable metric-SLO columns land on slo_definition (seven for #329's kinds, plus comparison and
        // gauge_aggregate for #331): nullable, so the migration is additive on a table with rows.
        var sloColumns = creates
            .Single(x => string.Equals(x.Schema, "warp", StringComparison.Ordinal) && string.Equals(x.Name, "slo_definition", StringComparison.Ordinal))
            .Columns
            .ToDictionary(x => x.Name, StringComparer.Ordinal);
        foreach (var column in new[] { "tag_filter", "secondary_metric", "secondary_tag_filter", "ratio_tag", "good_values", "bad_values", "baseline_days", "comparison", "gauge_aggregate" })
        {
            sloColumns.ShouldContainKey(column);
            sloColumns[column].IsNullable.ShouldBeTrue(column);
        }

        // #330: metric_point.version is nullable (null = collected without ByVersion), so existing rows stay valid.
        var pointColumns = creates
            .Single(x => string.Equals(x.Schema, "warp", StringComparison.Ordinal) && string.Equals(x.Name, "metric_point", StringComparison.Ordinal))
            .Columns
            .ToDictionary(x => x.Name, StringComparer.Ordinal);
        pointColumns.ShouldContainKey("version");
        pointColumns["version"].IsNullable.ShouldBeTrue("version");

        // Named boards: metric_board_tile.board_id is nullable (null = the Default board), so existing tiles stay valid
        // with no data migration, and it is a soft reference: no foreign key to metric_board.
        var tileTable = creates
            .Single(x => string.Equals(x.Schema, "warp", StringComparison.Ordinal) && string.Equals(x.Name, "metric_board_tile", StringComparison.Ordinal));
        var boardColumn = tileTable.Columns.Single(x => string.Equals(x.Name, "board_id", StringComparison.Ordinal));
        boardColumn.IsNullable.ShouldBeTrue("board_id");
        tileTable.ForeignKeys.ShouldBeEmpty();

        // A seeded board's seed key: nullable (boards created in the dashboard have none), so the column is additive.
        var boardTable = creates
            .Single(x => string.Equals(x.Schema, "warp", StringComparison.Ordinal) && string.Equals(x.Name, "metric_board", StringComparison.Ordinal));
        boardTable.Columns.Single(x => string.Equals(x.Name, "seed_key", StringComparison.Ordinal)).IsNullable.ShouldBeTrue("seed_key");

        // Saved views: target is nullable (an issues view has none), the rest required.
        var viewTable = creates
            .Single(x => string.Equals(x.Schema, "warp", StringComparison.Ordinal) && string.Equals(x.Name, "saved_view", StringComparison.Ordinal));
        viewTable.Columns.Single(x => string.Equals(x.Name, "target", StringComparison.Ordinal)).IsNullable.ShouldBeTrue("target");
        viewTable.Columns.Single(x => string.Equals(x.Name, "query", StringComparison.Ordinal)).IsNullable.ShouldBeFalse("query");
        viewTable.Columns.Single(x => string.Equals(x.Name, "author", StringComparison.Ordinal)).IsNullable.ShouldBeFalse("author");
    }

    private static async Task<SaveOutcome> SaveTwiceAsync(HostDbContext context, DbTransaction transaction, string reference)
    {
        await using (context)
        {
            await context.Database.UseTransactionAsync(transaction, Ct);
            var interceptor = context.GetService<IDbContextOptions>()
                .FindExtension<CoreOptionsExtension>()!
                .Interceptors!
                .OfType<AuditInterceptor>()
                .Single();

            var order = new HostOrder { Reference = reference, Amount = 10 };
            context.Set<HostOrder>().Add(order);

            // One save through each of the interceptor's overrides: the sync call is the point here.
#pragma warning disable MA0042, S6966
            context.SaveChanges();
#pragma warning restore MA0042, S6966
            order.Amount = 20;
            await context.SaveChangesAsync(Ct);

            var auditedAt = await context.Set<HostOrder>()
                .AsNoTracking()
                .Where(x => x.Id == order.Id)
                .Select(x => x.AuditedAt)
                .FirstAsync(Ct);

            return new SaveOutcome(interceptor.SyncCalls, interceptor.AsyncCalls, interceptor.Stamped, auditedAt);
        }
    }

    private static async Task CreateHostTableAsync(HostDbContext plain)
    {
        // Provider-correct DDL for the host table alone, generated from the plain model (it has no Warp entities).
        var model = plain.GetService<IDesignTimeModel>().Model;
        var operations = plain.GetService<IMigrationsModelDiffer>().GetDifferences(null, model.GetRelationalModel());
        var commands = plain.GetService<IMigrationsSqlGenerator>().Generate(operations, model);

        foreach (var command in commands)
        {
            await plain.Database.ExecuteSqlRawAsync(command.CommandText, Ct);
        }
    }

    private static HostDbContext CreatePlainContext(DbConnection connection, bool isPostgres, AuditInterceptor interceptor)
    {
        var builder = new DbContextOptionsBuilder<HostDbContext>();
        ConfigureHost(builder, connection, isPostgres, interceptor);

        return new HostDbContext(builder.Options);
    }

    private static ServiceProvider BuildWarpProvider(DbConnection connection, bool isPostgres, AuditInterceptor interceptor)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // AddDbContext first: AddWarp hooks the registered options (the documented ordering caveat).
        services.AddDbContext<HostDbContext>(x => ConfigureHost(x, connection, isPostgres, interceptor));
        services.AddWarp<HostDbContext>(x => x.AddMetrics(y => y.CollectMeter("Host.Coexistence")));

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static void ConfigureHost(DbContextOptionsBuilder builder, DbConnection connection, bool isPostgres, AuditInterceptor interceptor)
    {
        if (isPostgres)
        {
            builder.UseNpgsql(connection);
        }
        else
        {
            builder.UseSqlServer(connection);
        }

        builder.UseSnakeCaseNamingConvention().AddInterceptors(interceptor);
    }

    private static DbConnection CreateConnection(string connectionString, bool isPostgres)
        => isPostgres ? new NpgsqlConnection(connectionString) : new SqlConnection(connectionString);

    private async Task<(string ConnectionString, bool IsPostgres)> ProbeAsync()
    {
        await using var probe = _fixture.CreateContext();

        return (probe.Database.GetConnectionString()!, probe.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true);
    }

    private sealed record SaveOutcome(int SyncCalls, int AsyncCalls, int Stamped, DateTime? AuditedAt);

    private sealed class HostDbContext : DbContext
    {
        public HostDbContext(DbContextOptions<HostDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<HostOrder>(x =>
            {
                x.HasKey(y => y.Id);
                x.Property(y => y.Reference).HasMaxLength(64);
            });
        }
    }

    public sealed class HostOrder
    {
        public int Id { get; set; }

        public string Reference { get; set; } = string.Empty;

        public int Amount { get; set; }

        public Guid Version { get; set; }

        public DateTime? AuditedAt { get; set; }
    }

    // The host's own audit interceptor: counts each override separately and stamps every added or modified order
    // with a deterministic, per-call time, so two runs can be compared value for value.
    private sealed class AuditInterceptor : SaveChangesInterceptor
    {
        private int _calls;

        public int SyncCalls { get; private set; }

        public int AsyncCalls { get; private set; }

        public int Stamped { get; private set; }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            SyncCalls++;
            StampOrders(eventData.Context);

            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            AsyncCalls++;
            StampOrders(eventData.Context);

            return ValueTask.FromResult(result);
        }

        private void StampOrders(DbContext? context)
        {
            _calls++;
            var entries = context!.ChangeTracker
                .Entries<HostOrder>()
                .Where(x => x.State is EntityState.Added or EntityState.Modified);

            foreach (var entry in entries)
            {
                entry.Entity.AuditedAt = Stamp.AddMinutes(_calls);
                Stamped++;
            }
        }
    }
}
