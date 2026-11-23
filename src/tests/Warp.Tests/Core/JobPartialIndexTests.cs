using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Entities;

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

    private static List<string> Filters(TestContext context)
    {
        var filters = context.Model.FindEntityType(typeof(Job))!
            .GetIndexes()
            .Select(x => $"{string.Join(',', x.Properties.Select(y => y.Name))}: {x.GetFilter() ?? "(none)"}")
            .ToList();
        filters.Sort(StringComparer.Ordinal);

        return filters;
    }
}
