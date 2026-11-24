using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Core.Entities;
using Warp.Provider.PostgreSql;

namespace Warp.Tests.Core;

// Rule 6.15: the job table's state, parent and expiry indexes are partial, and their filters name the
// columns as the host's naming convention spells them. A filter naming a column that does not exist
// fails the schema creation (or the migration), so both spellings are pinned here.
[Trait("Category", "NoDb")]
public class JobPartialIndexTests
{
    [Fact]
    public void JobIndexes_WithoutANamingConvention_FilterOnThePascalCaseColumns()
    {
        var options = new DbContextOptionsBuilder<TestContext>()
            .UseSqlServer("Server=dummy")
            .Options;

        using var context = new TestContext(options);

        Filters(context).ShouldBe(
            [
                "CurrentState,ScheduleTime: \"CurrentState\" = 7",
                "ExpireAt: \"ExpireAt\" IS NOT NULL",
                "Kind,CurrentState,CreateTime: (none)",
                "Kind,CurrentState,Queue,ScheduleTime: \"CurrentState\" = 1",
                "ParentJobId,CurrentState: \"ParentJobId\" IS NOT NULL",
                "TraceId: (none)",
            ]);
    }

    [Fact]
    public void JobIndexes_WithTheSnakeCaseConvention_FilterOnTheSnakeCaseColumns()
    {
        var options = new DbContextOptionsBuilder<TestContext>()
            .UseNpgsql("Host=dummy")
            .UseSnakeCaseNamingConvention()
            .Options;

        using var context = new TestContext(options);

        Filters(context).ShouldBe(
            [
                "CurrentState,ScheduleTime: \"current_state\" = 7",
                "ExpireAt: \"expire_at\" IS NOT NULL",
                "Kind,CurrentState,CreateTime: (none)",
                "Kind,CurrentState,Queue,ScheduleTime: \"current_state\" = 1",
                "ParentJobId,CurrentState: \"parent_job_id\" IS NOT NULL",
                "TraceId: (none)",
            ]);
    }

    [Fact]
    public void JobIndexes_AnEntityConfiguratorRenamesAColumn_FilterOnTheNewName()
    {
        // The filters are SQL built from the column names. Built once inside ApplyWarpModel, a rename made
        // afterwards left a filter naming a column that does not exist, and the migration failed.
        var services = new ServiceCollection();
        services.AddDbContext<TestContext>(x => x.UseNpgsql("Host=dummy"));
        services.AddWarp<TestContext>(opt =>
        {
            opt.UsePostgreSql();
            opt.EntityConfigurators.Add((modelBuilder, _) => modelBuilder.Entity<Job>().Property(x => x.CurrentState).HasColumnName("job_state"));
        });
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var filters = Filters(scope.ServiceProvider.GetRequiredService<TestContext>());

        filters.ShouldContain("Kind,CurrentState,Queue,ScheduleTime: \"job_state\" = 1");
        filters.ShouldContain("CurrentState,ScheduleTime: \"job_state\" = 7");
    }

    [Fact]
    public void JobIndexes_TheHostRenamesAColumnAfterApplyWarpModel_FilterOnTheNewName()
    {
        var services = new ServiceCollection();
        services.AddDbContext<RenamingContext>(x => x.UseNpgsql("Host=dummy"));
        services.AddWarp<RenamingContext>(opt => opt.UsePostgreSql());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var filters = Filters(scope.ServiceProvider.GetRequiredService<RenamingContext>());

        filters.ShouldContain("ExpireAt: \"expires\" IS NOT NULL");
    }

    private static List<string> Filters(DbContext context)
    {
        var filters = context.Model.FindEntityType(typeof(Job))!
            .GetIndexes()
            .Select(x => $"{string.Join(',', x.Properties.Select(y => y.Name))}: {x.GetFilter() ?? "(none)"}")
            .ToList();
        filters.Sort(StringComparer.Ordinal);

        return filters;
    }
}

public sealed class RenamingContext(DbContextOptions<RenamingContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyWarpModel("warp");
        modelBuilder.Entity<Job>().Property(x => x.ExpireAt).HasColumnName("expires");
    }
}
