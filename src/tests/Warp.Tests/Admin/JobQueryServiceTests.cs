using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Services;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Admin;

[GenerateDatabaseTests]
public abstract class JobQueryServiceTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected JobQueryServiceTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task GetJobsList_ReturnsJobsByState()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        for (var i = 0; i < 3; i++)
        {
            ctx.Set<Job>().Add(new Job
            {
                Id = Guid.NewGuid(),
                Kind = JobKind.Job,
                CurrentState = State.Completed,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
            });
        }

        for (var i = 0; i < 2; i++)
        {
            ctx.Set<Job>().Add(new Job
            {
                Id = Guid.NewGuid(),
                Kind = JobKind.Job,
                CurrentState = State.Failed,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
            });
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new JobQueryService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var result = await svc.GetJobsList(new BaseListRequest { Page = 0, PageSize = 20 }, State.Completed);

        // Assert
        result.TotalCount.ShouldBe(3);
    }

    [TimedFact]
    public async Task GetScheduledJobs_ReturnsScheduledStateOnly()
    {
        // Arrange
        var ctx = _fixture.CreateContext();

        // Future-dated job in Scheduled state (new routing via JobHelper)
        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Scheduled,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow.AddHours(2),
            Queue = "default",
        });

        // Enqueued job (immediately runnable, not scheduled)
        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow.AddHours(-1),
            Queue = "default",
        });

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new JobQueryService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var result = await svc.GetScheduledJobs(new BaseListRequest { Page = 0, PageSize = 20 });

        // Assert
        result.TotalCount.ShouldBe(1);
    }

    [TimedFact]
    public async Task GetAwaitingJobs_ReturnsOnlyAwaiting()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Awaiting,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new JobQueryService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var result = await svc.GetAwaitingJobs(new BaseListRequest { Page = 0, PageSize = 20 });

        // Assert
        result.TotalCount.ShouldBe(1);
    }

    [TimedFact]
    public async Task GetSiblingJobs_ReturnsOtherChildrenOfSameParent()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var parentId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = parentId,
            Kind = JobKind.Message,
            CurrentState = State.Processing,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });

        var child1Id = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = child1Id,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            ParentJobId = parentId,
        });

        for (var i = 0; i < 2; i++)
        {
            ctx.Set<Job>().Add(new Job
            {
                Id = Guid.NewGuid(),
                Kind = JobKind.Job,
                CurrentState = State.Enqueued,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
                ParentJobId = parentId,
            });
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new JobQueryService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var result = await svc.GetSiblingJobs(child1Id, new BaseListRequest { Page = 0, PageSize = 20 });

        // Assert
        result.TotalCount.ShouldBe(2);
    }

    [TimedFact]
    public async Task GetChildJobs_ReturnsDirectChildren()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var parentId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = parentId,
            Kind = JobKind.Batch,
            CurrentState = State.Awaiting,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });

        for (var i = 0; i < 2; i++)
        {
            ctx.Set<Job>().Add(new Job
            {
                Id = Guid.NewGuid(),
                Kind = JobKind.Job,
                CurrentState = State.Enqueued,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
                ParentJobId = parentId,
            });
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new JobQueryService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var result = await svc.GetChildJobs(parentId, new BaseListRequest { Page = 0, PageSize = 20 });

        // Assert
        result.TotalCount.ShouldBe(2);
    }

    [TimedFact]
    public async Task GetTraceJobs_ReturnsJobsWithSameTraceId()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var traceId = Guid.NewGuid();
        var job1Id = Guid.NewGuid();

        ctx.Set<Job>().Add(new Job
        {
            Id = job1Id,
            Kind = JobKind.Job,
            CurrentState = State.Completed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            TraceId = traceId,
        });

        for (var i = 0; i < 2; i++)
        {
            ctx.Set<Job>().Add(new Job
            {
                Id = Guid.NewGuid(),
                Kind = JobKind.Job,
                CurrentState = State.Completed,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
                TraceId = traceId,
            });
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new JobQueryService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var result = await svc.GetTraceJobs(job1Id, new BaseListRequest { Page = 0, PageSize = 20 });

        // Assert
        result.TotalCount.ShouldBe(2);
    }

    /// <summary>
    /// BUG: JobModel doesn't include HandlerType. Job lists should show handler info.
    /// </summary>
    [TimedFact]
    public async Task GetJobsList_IncludesHandlerType()
    {
        // Arrange: create a job with a handler type
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Completed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            Type = "MyApp.Jobs.SendEmail, MyApp",
            HandlerType = "MyApp.Handlers.SendEmailHandler, MyApp",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new JobQueryService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var result = await svc.GetJobsList(new BaseListRequest { Page = 0, PageSize = 20 }, State.Completed);

        // Assert
        result.Items.Count.ShouldBe(1);
        result.Items[0].HandlerType.ShouldBe("MyApp.Handlers.SendEmailHandler, MyApp");
    }

    [TimedFact]
    public async Task GetJobsList_OrdersCompletedByFinishedTimeDescending()
    {
        // Three Completed jobs with finished-time logs in reverse order of CreateTime.
        // After sort, the latest *finish* (oldJobLatestFinish) should be first, even
        // though the job was created earliest.
        var ctx = _fixture.CreateContext();
        var now = new DateTime(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc);

        var oldJobLatestFinish = Guid.NewGuid();
        var midJobMidFinish = Guid.NewGuid();
        var newJobEarliestFinish = Guid.NewGuid();

        ctx.Set<Job>().Add(new Job { Id = oldJobLatestFinish, Kind = JobKind.Job, CurrentState = State.Completed, CreateTime = now, ScheduleTime = now, Queue = "default" });
        ctx.Set<Job>().Add(new Job { Id = midJobMidFinish, Kind = JobKind.Job, CurrentState = State.Completed, CreateTime = now.AddMinutes(1), ScheduleTime = now.AddMinutes(1), Queue = "default" });
        ctx.Set<Job>().Add(new Job { Id = newJobEarliestFinish, Kind = JobKind.Job, CurrentState = State.Completed, CreateTime = now.AddMinutes(2), ScheduleTime = now.AddMinutes(2), Queue = "default" });

        ctx.Set<JobLog>().Add(new JobLog { Id = Guid.NewGuid(), JobId = newJobEarliestFinish, EventType = "Completed", Timestamp = now.AddHours(1), Level = "Information" });
        ctx.Set<JobLog>().Add(new JobLog { Id = Guid.NewGuid(), JobId = midJobMidFinish, EventType = "Completed", Timestamp = now.AddHours(2), Level = "Information" });
        ctx.Set<JobLog>().Add(new JobLog { Id = Guid.NewGuid(), JobId = oldJobLatestFinish, EventType = "Completed", Timestamp = now.AddHours(3), Level = "Information" });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = new JobQueryService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var result = await svc.GetJobsList(new BaseListRequest { Page = 0, PageSize = 20 }, State.Completed);

        result.Items.Count.ShouldBe(3);
        result.Items[0].Id.ShouldBe(oldJobLatestFinish);
        result.Items[1].Id.ShouldBe(midJobMidFinish);
        result.Items[2].Id.ShouldBe(newJobEarliestFinish);
    }

    [TimedFact]
    public async Task GetJobsList_NonTerminalState_OrdersByCreateTimeDescending()
    {
        // Non-terminal states (Enqueued/Processing/Scheduled/Awaiting) go through the
        // OrderByCreateTimeDescending path — plain ORDER BY create_time DESC, no JobLog
        // subquery. The terminal-state subquery path is exercised in
        // GetJobsList_OrdersCompletedByFinishedTimeDescending and the new
        // GetFailedJobsByType_OrdersByFinishedTimeDescending.
        var ctx = _fixture.CreateContext();
        var now = new DateTime(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc);

        var oldest = Guid.NewGuid();
        var middle = Guid.NewGuid();
        var newest = Guid.NewGuid();

        ctx.Set<Job>().Add(new Job { Id = oldest, Kind = JobKind.Job, CurrentState = State.Enqueued, CreateTime = now, ScheduleTime = now, Queue = "default" });
        ctx.Set<Job>().Add(new Job { Id = middle, Kind = JobKind.Job, CurrentState = State.Enqueued, CreateTime = now.AddMinutes(1), ScheduleTime = now.AddMinutes(1), Queue = "default" });
        ctx.Set<Job>().Add(new Job { Id = newest, Kind = JobKind.Job, CurrentState = State.Enqueued, CreateTime = now.AddMinutes(2), ScheduleTime = now.AddMinutes(2), Queue = "default" });

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = new JobQueryService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var result = await svc.GetJobsList(new BaseListRequest { Page = 0, PageSize = 20 }, State.Enqueued);

        result.Items.Count.ShouldBe(3);
        result.Items[0].Id.ShouldBe(newest);
        result.Items[1].Id.ShouldBe(middle);
        result.Items[2].Id.ShouldBe(oldest);
    }

    [TimedFact]
    public async Task GetJobStatesInProcess_OrdersByCreateTimeDescending()
    {
        // Processing has its own query method (GetJobStatesInProcess) that bypasses
        // GetJobsByState — sort that path explicitly too. Plain CreateTime DESC, no
        // JobLog subquery (Processing is non-terminal).
        var ctx = _fixture.CreateContext();
        var now = new DateTime(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc);

        var oldest = Guid.NewGuid();
        var middle = Guid.NewGuid();
        var newest = Guid.NewGuid();

        ctx.Set<Job>().Add(new Job { Id = oldest, Kind = JobKind.Job, CurrentState = State.Processing, CreateTime = now, ScheduleTime = now, Queue = "default" });
        ctx.Set<Job>().Add(new Job { Id = middle, Kind = JobKind.Job, CurrentState = State.Processing, CreateTime = now.AddMinutes(1), ScheduleTime = now.AddMinutes(1), Queue = "default" });
        ctx.Set<Job>().Add(new Job { Id = newest, Kind = JobKind.Job, CurrentState = State.Processing, CreateTime = now.AddMinutes(2), ScheduleTime = now.AddMinutes(2), Queue = "default" });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = new JobQueryService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var result = await svc.GetJobStatesInProcess(new BaseListRequest { Page = 0, PageSize = 20 });

        result.Items.Count.ShouldBe(3);
        result.Items[0].Id.ShouldBe(newest);
        result.Items[1].Id.ShouldBe(middle);
        result.Items[2].Id.ShouldBe(oldest);
    }

    [TimedFact]
    public async Task GetFailedJobsByType_OrdersByFinishedTimeDescending()
    {
        // Failed-by-type goes through OrderByFinishedTimeDescending (Failed is terminal).
        // Mirror GetJobsList_OrdersCompletedByFinishedTimeDescending: jobs with reverse
        // CreateTime vs finished time, assert the sort follows finished time.
        var ctx = _fixture.CreateContext();
        var now = new DateTime(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc);
        const string typeName = "MyApp.Jobs.ChargeCard, MyApp";

        var oldJobLatestFailure = Guid.NewGuid();
        var midJobMidFailure = Guid.NewGuid();
        var newJobEarliestFailure = Guid.NewGuid();

        ctx.Set<Job>().Add(new Job { Id = oldJobLatestFailure, Kind = JobKind.Job, CurrentState = State.Failed, CreateTime = now, ScheduleTime = now, Queue = "default", Type = typeName });
        ctx.Set<Job>().Add(new Job { Id = midJobMidFailure, Kind = JobKind.Job, CurrentState = State.Failed, CreateTime = now.AddMinutes(1), ScheduleTime = now.AddMinutes(1), Queue = "default", Type = typeName });
        ctx.Set<Job>().Add(new Job { Id = newJobEarliestFailure, Kind = JobKind.Job, CurrentState = State.Failed, CreateTime = now.AddMinutes(2), ScheduleTime = now.AddMinutes(2), Queue = "default", Type = typeName });

        ctx.Set<JobLog>().Add(new JobLog { Id = Guid.NewGuid(), JobId = newJobEarliestFailure, EventType = "Failed", Timestamp = now.AddHours(1), Level = "Error" });
        ctx.Set<JobLog>().Add(new JobLog { Id = Guid.NewGuid(), JobId = midJobMidFailure, EventType = "Failed", Timestamp = now.AddHours(2), Level = "Error" });
        ctx.Set<JobLog>().Add(new JobLog { Id = Guid.NewGuid(), JobId = oldJobLatestFailure, EventType = "Failed", Timestamp = now.AddHours(3), Level = "Error" });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = new JobQueryService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var result = await svc.GetFailedJobsByType(new BaseListRequest { Page = 0, PageSize = 20 }, typeName);

        result.Items.Count.ShouldBe(3);
        result.Items[0].Id.ShouldBe(oldJobLatestFailure);
        result.Items[1].Id.ShouldBe(midJobMidFailure);
        result.Items[2].Id.ShouldBe(newJobEarliestFailure);
    }

    /// <summary>
    /// The retrying listing keys on the PRESENCE of the <c>RetriedTimes</c> metadata key, so the rows
    /// that matter are the near misses: a job carrying a retry POLICY it has never spent
    /// (<c>StampRetry</c> writes <c>MaxRetries</c>/<c>RetryDelays</c> before the first attempt, so most
    /// of the backlog looks like this), and a key that merely ends in the same word. Both must stay out,
    /// or the tab becomes "jobs that could retry" instead of "jobs that are retrying".
    /// </summary>
    [TimedFact]
    public async Task GetRetryingJobs_ReturnsOnlyWaitingJobsThatHaveSpentAnAttempt()
    {
        var ctx = _fixture.CreateContext();
        var now = DateTime.UtcNow;

        var retryingScheduled = Guid.NewGuid();
        var retryingEnqueued = Guid.NewGuid();

        ctx.Set<Job>().Add(new Job { Id = retryingScheduled, Kind = JobKind.Job, CurrentState = State.Scheduled, CreateTime = now, ScheduleTime = now.AddMinutes(1), Queue = "default", Metadata = """{"MaxRetries":3,"RetryDelays":[15,60,300],"RetriedTimes":2}""" });
        ctx.Set<Job>().Add(new Job { Id = retryingEnqueued, Kind = JobKind.Job, CurrentState = State.Enqueued, CreateTime = now, ScheduleTime = now, Queue = "default", Metadata = """{"RetriedTimes":1}""" });

        // Has a retry budget but has never used it.
        ctx.Set<Job>().Add(new Job { Id = Guid.NewGuid(), Kind = JobKind.Job, CurrentState = State.Scheduled, CreateTime = now, ScheduleTime = now.AddMinutes(1), Queue = "default", Metadata = """{"MaxRetries":3,"RetryDelays":[15,60,300]}""" });

        // A user metadata key that ends in the same word — the quoted token must not match it.
        ctx.Set<Job>().Add(new Job { Id = Guid.NewGuid(), Kind = JobKind.Job, CurrentState = State.Scheduled, CreateTime = now, ScheduleTime = now.AddMinutes(1), Queue = "default", Metadata = """{"LastRetriedTimes":4}""" });

        ctx.Set<Job>().Add(new Job { Id = Guid.NewGuid(), Kind = JobKind.Job, CurrentState = State.Scheduled, CreateTime = now, ScheduleTime = now.AddMinutes(1), Queue = "default", Metadata = null });

        // Settled: retried in the past, but no longer waiting on an attempt.
        ctx.Set<Job>().Add(new Job { Id = Guid.NewGuid(), Kind = JobKind.Job, CurrentState = State.Failed, CreateTime = now, ScheduleTime = now, Queue = "default", Metadata = """{"RetriedTimes":3}""" });
        ctx.Set<Job>().Add(new Job { Id = Guid.NewGuid(), Kind = JobKind.Job, CurrentState = State.Completed, CreateTime = now, ScheduleTime = now, Queue = "default", Metadata = """{"RetriedTimes":1}""" });

        // Messages are routed, not executed — the jobs listing is Kind=Job only.
        ctx.Set<Job>().Add(new Job { Id = Guid.NewGuid(), Kind = JobKind.Message, CurrentState = State.Scheduled, CreateTime = now, ScheduleTime = now.AddMinutes(1), Queue = "default", Metadata = """{"RetriedTimes":1}""" });

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = new JobQueryService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var result = await svc.GetRetryingJobs(new BaseListRequest { Page = 0, PageSize = 20 });

        result.TotalCount.ShouldBe(2);
        result.Items.Select(x => x.Id).ShouldBe([retryingScheduled, retryingEnqueued], ignoreOrder: true);
    }

    [TimedFact]
    public async Task GetRetryingJobs_ProjectsRetryCountWithoutLeakingMetadata()
    {
        var ctx = _fixture.CreateContext();
        var now = DateTime.UtcNow;

        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Scheduled,
            CreateTime = now,
            ScheduleTime = now.AddMinutes(1),
            Queue = "default",

            // ConcurrencyKey is the §1.2 case: it rides the same dictionary and must not reach the model.
            Metadata = """{"ConcurrencyKey":"tenant-42","MaxRetries":5,"RetriedTimes":2}""",
        });

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = new JobQueryService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var result = await svc.GetRetryingJobs(new BaseListRequest { Page = 0, PageSize = 20 });

        var job = result.Items.ShouldHaveSingleItem();
        job.RetryCount.ShouldBe(2);
        job.Message.ShouldBeNull();
    }

    /// <summary>
    /// The badge count must agree with the listing exactly — they share one filter precisely so a
    /// future change cannot move the list without moving the number beside it.
    /// </summary>
    [TimedFact]
    public async Task CountRetryingJobs_MatchesTheListing()
    {
        var ctx = _fixture.CreateContext();
        var now = DateTime.UtcNow;

        ctx.Set<Job>().Add(new Job { Id = Guid.NewGuid(), Kind = JobKind.Job, CurrentState = State.Scheduled, CreateTime = now, ScheduleTime = now.AddMinutes(1), Queue = "default", Metadata = """{"RetriedTimes":1}""" });
        ctx.Set<Job>().Add(new Job { Id = Guid.NewGuid(), Kind = JobKind.Job, CurrentState = State.Enqueued, CreateTime = now, ScheduleTime = now, Queue = "default", Metadata = """{"RetriedTimes":4}""" });
        ctx.Set<Job>().Add(new Job { Id = Guid.NewGuid(), Kind = JobKind.Job, CurrentState = State.Scheduled, CreateTime = now, ScheduleTime = now.AddMinutes(1), Queue = "default", Metadata = """{"MaxRetries":3}""" });
        ctx.Set<Job>().Add(new Job { Id = Guid.NewGuid(), Kind = JobKind.Job, CurrentState = State.Failed, CreateTime = now, ScheduleTime = now, Queue = "default", Metadata = """{"RetriedTimes":3}""" });

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = new JobQueryService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var count = await svc.CountRetryingJobs();

        count.ShouldBe(2);
    }
}
