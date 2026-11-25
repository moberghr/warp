using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Shouldly;
using Warp.Core.Data;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Core;

/// <summary>
/// The providers' <c>RecreateWarpJobIndexesOnline</c> against a real database: a job table that still has
/// the four indexes 7.7 created (whole, no filter) and holds rows, the helper's statements run one by one
/// outside a transaction as the migration runs them, and the indexes end up partial under their own names
/// with every row still there.
/// </summary>
[GenerateDatabaseTests]
public abstract class OnlineJobIndexMigrationTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected OnlineJobIndexMigrationTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task RecreateWarpJobIndexesOnline_PopulatedTableWithThe77Indexes_LeavesThemPartialAndTheRowsInPlace()
    {
        // Arrange: rows in every state the filters split on, and the indexes as 7.7 left them.
        await using (var seed = _fixture.CreateContext())
        {
            for (var i = 0; i < 200; i++)
            {
                seed.Set<Job>().Add(new Job
                {
                    Id = Guid.NewGuid(),
                    Kind = JobKind.Job,
                    CurrentState = (i % 3) switch { 0 => State.Enqueued, 1 => State.Scheduled, _ => State.Completed },
                    Type = "test",
                    Message = "{}",
                    CreateTime = DateTime.UtcNow,
                    ScheduleTime = DateTime.UtcNow,
                    ExpireAt = i % 2 == 0 ? DateTime.UtcNow.AddDays(1) : null,
                    Queue = "default",
                });
            }

            await seed.SaveChangesAsync(Ct);
        }

        await using var context = _fixture.CreateContext();
        var sqlServer = IsSqlServer(context);
        var indexes = WarpJobIndexes.FindPartial(context.Model);
        indexes.Count.ShouldBe(4);
        foreach (var index in indexes)
        {
            await context.Database.ExecuteSqlRawAsync(WholeIndexSql(index, sqlServer), Ct);
        }

        (await FilteredAsync(context, indexes, sqlServer)).ShouldBeEmpty("the arrange should have left whole indexes");

        // Act: the migration's operations, each on its own, outside any transaction.
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName);
        if (sqlServer)
        {
            Warp.Provider.SqlServer.WarpMigrationBuilderExtensions.RecreateWarpJobIndexesOnline(migrationBuilder, context.Model);
        }
        else
        {
            Warp.Provider.PostgreSql.WarpMigrationBuilderExtensions.RecreateWarpJobIndexesOnline(migrationBuilder, context.Model);
        }

        foreach (var operation in migrationBuilder.Operations.Cast<SqlOperation>())
        {
            operation.SuppressTransaction.ShouldBeTrue();
            await context.Database.ExecuteSqlRawAsync(operation.Sql, Ct);
        }

        // Assert
        (await FilteredAsync(context, indexes, sqlServer)).ShouldBe([.. indexes.Select(x => x.Name).Order(StringComparer.Ordinal)]);
        (await context.Set<Job>().CountAsync(Ct)).ShouldBe(200);
        (await context.Set<Job>().Where(x => x.CurrentState == State.Enqueued).CountAsync(Ct)).ShouldBe(67);
        if (!sqlServer)
        {
            (await context.Database.SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM pg_indexes WHERE indexname LIKE '%warp_new'").SingleAsync(Ct))
                .ShouldBe(0, "no temporary index may be left behind");
        }
    }

    private static bool IsSqlServer(DbContext context) =>
        context.Database.ProviderName?.Contains("SqlServer", StringComparison.Ordinal) == true;

    // The index as 7.7 created it: same name and columns, no filter.
    private static string WholeIndexSql(WarpJobIndex index, bool sqlServer)
    {
        if (sqlServer)
        {
            var table = $"[{index.Schema}].[{index.Table}]";
            var columns = string.Join(", ", index.Columns.Select(x => $"[{x}]"));

            return $"DROP INDEX [{index.Name}] ON {table}; CREATE INDEX [{index.Name}] ON {table} ({columns});";
        }

        var pgColumns = string.Join(", ", index.Columns.Select(x => $"\"{x}\""));

        return $"DROP INDEX \"{index.Schema}\".\"{index.Name}\"; CREATE INDEX \"{index.Name}\" ON \"{index.Schema}\".\"{index.Table}\" ({pgColumns});";
    }

    // The names of those of the given indexes that exist with a filter, in name order.
    private static async Task<List<string>> FilteredAsync(DbContext context, IReadOnlyList<WarpJobIndex> indexes, bool sqlServer)
    {
        var sql = sqlServer
            ? "SELECT i.name AS [Value] FROM sys.indexes i WHERE i.object_id = OBJECT_ID(N'[warp].[Job]') AND i.has_filter = 1"
            : "SELECT indexname AS \"Value\" FROM pg_indexes WHERE schemaname = 'warp' AND indexdef LIKE '% WHERE %'";
        var names = indexes.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var filtered = await context.Database.SqlQueryRaw<string>(sql).ToListAsync(Ct);

        return [.. filtered.Where(names.Contains).Order(StringComparer.Ordinal)];
    }
}
