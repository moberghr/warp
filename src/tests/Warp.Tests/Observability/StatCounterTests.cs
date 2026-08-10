using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Services;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Observability;

[GenerateDatabaseTests]
public abstract class StatCounterTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected StatCounterTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task DeleteJob_FromCompletedState_DoesNotDecrementSucceededCounter()
    {
        // Arrange
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
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        await svc.DeleteJob(jobId);

        // Assert — deleting a completed job does NOT rewrite stats:succeeded. The job did succeed; a later
        // delete does not un-happen it. Current state comes from querying Job, not from a metric.
        var readCtx = _fixture.CreateContext();
        var counters = await readCtx.Set<Counter>().ToListAsync(Xunit.TestContext.Current.CancellationToken);

        AssertAppendOnly(counters);
        Sum(counters, "stats:succeeded").ShouldBe(0);
    }

    [TimedFact]
    public async Task DeleteJob_FromFailedState_DoesNotDecrementFailedCounter()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Failed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        await svc.DeleteJob(jobId);

        // Assert
        var readCtx = _fixture.CreateContext();
        var counters = await readCtx.Set<Counter>().ToListAsync(Xunit.TestContext.Current.CancellationToken);

        AssertAppendOnly(counters);
        Sum(counters, "stats:failed").ShouldBe(0);
    }

    [TimedFact]
    public async Task DeleteJob_FromDeletedState_NoOp()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Deleted,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            ExpireAt = DateTime.UtcNow.AddDays(1),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        await svc.DeleteJob(jobId);

        // Assert — no counter rows should be created because it was already Deleted
        var readCtx = _fixture.CreateContext();
        var counterCount = await readCtx.Set<Counter>().CountAsync(Xunit.TestContext.Current.CancellationToken);
        counterCount.ShouldBe(0);
    }

    [TimedFact]
    public async Task DeleteJob_AddsDeletedCounter()
    {
        // Arrange
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
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        await svc.DeleteJob(jobId);

        // Assert
        var readCtx = _fixture.CreateContext();
        var deletedCounterSum = await readCtx.Set<Counter>()
            .Where(c => c.Key == "stats:deleted")
            .SumAsync(c => c.Value, Xunit.TestContext.Current.CancellationToken);
        deletedCounterSum.ShouldBe(1);
    }

    [TimedFact]
    public async Task RequeueJob_FromCompletedState_DoesNotDecrementSucceededCounter()
    {
        // Arrange
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
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        await svc.RequeueJob(jobId);

        // Assert
        var readCtx = _fixture.CreateContext();
        var counters = await readCtx.Set<Counter>().ToListAsync(Xunit.TestContext.Current.CancellationToken);

        AssertAppendOnly(counters);
        Sum(counters, "stats:succeeded").ShouldBe(0);
    }

    [TimedFact]
    public async Task RequeueJob_FromFailedState_DoesNotDecrementFailedCounter()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Failed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        await svc.RequeueJob(jobId);

        // Assert
        var readCtx = _fixture.CreateContext();
        var counters = await readCtx.Set<Counter>().ToListAsync(Xunit.TestContext.Current.CancellationToken);

        AssertAppendOnly(counters);
        Sum(counters, "stats:failed").ShouldBe(0);
    }

    [TimedFact]
    public async Task RequeueJob_AlreadyEnqueued_NoOp()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        await svc.RequeueJob(jobId);

        // Assert — no counter rows should be created because it was already Enqueued
        var readCtx = _fixture.CreateContext();
        var counterCount = await readCtx.Set<Counter>().CountAsync(Xunit.TestContext.Current.CancellationToken);
        counterCount.ShouldBe(0);
    }

    /// <summary>
    /// The append-only rule (RSC4) is "no negative <see cref="Counter"/> row is ever written", which is
    /// strictly stronger than the sum being 0 — a compensating <c>+1 / -1</c> pair sums to 0 too, and that
    /// pair is exactly the shape being removed. Asserting only the sum would let the decrement come back.
    /// </summary>
    private static void AssertAppendOnly(List<Counter> counters) =>
        counters.ShouldNotContain(c => c.Value < 0, "stats: counters are append-only — no row may be negative.");

    private static int Sum(List<Counter> counters, string key) =>
        counters
            .Where(x => string.Equals(x.Key, key, StringComparison.Ordinal))
            .Sum(x => x.Value);
}
