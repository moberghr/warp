using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Warp.Core;
using Warp.Core.Entities;
using Warp.Provider.PostgreSql;

namespace Warp.Tests.Core;

// Warp's time columns must be timestamp with time zone on PostgreSQL: 8.0 compares them with the database's
// now(), and under Npgsql's legacy timestamp mode those comparisons depend on the session time zone. A
// process refuses to start on such a model. Legacy mode itself is a process-wide AppContext switch Npgsql
// reads once, so these tests build the model it produces by hand (a Warp DateTime retyped as
// timestamp without time zone) rather than flip the switch for the whole test run.
[Trait("Category", "NoDb")]
public class PostgresTimestampCheckTests
{
    [Fact]
    public void FindLegacyColumns_TheDefaultNpgsqlModel_FindsNone()
    {
        var options = new DbContextOptionsBuilder<TestContext>()
            .UseNpgsql("Host=dummy")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var context = new TestContext(options);

        PostgresTimestampCheck.FindLegacyColumns(context.Model).ShouldBeEmpty();
    }

    [Fact]
    public void Ensure_AWarpTimeColumnWithoutTimeZone_ThrowsNamingItAndTheSwitch()
    {
        var options = new DbContextOptionsBuilder<LegacyTimestampContext>()
            .UseNpgsql("Host=dummy")
            .Options;
        using var context = new LegacyTimestampContext(options);

        var exception = Should.Throw<InvalidOperationException>(() => PostgresTimestampCheck.Ensure(context.Model));

        exception.Message.ShouldContain("Job.ScheduleTime (timestamp without time zone)");
        exception.Message.ShouldContain("Npgsql.EnableLegacyTimestampBehavior");
    }

    [Fact]
    public async Task UsePostgreSql_ModelWithALegacyTimeColumn_TheHostRefusesToStart()
    {
        // Registered by the provider for every process: a publisher writes the times a server compares.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<LegacyTimestampContext>(x => x.UseNpgsql("Host=dummy"));
        services.AddWarp<LegacyTimestampContext>(opt => opt.UsePostgreSql());
        await using var provider = services.BuildServiceProvider();
        var check = provider.GetServices<IHostedService>().OfType<PostgresTimestampCheckService<LegacyTimestampContext>>().ShouldHaveSingleItem();

        await Should.ThrowAsync<InvalidOperationException>(() => check.StartAsync(Xunit.TestContext.Current.CancellationToken));
    }
}

// The model Npgsql's legacy timestamp mode produces for a Warp time column.
public sealed class LegacyTimestampContext(DbContextOptions<LegacyTimestampContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyWarpModel("warp");
        modelBuilder.Entity<Job>().Property(x => x.ScheduleTime).HasColumnType("timestamp without time zone");
    }
}
