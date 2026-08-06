using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Services;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Observability;

[GenerateDatabaseTests]
public abstract class DashboardBreakdownTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected DashboardBreakdownTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static Job CreateJob(JobKind kind, State state, string queue = "default")
    {
        return new Job
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            CurrentState = state,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = queue,
        };
    }

    [TimedFact]
    public async Task GetWarpStatus_CountsMessagesByState()
    {
        // Arrange
        var ctx = _fixture.CreateContext();

        // 2 Enqueued messages
        ctx.Set<Job>().Add(CreateJob(JobKind.Message, State.Enqueued));
        ctx.Set<Job>().Add(CreateJob(JobKind.Message, State.Enqueued));

        // 1 Processing message
        ctx.Set<Job>().Add(CreateJob(JobKind.Message, State.Processing));

        // 1 Completed message
        ctx.Set<Job>().Add(CreateJob(JobKind.Message, State.Completed));

        // 1 Failed message
        ctx.Set<Job>().Add(CreateJob(JobKind.Message, State.Failed));

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new DashboardStatsService<TestContext>(_fixture.CreateContext(), TimeProvider.System, new Warp.Core.Metrics.LocalMetricSource<TestContext>(_fixture.CreateContext()));
        var status = await svc.GetWarpStatus();

        // Assert
        status.MessagesEnqueued.ShouldBe(2);
        status.MessagesProcessing.ShouldBe(1);
        status.MessagesCompleted.ShouldBe(1);
        status.MessagesFailed.ShouldBe(1);
    }

    [TimedFact]
    public async Task GetWarpStatus_Messages_CountsActiveStatesOnly()
    {
        // Arrange — one of every state, message kind
        var ctx = _fixture.CreateContext();

        ctx.Set<Job>().Add(CreateJob(JobKind.Message, State.Enqueued));
        ctx.Set<Job>().Add(CreateJob(JobKind.Message, State.Awaiting));
        ctx.Set<Job>().Add(CreateJob(JobKind.Message, State.Processing));
        ctx.Set<Job>().Add(CreateJob(JobKind.Message, State.Scheduled));
        ctx.Set<Job>().Add(CreateJob(JobKind.Message, State.Completed));
        ctx.Set<Job>().Add(CreateJob(JobKind.Message, State.Failed));
        ctx.Set<Job>().Add(CreateJob(JobKind.Message, State.Deleted));

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new DashboardStatsService<TestContext>(_fixture.CreateContext(), TimeProvider.System, new Warp.Core.Metrics.LocalMetricSource<TestContext>(_fixture.CreateContext()));
        var status = await svc.GetWarpStatus();

        // Assert — only Enqueued + Awaiting + Processing + Scheduled (saga timeouts) count as active
        status.Messages.ShouldBe(4);
    }

    [TimedFact]
    public async Task GetWarpStatus_Messages_ExcludesFailedAndCompleted()
    {
        // Repro of issue #177: seed-sagas leaves four failed, three completed, and three
        // scheduled message rows; only the scheduled ones should show in the navbar badge.
        var ctx = _fixture.CreateContext();

        for (var i = 0; i < 4; i++)
        {
            ctx.Set<Job>().Add(CreateJob(JobKind.Message, State.Failed));
        }

        for (var i = 0; i < 3; i++)
        {
            ctx.Set<Job>().Add(CreateJob(JobKind.Message, State.Completed));
        }

        for (var i = 0; i < 3; i++)
        {
            ctx.Set<Job>().Add(CreateJob(JobKind.Message, State.Scheduled));
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = new DashboardStatsService<TestContext>(_fixture.CreateContext(), TimeProvider.System, new Warp.Core.Metrics.LocalMetricSource<TestContext>(_fixture.CreateContext()));
        var status = await svc.GetWarpStatus();

        status.Messages.ShouldBe(3);
        status.MessagesFailed.ShouldBe(4);
        status.MessagesCompleted.ShouldBe(3);
    }

    [TimedFact]
    public async Task GetWarpStatus_CountsBatchesByState()
    {
        // Arrange
        var ctx = _fixture.CreateContext();

        // 1 Processing batch (children running)
        ctx.Set<Job>().Add(CreateJob(JobKind.Batch, State.Processing));

        // 1 Awaiting batch (continuation waiting for parent)
        ctx.Set<Job>().Add(CreateJob(JobKind.Batch, State.Awaiting));

        // 1 Completed batch
        ctx.Set<Job>().Add(CreateJob(JobKind.Batch, State.Completed));

        // 1 Failed batch
        ctx.Set<Job>().Add(CreateJob(JobKind.Batch, State.Failed));

        // 1 Deleted batch
        ctx.Set<Job>().Add(CreateJob(JobKind.Batch, State.Deleted));

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new DashboardStatsService<TestContext>(_fixture.CreateContext(), TimeProvider.System, new Warp.Core.Metrics.LocalMetricSource<TestContext>(_fixture.CreateContext()));
        var status = await svc.GetWarpStatus();

        // Assert
        status.BatchesProcessing.ShouldBe(1);
        status.BatchesAwaiting.ShouldBe(1);
        status.BatchesCompleted.ShouldBe(1);
        status.BatchesFailed.ShouldBe(1);
        status.BatchesDeleted.ShouldBe(1);
        status.Batches.ShouldBe(4); // excludes deleted
    }

    [TimedFact]
    public async Task GetWarpStatus_CountsJobStates()
    {
        // Arrange
        var ctx = _fixture.CreateContext();

        ctx.Set<Job>().Add(CreateJob(JobKind.Job, State.Enqueued));
        ctx.Set<Job>().Add(CreateJob(JobKind.Job, State.Enqueued));
        ctx.Set<Job>().Add(CreateJob(JobKind.Job, State.Processing));
        ctx.Set<Job>().Add(CreateJob(JobKind.Job, State.Completed));
        ctx.Set<Job>().Add(CreateJob(JobKind.Job, State.Failed));
        ctx.Set<Job>().Add(CreateJob(JobKind.Job, State.Awaiting));
        ctx.Set<Job>().Add(CreateJob(JobKind.Job, State.Deleted));

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new DashboardStatsService<TestContext>(_fixture.CreateContext(), TimeProvider.System, new Warp.Core.Metrics.LocalMetricSource<TestContext>(_fixture.CreateContext()));
        var status = await svc.GetWarpStatus();

        // Assert
        status.Total.ShouldBe(7);
        status.Created.ShouldBe(2); // Enqueued + not scheduled
        status.Processing.ShouldBe(1);
        status.Completed.ShouldBe(1);
        status.Failed.ShouldBe(1);
        status.Awaiting.ShouldBe(1);
        status.Deleted.ShouldBe(1);
    }

    [TimedFact]
    public async Task GetWarpStatus_ExcludesNonJobKindFromJobCounts()
    {
        // Arrange — only insert Message + Batch kinds, no Job kind
        var ctx = _fixture.CreateContext();

        ctx.Set<Job>().Add(CreateJob(JobKind.Message, State.Enqueued));
        ctx.Set<Job>().Add(CreateJob(JobKind.Message, State.Completed));
        ctx.Set<Job>().Add(CreateJob(JobKind.Batch, State.Awaiting));
        ctx.Set<Job>().Add(CreateJob(JobKind.Batch, State.Completed));

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new DashboardStatsService<TestContext>(_fixture.CreateContext(), TimeProvider.System, new Warp.Core.Metrics.LocalMetricSource<TestContext>(_fixture.CreateContext()));
        var status = await svc.GetWarpStatus();

        // Assert — job state counts should be zero because only Message/Batch kinds exist
        status.Total.ShouldBe(0);
        status.Created.ShouldBe(0);
        status.Processing.ShouldBe(0);
        status.Completed.ShouldBe(0);
        status.Failed.ShouldBe(0);
    }

    [TimedFact]
    public async Task GetWarpStatus_CombinesStatisticAndCounterValues()
    {
        // Arrange
        var ctx = _fixture.CreateContext();

        // Aggregated statistic
        ctx.Set<Statistic>().Add(new Statistic { Key = "stats:succeeded", Value = 10 });

        // Pending counter rows
        ctx.Set<Counter>().Add(new Counter { Key = "stats:succeeded", Value = 2 });
        ctx.Set<Counter>().Add(new Counter { Key = "stats:succeeded", Value = 1 });

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new DashboardStatsService<TestContext>(_fixture.CreateContext(), TimeProvider.System, new Warp.Core.Metrics.LocalMetricSource<TestContext>(_fixture.CreateContext()));
        var status = await svc.GetWarpStatus();

        // Assert — should combine Statistic(10) + Counter(2+1) = 13
        status.TotalSucceeded.ShouldBe(13);
    }
}
