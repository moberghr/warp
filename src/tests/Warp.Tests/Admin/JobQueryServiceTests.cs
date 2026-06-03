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
}
