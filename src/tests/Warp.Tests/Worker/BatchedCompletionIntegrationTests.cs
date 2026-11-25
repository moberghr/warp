using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Notifications;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Worker;

[GenerateDatabaseTests(SerializeInCollection = "HeavyIntegration")]
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

    [TimedFact]
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

    [TimedFact]
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
            .Where(j => j.Type == failType)
            .AsNoTracking()
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        failed.Count.ShouldBe(failCount);
        failed.ShouldAllBe(j => j.CurrentState == State.Failed);

        // The batched write releases a failed row the way the single worker does, and leaves it
        // without an expiry: a failed job waits for an operator.
        failed.ShouldAllBe(j => j.CurrentWorkerId == null);
        failed.ShouldAllBe(j => j.LastKeepAlive == null);
        failed.ShouldAllBe(j => j.ExpireAt == null);
    }

    [TimedFact]
    public async Task GivenIdleWorker_WhenFewJobsBelowBatchSize_ThenBufferIsDrainedByIdleTrigger()
    {
        // Arrange — CompletionBatchSize is 10, so 3 jobs never hit the size threshold, and the
        // flush interval is far beyond this test's wait, so the time trigger cannot fire either.
        // Only the idle drain, run before a worker suspends on the channel, can commit them.
        await using var server = await WarpTestServer.StartAsync(Fixture, config =>
        {
            ConfigureBatchedServer(config);
            config.CompletionFlushInterval = TimeSpan.FromMinutes(5);
        });
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

    [TimedFact]
    public async Task GivenShutdown_WhenServerStops_ThenPendingCompletionsAreFlushed()
    {
        // Arrange — single-worker, isolated queue, with a barrier handler queued behind 4 short
        // ones, a batch far larger than the workload and a flush interval far beyond the test.
        // The group still holds the barrier job, so the idle trigger never fires either: when the
        // worker parks inside the barrier handler, 4 completions sit in the group's buffer. With a
        // short HostOptions.ShutdownTimeout the worker stops before the parked handler completes,
        // and the host then stops the group's CompletionFlusher, whose final flush writes them —
        // that is the load-bearing path under test.
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
            await InsertInOrderAsync(isolatedQueue, new UnitRequest(), new UnitRequest(), new UnitRequest(), new UnitRequest(), new BarrierRequest());

            // Wait for the barrier handler to enter. Single-worker FIFO guarantees the 4 short
            // jobs ran first and each added to _batch. No idle-drain has fired because the inner
            // TryRead loop is now parked inside ProcessJob(barrier).
            await barrier.Running.WaitAsync(Xunit.TestContext.Current.CancellationToken);
        }
        finally
        {
            // The flusher's final flush is the only path that can persist the 4 buffered
            // completions — the parked handler keeps the idle trigger from firing.
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

    [TimedFact]
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

        await server.FlushCountersAsync(Xunit.TestContext.Current.CancellationToken);

        // Both the aggregate and hourly counters must be written, matching non-dispatcher mode.
        var ctx = Fixture.CreateContext();
        var hourSuffix = DateTime.UtcNow.ToString("yyyy-MM-dd-HH", System.Globalization.CultureInfo.InvariantCulture);
        var aggregateSum = await ctx.Set<Counter>()
            .Where(c => c.Key == "stats:deleted")
            .SumAsync(c => c.Value, Xunit.TestContext.Current.CancellationToken);
        aggregateSum.ShouldBe(1);

        var hourlySum = await ctx.Set<Counter>()
            .Where(c => c.Key == $"stats:deleted:{hourSuffix}")
            .SumAsync(c => c.Value, Xunit.TestContext.Current.CancellationToken);
        hourlySum.ShouldBe(1);
    }

    [TimedFact]
    public async Task GivenBusyWorker_WhenFlushIntervalElapses_ThenBufferIsFlushedByTimeTrigger()
    {
        // Arrange — one worker with a batch far larger than the workload, so the size trigger cannot
        // fire. A short job, a 200ms job and a handler that parks on a barrier are queued in that
        // order. When the 200ms job finishes, the first completion has been buffered for longer than
        // the 100ms flush interval; the worker then goes straight into the parked handler, so it never
        // reaches its idle drain. Only the time trigger can have committed the first two.
        const string isolatedQueue = "batch-time-trigger";
        var barrier = new BarrierSignal();
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            config =>
            {
                config.UseDispatcher = true;
                config.WorkerCount = 1;
                config.Queues = [isolatedQueue];
                config.CompletionBatchSize = 50;
                config.CompletionFlushInterval = TimeSpan.FromMilliseconds(100);
            },
            services => services.AddSingleton(barrier));

        var ids = await InsertInOrderAsync(isolatedQueue, new UnitRequest(), new ShortDelayRequest(), new BarrierRequest());
        var (shortJobId, delayJobId, barrierJobId) = (ids[0], ids[1], ids[2]);

        try
        {
            (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken))
                .ShouldBeTrue("the barrier handler never started");

            // Act + Assert — both committed while the worker is still inside the parked handler.
            await server.WaitForJobState(shortJobId, State.Completed, TimeSpan.FromSeconds(3));
            await server.WaitForJobState(delayJobId, State.Completed, TimeSpan.FromSeconds(3));
            (await server.GetJob(barrierJobId)).CurrentState.ShouldBe(State.Processing);
        }
        finally
        {
            barrier.CanFinish.Release();
        }

        await server.WaitForJobState(barrierJobId, State.Completed, TimeSpan.FromSeconds(5));
    }

    [TimedFact]
    public async Task GivenCompletionBatchSizeOne_WhenProcessed_ThenEachJobCommitsIndividually()
    {
        // Arrange — one worker, batch size 1, and a flush interval far beyond the test. A short
        // job is queued ahead of a handler that parks on a barrier: while that handler is parked
        // the worker cannot reach its idle drain, so the short job can only be Completed in the
        // database if its completion was committed on its own, when the batch of one filled.
        const string isolatedQueue = "batch-size-one";
        var barrier = new BarrierSignal();
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            config =>
            {
                config.UseDispatcher = true;
                config.WorkerCount = 1;
                config.Queues = [isolatedQueue];
                config.CompletionBatchSize = 1;
                config.CompletionFlushInterval = TimeSpan.FromMinutes(5);
            },
            services => services.AddSingleton(barrier));

        var ids = await InsertInOrderAsync(isolatedQueue, new UnitRequest(), new BarrierRequest());
        var (shortJobId, barrierJobId) = (ids[0], ids[1]);

        try
        {
            (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken))
                .ShouldBeTrue("the barrier handler never started");

            // Act + Assert — committed while the worker is still inside the next handler.
            await server.WaitForJobState(shortJobId, State.Completed, TimeSpan.FromSeconds(3));
            (await server.GetJob(barrierJobId)).CurrentState.ShouldBe(State.Processing);
        }
        finally
        {
            barrier.CanFinish.Release();
        }

        await server.WaitForJobState(barrierJobId, State.Completed, TimeSpan.FromSeconds(5));
    }

    [TimedFact]
    public async Task GivenACompletionWriteThatHangs_WhenTheWorkerHasAnotherJob_ThenItRunsIt()
    {
        // Arrange — one worker, no prefetch: the first job is claimed alone, and when it finishes the
        // group has nothing claimed, so its completion is written at once. That write is held where it
        // announces itself (the notification transport, the last step of a flush). The worker must not
        // be the one waiting on it: the second job, claimed once the first one's slot is free, has to
        // start while the write is still held. When the worker wrote completions itself, it sat in that
        // write and the second job waited.
        const string isolatedQueue = "batch-flusher-off-worker";
        var barrier = new BarrierSignal();
        var transport = new HoldFirstFinalizedTransport();
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            config =>
            {
                config.UseDispatcher = true;
                config.WorkerCount = 1;
                config.PrefetchCount = 0;
                config.Queues = [isolatedQueue];
                config.CompletionBatchSize = 50;
                config.CompletionFlushInterval = TimeSpan.FromMinutes(5);
            },
            services =>
            {
                services.AddSingleton(barrier);
                services.Replace(ServiceDescriptor.Singleton<IWarpNotificationTransport>(transport));
            });

        var ids = await InsertInOrderAsync(isolatedQueue, new UnitRequest(), new BarrierRequest());

        try
        {
            (await transport.Held.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken))
                .ShouldBeTrue("the first completion was never written");

            // Act + Assert
            (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken))
                .ShouldBeTrue("the worker did not start its next job while a completion write was held");
        }
        finally
        {
            transport.Release.Release();
            barrier.CanFinish.Release();
        }

        await server.WaitForJobState(ids[0], State.Completed, TimeSpan.FromSeconds(5));
        await server.WaitForJobState(ids[1], State.Completed, TimeSpan.FromSeconds(5));
    }

    // Holds the first JobFinalized publish (the end of the first completion flush) until released.
    private sealed class HoldFirstFinalizedTransport : IWarpNotificationTransport
    {
        private int _held;

        public SemaphoreSlim Held { get; } = new(0);

        public SemaphoreSlim Release { get; } = new(0);

        public async Task PublishAsync(NotificationKind kind, string? queue, CancellationToken ct)
        {
            if (kind == NotificationKind.JobFinalized && Interlocked.CompareExchange(ref _held, 1, 0) == 0)
            {
                Held.Release();
                await Release.WaitAsync(CancellationToken.None);
            }
        }

        public async IAsyncEnumerable<Notification> ListenAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            yield break;
        }
    }

    // Enqueued rows with explicit, ascending schedule times, inserted in one save. The claim orders by
    // ScheduleTime, and jobs PUBLISHED in one save can share an instant, in which case "the first one
    // runs first" is up to the database. Every test here that parks a handler behind earlier jobs
    // depends on that order.
    private async Task<Guid[]> InsertInOrderAsync(string queue, params object[] requests)
    {
        var start = DateTime.UtcNow.AddSeconds(-requests.Length);
        var jobs = requests
            .Select((x, i) =>
                new Job
                {
                    Id = Guid.NewGuid(),
                    Kind = JobKind.Job,
                    CurrentState = State.Enqueued,
                    Type = x.GetType().AssemblyQualifiedName,
                    Message = JsonSerializer.Serialize(x, x.GetType()),
                    CreateTime = start.AddSeconds(i),
                    ScheduleTime = start.AddSeconds(i),
                    Queue = queue,
                })
            .ToList();

        await using var ctx = Fixture.CreateContext();
        ctx.Set<Job>().AddRange(jobs);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        return [.. jobs.Select(x => x.Id)];
    }
}
