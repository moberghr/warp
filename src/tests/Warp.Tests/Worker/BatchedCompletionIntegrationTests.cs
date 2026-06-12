using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Worker;

[GenerateDatabaseTests]
public abstract class BatchedCompletionIntegrationTestsBase : IntegrationTestBase
{
    protected BatchedCompletionIntegrationTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    private static void ConfigureBatchedServer(WarpServerBuilder<TestContext> config)
    {
        config.UseDispatcher = true;
        config.WorkerCount = 5;
        config.CompletionBatchSize = 10;
        config.CompletionFlushInterval = TimeSpan.FromMilliseconds(50);
    }

    [TimedFact(timeout: 60_000)]
    public async Task GivenManyShortJobs_WhenProcessed_ThenAllReachCompletedState()
    {
        // Arrange
        await using var server = await WarpTestServer.StartAsync(Fixture, ConfigureBatchedServer);
        var publisher = server.CreatePublisher();
        const int jobCount = 200;
        for (var i = 0; i < jobCount; i++)
        {
            await publisher.Enqueue(new UnitRequest());
        }

        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        await server.WaitForCompletion(TimeSpan.FromSeconds(45));

        // Assert — all jobs reach Completed, and one Completed JobLog exists per job.
        // Per-row terminal-state fields must also match non-dispatcher mode: no worker
        // ownership left, no keep-alive, no lingering cancellation flag, ExpireAt set.
        var ctx = Fixture.CreateContext();
        var completedCount = await ctx.Set<Job>()
            .Where(j => j.Kind == JobKind.Job && j.CurrentState == State.Completed)
            .CountAsync(Xunit.TestContext.Current.CancellationToken);
        completedCount.ShouldBe(jobCount);

        var jobs = await ctx.Set<Job>()
            .Where(j => j.Kind == JobKind.Job)
            .AsNoTracking()
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        jobs.ShouldAllBe(j => j.CurrentWorkerId == null);
        jobs.ShouldAllBe(j => j.LastKeepAlive == null);
        jobs.ShouldAllBe(j => j.CancellationMode == CancellationMode.None);
        jobs.ShouldAllBe(j => j.ExpireAt != null);

        var completedLogs = await ctx.Set<JobLog>()
            .Where(l => l.EventType == "Completed")
            .CountAsync(Xunit.TestContext.Current.CancellationToken);
        completedLogs.ShouldBe(jobCount);
    }

    [TimedFact(timeout: 60_000)]
    public async Task GivenMixedOutcomes_WhenProcessed_ThenStatesMatch()
    {
        // Arrange
        await using var server = await WarpTestServer.StartAsync(Fixture, ConfigureBatchedServer);
        var publisher = server.CreatePublisher();
        var successType = typeof(UnitRequest).AssemblyQualifiedName;
        var failType = typeof(ThrowExceptionRequest).AssemblyQualifiedName;

        const int successCount = 25;
        const int failCount = 15;
        for (var i = 0; i < successCount; i++)
        {
            await publisher.Enqueue(new UnitRequest());
        }

        for (var i = 0; i < failCount; i++)
        {
            await publisher.Enqueue(new ThrowExceptionRequest());
        }

        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        await server.WaitForCompletion(TimeSpan.FromSeconds(45));

        // Assert
        var ctx = Fixture.CreateContext();
        var completed = await ctx.Set<Job>()
            .CountAsync(j => j.Type == successType && j.CurrentState == State.Completed, Xunit.TestContext.Current.CancellationToken);
        completed.ShouldBe(successCount);

        var failed = await ctx.Set<Job>()
            .CountAsync(j => j.Type == failType && j.CurrentState == State.Failed, Xunit.TestContext.Current.CancellationToken);
        failed.ShouldBe(failCount);
    }

    [TimedFact(timeout: 60_000)]
    public async Task GivenIdleWorker_WhenFewJobsBelowBatchSize_ThenBufferIsDrainedByIdleTrigger()
    {
        // Arrange — CompletionBatchSize is 10; publishing 3 jobs never hits the size threshold.
        // Idle drain should flush them before workers suspend on the channel.
        await using var server = await WarpTestServer.StartAsync(Fixture, ConfigureBatchedServer);
        var publisher = server.CreatePublisher();
        for (var i = 0; i < 3; i++)
        {
            await publisher.Enqueue(new UnitRequest());
        }

        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        await server.WaitForCompletion(TimeSpan.FromSeconds(15));

        // Assert
        var ctx = Fixture.CreateContext();
        var completed = await ctx.Set<Job>()
            .CountAsync(j => j.Kind == JobKind.Job && j.CurrentState == State.Completed, Xunit.TestContext.Current.CancellationToken);
        completed.ShouldBe(3);
    }

    [TimedFact(timeout: 60_000)]
    public async Task GivenShutdown_WhenServerStops_ThenPendingCompletionsAreFlushed()
    {
        // Arrange — single-worker, isolated queue, with a barrier handler queued behind 4 short
        // ones. The single worker pulls them from the dispatcher channel back-to-back: short
        // jobs 1..4 each call _batch.Add and TryRead returns true for the next one — so the inner
        // loop never exits and the idle-drain at WarpDispatcherWorker line 107 does NOT run.
        // When the worker enters the barrier handler it parks inside ProcessJob, leaving 4
        // PendingCompletion entries sitting in _batch. With a short HostOptions.ShutdownTimeout,
        // base.StopAsync returns before the parked handler completes; the worker's StopAsync
        // override then runs _batch.FlushAsync() — that is the load-bearing path under test.
        // The 5th (barrier) job stays in Processing and would be recovered by StaleJobRecovery
        // in production; that's the realistic outcome of a shutdown mid-handler.
        const string isolatedQueue = "batch-shutdown-flush";
        var barrier = new BarrierSignal();
        var server = await WarpTestServer.StartAsync(
            Fixture,
            config =>
            {
                config.UseDispatcher = true;
                config.WorkerCount = 1;
                config.Queues = [isolatedQueue];
                config.CompletionBatchSize = 50;
                config.CompletionFlushInterval = TimeSpan.FromSeconds(30);
            },
            services =>
            {
                services.AddSingleton(barrier);
                services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromMilliseconds(500));
            });

        try
        {
            var publisher = server.CreatePublisher();
            for (var i = 0; i < 4; i++)
            {
                await publisher.Enqueue(new UnitRequest(), queue: isolatedQueue);
            }

            await publisher.Enqueue(new BarrierRequest(), queue: isolatedQueue);
            await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

            // Wait for the barrier handler to enter. Single-worker FIFO guarantees the 4 short
            // jobs ran first and each added to _batch. No idle-drain has fired because the inner
            // TryRead loop is now parked inside ProcessJob(barrier).
            await barrier.Running.WaitAsync(Xunit.TestContext.Current.CancellationToken);
        }
        finally
        {
            // StopAsync override is the only path that can persist the 4 buffered completions —
            // the parked handler prevents idle-drain from running.
            await server.DisposeAsync();

            // Release the orphaned handler so its task doesn't keep waiting after the host is
            // gone. The post-handler code may observe disposed scopes, but nothing awaits it.
            barrier.CanFinish.Release();
        }

        // Assert — the 4 short jobs must reach Completed via the shutdown-flush path. The 5th
        // (barrier) is the in-flight orphan; it stays Processing because its handler was still
        // parked when shutdown returned. StaleJobRecovery would clean it up in production.
        var ctx = Fixture.CreateContext();
        var jobs = await ctx.Set<Job>()
            .Where(j => j.Queue == isolatedQueue)
            .OrderBy(j => j.CreateTime)
            .AsNoTracking()
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        jobs.Count.ShouldBe(5);
        jobs.Take(4).ShouldAllBe(j => j.CurrentState == State.Completed);
        jobs[4].CurrentState.ShouldBe(State.Processing);
    }

    [TimedFact(timeout: 60_000)]
    public async Task GivenProcessingJob_WhenCancelledInDispatcherMode_ThenFlushesAsDeletedWithHourlyCounter()
    {
        // Arrange — dispatcher-mode server. Enqueue a handler that blocks on cancellation.
        await using var server = await WarpTestServer.StartAsync(Fixture, ConfigureBatchedServer);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new CancellableRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForJobState(jobId, State.Processing);

        // Act — cancel while in-flight; worker's handler token is cancelled, the catch path builds a
        // PendingCompletion with State.Deleted and the batch flushes.
        var cmd = server.CreateCommandService();
        await cmd.DeleteJob(jobId);

        await server.WaitForJobState(jobId, State.Deleted, TimeSpan.FromSeconds(15));

        // Assert — terminal state + Cancelled log.
        var job = await server.GetJob(jobId);
        job.CurrentState.ShouldBe(State.Deleted);
        job.CancellationMode.ShouldBe(CancellationMode.None);
        job.CurrentWorkerId.ShouldBeNull();
        job.ExpireAt.ShouldNotBeNull();

        var logs = await server.GetJobLogs(jobId);
        logs.ShouldContain(l => l.EventType == "Cancelled");

        // Both the aggregate and hourly counters must be written, matching non-dispatcher mode.
        var ctx = Fixture.CreateContext();
        var hourSuffix = DateTime.UtcNow.ToString("yyyy-MM-dd-HH", System.Globalization.CultureInfo.InvariantCulture);
        var aggregateSum = await ctx.Set<Counter>()
            .Where(c => c.Key == "stats:deleted")
            .SumAsync(c => c.Value, Xunit.TestContext.Current.CancellationToken);
        aggregateSum.ShouldBeGreaterThanOrEqualTo(1);

        var hourlySum = await ctx.Set<Counter>()
            .Where(c => c.Key == $"stats:deleted:{hourSuffix}")
            .SumAsync(c => c.Value, Xunit.TestContext.Current.CancellationToken);
        hourlySum.ShouldBeGreaterThanOrEqualTo(1);
    }

    [TimedFact(timeout: 60_000)]
    public async Task GivenCompletionBatchSizeOne_WhenProcessed_ThenEachJobCommitsIndividually()
    {
        // Arrange — isolated server with batching disabled (opt-out path).
        const string isolatedQueue = "batch-size-one";
        await using var server = await WarpTestServer.StartAsync(Fixture, config =>
        {
            config.UseDispatcher = true;
            config.WorkerCount = 2;
            config.Queues = [isolatedQueue];
            config.CompletionBatchSize = 1;
            config.CompletionFlushInterval = TimeSpan.FromSeconds(30);
        });

        var publisher = server.CreatePublisher();
        for (var i = 0; i < 8; i++)
        {
            await publisher.Enqueue(new UnitRequest(), queue: isolatedQueue);
        }

        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        await server.WaitForCompletion(TimeSpan.FromSeconds(30));

        // Assert
        var ctx = Fixture.CreateContext();
        var jobs = await ctx.Set<Job>()
            .Where(j => j.Queue == isolatedQueue)
            .CountAsync(j => j.CurrentState == State.Completed, Xunit.TestContext.Current.CancellationToken);
        jobs.ShouldBe(8);
    }
}
