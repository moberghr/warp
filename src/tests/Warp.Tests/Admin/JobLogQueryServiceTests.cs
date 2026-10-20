using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Services;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Admin;

[GenerateDatabaseTests]
public abstract class JobLogQueryServiceTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected JobLogQueryServiceTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task GetHandlerLogs_MiddlePage_NewestFirstWithOlderPageFlagged()
    {
        var jobId = Guid.NewGuid();
        var otherJobId = Guid.NewGuid();
        var start = DateTime.UtcNow.AddMinutes(-1);
        var ctx = _fixture.CreateContext();
        ctx.Set<Job>().Add(CompletedJob(jobId));
        ctx.Set<Job>().Add(CompletedJob(otherJobId));
        for (var i = 0; i < 5; i++)
        {
            ctx.Set<JobLog>().Add(Log(jobId, "Log", start.AddSeconds(i), $"line {i}"));
        }

        ctx.Set<JobLog>().Add(Log(jobId, "Completed", start.AddSeconds(10), string.Empty));
        ctx.Set<JobLog>().Add(Log(otherJobId, "Log", start.AddSeconds(20), "other job"));
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = new JobLogQueryService<TestContext>(_fixture.CreateContext());
        var page = await svc.GetHandlerLogs(jobId, new BaseListRequest { Page = 1, PageSize = 2 });

        page.Items.Select(x => x.Message).ShouldBe(["line 2", "line 1"]);
        page.HasMore.ShouldBeTrue();
    }

    [TimedFact]
    public async Task GetHandlerLogs_LastPageExactlyFull_HasNoMore()
    {
        var jobId = Guid.NewGuid();
        var start = DateTime.UtcNow.AddMinutes(-1);
        var ctx = _fixture.CreateContext();
        ctx.Set<Job>().Add(CompletedJob(jobId));
        for (var i = 0; i < 4; i++)
        {
            ctx.Set<JobLog>().Add(Log(jobId, "Log", start.AddSeconds(i), $"line {i}"));
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = new JobLogQueryService<TestContext>(_fixture.CreateContext());
        var page = await svc.GetHandlerLogs(jobId, new BaseListRequest { Page = 1, PageSize = 2 });

        page.Items.Select(x => x.Message).ShouldBe(["line 1", "line 0"]);
        page.HasMore.ShouldBeFalse();
    }

    [TimedFact]
    public async Task GetHandlerLogs_EqualTimestampsAcrossPages_EveryRowAppearsOnce()
    {
        var jobId = Guid.NewGuid();
        var timestamp = DateTime.UtcNow.AddMinutes(-1);
        var ctx = _fixture.CreateContext();
        ctx.Set<Job>().Add(CompletedJob(jobId));
        for (var i = 0; i < 6; i++)
        {
            ctx.Set<JobLog>().Add(Log(jobId, "Log", timestamp, $"line {i}"));
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var seen = new List<string>();
        for (var page = 0; page < 3; page++)
        {
            var svc = new JobLogQueryService<TestContext>(_fixture.CreateContext());
            var result = await svc.GetHandlerLogs(jobId, new BaseListRequest { Page = page, PageSize = 2 });
            seen.AddRange(result.Items.Select(x => x.Message));
        }

        seen.Order(StringComparer.Ordinal).ShouldBe(Enumerable.Range(0, 6).Select(x => $"line {x}"));
    }

    [TimedFact]
    public async Task GetHandlerLogs_OutOfRangePaging_ClampedToValidPage()
    {
        var jobId = Guid.NewGuid();
        var start = DateTime.UtcNow.AddMinutes(-10);
        var ctx = _fixture.CreateContext();
        ctx.Set<Job>().Add(CompletedJob(jobId));
        for (var i = 0; i < JobLogQueryService<TestContext>.MaxPageSize + 1; i++)
        {
            ctx.Set<JobLog>().Add(Log(jobId, "Log", start.AddMilliseconds(i), $"line {i}"));
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = new JobLogQueryService<TestContext>(_fixture.CreateContext());
        var oversized = await svc.GetHandlerLogs(jobId, new BaseListRequest { Page = -1, PageSize = 1_000_000 });
        var zeroSize = await svc.GetHandlerLogs(jobId, new BaseListRequest { Page = 0, PageSize = 0 });

        oversized.Page.ShouldBe(0);
        oversized.PageSize.ShouldBe(JobLogQueryService<TestContext>.MaxPageSize);
        oversized.Items.Count.ShouldBe(JobLogQueryService<TestContext>.MaxPageSize);
        oversized.Items[0].Message.ShouldBe($"line {JobLogQueryService<TestContext>.MaxPageSize}");
        oversized.HasMore.ShouldBeTrue();
        zeroSize.PageSize.ShouldBe(1);
        zeroSize.Items.Count.ShouldBe(1);
    }

    [TimedFact]
    public async Task GetProgress_ProgressSeries_ReturnsLatestValuePerBarOrderedByName()
    {
        var jobId = Guid.NewGuid();
        var otherJobId = Guid.NewGuid();
        var start = DateTime.UtcNow.AddMinutes(-1);
        var ctx = _fixture.CreateContext();
        ctx.Set<Job>().Add(CompletedJob(jobId));
        ctx.Set<Job>().Add(CompletedJob(otherJobId));
        for (var i = 1; i <= 3; i++)
        {
            ctx.Set<JobLog>().Add(Progress(jobId, "render", start.AddSeconds(i), (short)(i * 10)));
            ctx.Set<JobLog>().Add(Progress(jobId, string.Empty, start.AddSeconds(i), (short)(i * 30)));
        }

        ctx.Set<JobLog>().Add(Progress(otherJobId, "render", start.AddSeconds(10), 99));
        ctx.Set<JobLog>().Add(Log(jobId, "Log", start.AddSeconds(10), "handler output"));
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = new JobLogQueryService<TestContext>(_fixture.CreateContext());
        var bars = await svc.GetProgress(jobId);

        bars.Select(x => (x.Name, x.Value)).ShouldBe([(string.Empty, (short)90), ("render", (short)30)]);
        bars[1].UpdatedAt.ShouldBe(start.AddSeconds(3), TimeSpan.FromMilliseconds(1));
    }

    [TimedFact]
    public async Task GetProgress_NullAndEmptyBarName_ReturnsOneUnnamedBar()
    {
        var jobId = Guid.NewGuid();
        var start = DateTime.UtcNow.AddMinutes(-1);
        var ctx = _fixture.CreateContext();
        ctx.Set<Job>().Add(CompletedJob(jobId));
        ctx.Set<JobLog>().Add(Progress(jobId, null, start, 20));
        ctx.Set<JobLog>().Add(Progress(jobId, string.Empty, start.AddSeconds(1), 60));
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = new JobLogQueryService<TestContext>(_fixture.CreateContext());
        var bars = await svc.GetProgress(jobId);

        var bar = bars.ShouldHaveSingleItem();
        bar.Name.ShouldBe(string.Empty);
        bar.Value.ShouldBe((short)60);
    }

    [TimedFact]
    public async Task GetProgress_NoProgressReported_ReturnsEmpty()
    {
        var jobId = Guid.NewGuid();
        var ctx = _fixture.CreateContext();
        ctx.Set<Job>().Add(CompletedJob(jobId));
        ctx.Set<JobLog>().Add(Log(jobId, "Log", DateTime.UtcNow, "handler output"));
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = new JobLogQueryService<TestContext>(_fixture.CreateContext());
        var bars = await svc.GetProgress(jobId);

        bars.ShouldBeEmpty();
    }

    private static Job CompletedJob(Guid id)
    {
        return new Job
        {
            Id = id,
            Kind = JobKind.Job,
            CurrentState = State.Completed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        };
    }

    private static JobLog Log(Guid jobId, string eventType, DateTime timestamp, string message)
    {
        return new JobLog
        {
            Id = Guid.NewGuid(),
            JobId = jobId,
            EventType = eventType,
            Timestamp = timestamp,
            Level = "Information",
            Message = message,
        };
    }

    private static JobLog Progress(Guid jobId, string? name, DateTime timestamp, short value)
    {
        return new JobLog
        {
            Id = Guid.NewGuid(),
            JobId = jobId,
            EventType = "Progress",
            Name = name,
            Value = value,
            Timestamp = timestamp,
            Level = "Information",
            Message = string.Empty,
        };
    }
}
