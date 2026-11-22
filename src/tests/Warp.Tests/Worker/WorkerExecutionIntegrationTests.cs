using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Worker;

// What a worker does around a handler that is still running: renewing its hold on the row, and what
// becomes of a job whose handler is told to stop and does not. Each case runs in single-worker and in
// dispatcher mode, which keep separate copies of this code.
[GenerateDatabaseTests(SerializeInCollection = "HeavyIntegration")]
public abstract class WorkerExecutionIntegrationTestsBase : IntegrationTestBase
{
    protected WorkerExecutionIntegrationTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenLongRunningHandler_WhileItRuns_ThenKeepAliveIsRenewed(bool useDispatcher)
    {
        // StaleJobRecovery requeues a Processing row whose LastKeepAlive stops moving, so a handler
        // that outlives the stale threshold depends on the job monitor renewing it.
        var barrier = new BarrierSignal();
        await using var server = await StartAsync(useDispatcher, barrier);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new BarrierRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        try
        {
            (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken))
                .ShouldBeTrue("the handler never started");

            var atStart = (await server.GetJob(jobId)).LastKeepAlive;
            atStart.ShouldNotBeNull();

            await WarpTestServer.WaitUntil(
                async () => (await server.GetJob(jobId)).LastKeepAlive > atStart,
                TimeSpan.FromSeconds(5),
                Xunit.TestContext.Current.CancellationToken);
        }
        finally
        {
            barrier.CanFinish.Release();
        }

        await server.WaitForJobState(jobId, State.Completed, TimeSpan.FromSeconds(5));
    }

    // Rule 6.12. Nothing is written for a job before its handler starts: its "Processing" log row and,
    // in dispatcher mode, the worker's id on the row arrive with the monitor's first flush and first
    // keep-alive. A job that is still running must get both without waiting for it to finish.
    [TimedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenLongRunningHandler_WhileItRuns_ThenItsProcessingLogAndItsWorkerAppear(bool useDispatcher)
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var barrier = new BarrierSignal();
        await using var server = await StartAsync(useDispatcher, barrier);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new BarrierRequest());
        await publisher.SaveChangesAsync(ct);

        try
        {
            (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), ct)).ShouldBeTrue("the handler never started");

            await server.WaitForJobLog(jobId, "Processing");
            await WarpTestServer.WaitUntil(
                "The running job's row naming one of the server's workers",
                async () =>
                {
                    await using var ctx = Fixture.CreateContext();
                    var workerId = await ctx.Set<Job>().Where(x => x.Id == jobId).Select(x => x.CurrentWorkerId).SingleAsync(ct);

                    return workerId != null && await ctx.Set<Warp.Core.Data.Entities.Worker>().AnyAsync(x => x.Id == workerId, ct);
                },
                TimeSpan.FromSeconds(5),
                ct);
        }
        finally
        {
            barrier.CanFinish.Release();
        }

        await server.WaitForJobState(jobId, State.Completed, TimeSpan.FromSeconds(5));

        var logs = await server.GetJobLogs(jobId);
        logs.Count(x => string.Equals(x.EventType, "Processing", StringComparison.Ordinal)).ShouldBe(1, "the monitor wrote it, so the completion must not write it again");
    }

    // The other half: a job that finishes before the monitor's first flush still gets its "Processing"
    // row, with the completion, stamped with the instant the worker took it.
    [TimedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenShortHandler_WhenItCompletes_ThenItsProcessingLogIsWrittenWithTheCompletion(bool useDispatcher)
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await using var server = await StartAsync(useDispatcher, new BarrierSignal());
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new UnitRequest());
        await publisher.SaveChangesAsync(ct);

        await server.WaitForJobState(jobId, State.Completed, TimeSpan.FromSeconds(5));

        var logs = await server.GetJobLogs(jobId);
        var processing = logs.Single(x => string.Equals(x.EventType, "Processing", StringComparison.Ordinal));
        var completed = logs.Single(x => string.Equals(x.EventType, "Completed", StringComparison.Ordinal));
        processing.WorkerId.ShouldNotBeNull();
        processing.Timestamp.ShouldBeLessThanOrEqualTo(completed.Timestamp);
    }

    [TimedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenHandlerThatIgnoresCancellation_WhenItFinishesItsWork_ThenJobIsCompleted(bool useDispatcher)
    {
        // Rule 8.5: the work happened, so the job is Completed, and the cancellation request does not
        // outlive it on the row.
        var barrier = new BarrierSignal();
        await using var server = await StartAsync(useDispatcher, barrier);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new IgnoresCancellationRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        try
        {
            (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken))
                .ShouldBeTrue("the handler never started");

            await server.CreateCommandService().DeleteJob(jobId);
            (await server.GetJob(jobId)).CancellationMode.ShouldBe(CancellationMode.Graceful);
        }
        finally
        {
            barrier.CanFinish.Release();
        }

        await server.WaitForJobLog(jobId, "Completed", timeout: TimeSpan.FromSeconds(5));

        var job = await server.GetJob(jobId);
        job.CurrentState.ShouldBe(State.Completed);
        job.CancellationMode.ShouldBe(CancellationMode.None);
        job.CurrentWorkerId.ShouldBeNull();

        var logs = await server.GetJobLogs(jobId);
        logs.ShouldNotContain(x => string.Equals(x.EventType, "Cancelled", StringComparison.Ordinal));
    }

    [TimedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenOneKeepAliveRenewalFails_WhileTheHandlerRuns_ThenLaterRenewalsStillHappen(bool useDispatcher)
    {
        // The job monitor shares nothing with the handler but the row. One failed renewal (a dropped
        // connection) must be logged and survived: a monitor that died there would leave a healthy,
        // still-running job to be requeued by StaleJobRecovery and run twice.
        var barrier = new BarrierSignal();
        var fault = new FailFirstKeepAliveInterceptor();
        await using var server = await StartAsync(useDispatcher, barrier, services => services.AddServerContextInterceptor(fault));
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new BarrierRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        try
        {
            (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken))
                .ShouldBeTrue("the handler never started");

            var atStart = (await server.GetJob(jobId)).LastKeepAlive;
            atStart.ShouldNotBeNull();

            // The first renewal is the one that fails, so an advance here is a later one.
            await WarpTestServer.WaitUntil(
                async () => (await server.GetJob(jobId)).LastKeepAlive > atStart,
                TimeSpan.FromSeconds(6),
                Xunit.TestContext.Current.CancellationToken);

            fault.Failures.ShouldBe(1, "the renewal failure this test depends on was never injected");
        }
        finally
        {
            barrier.CanFinish.Release();
        }

        await server.WaitForJobState(jobId, State.Completed, TimeSpan.FromSeconds(3));
    }

    [TimedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenHandlerStagesARowThatCannotBeSaved_WhenTheWorkerSavesItsContext_ThenJobFails(bool useDispatcher)
    {
        // The handler returns normally; the failure is in the worker's save of the handler's own
        // context. That must fail the job like a throwing handler does, not leave it Processing.
        var barrier = new BarrierSignal();
        await using var server = await StartAsync(useDispatcher, barrier);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new StagesUnsavableRowRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Fails, is retried once by the test server's default policy, fails again.
        await server.WaitForJobState(jobId, State.Failed, TimeSpan.FromSeconds(8));

        var job = await server.GetJob(jobId);
        job.CurrentWorkerId.ShouldBeNull();
        job.ExpireAt.ShouldBeNull();

        var logs = await server.GetJobLogs(jobId);
        logs.ShouldContain(x => string.Equals(x.EventType, "Failed", StringComparison.Ordinal));
        logs.ShouldNotContain(x => string.Equals(x.EventType, "Completed", StringComparison.Ordinal));
    }

    [TimedFact]
    public async Task GivenSingleWorker_WhenServerStopsWhileHandlerIsRunning_ThenJobStaysProcessingForRecovery()
    {
        // The handler does not react to shutdown and the host gives up waiting after half a second.
        // The row must be left exactly as a crashed worker would leave it, Processing and still owned,
        // because that is the state StaleJobRecovery knows how to requeue. (The dispatcher-mode twin is
        // BatchedCompletionIntegrationTests.GivenShutdown_WhenServerStops_ThenPendingCompletionsAreFlushed.)
        var barrier = new BarrierSignal();
        var server = await WarpTestServer.StartAsync(
            Fixture,
            cfg =>
            {
                cfg.UseDispatcher = false;
                cfg.WorkerCount = 1;
            },
            services =>
            {
                services.AddSingleton(barrier);
                services.Configure<HostOptions>(x => x.ShutdownTimeout = TimeSpan.FromMilliseconds(500));
            });

        Guid jobId;
        try
        {
            var publisher = server.CreatePublisher();
            jobId = await publisher.Enqueue(new IgnoresCancellationRequest());
            await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

            (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken))
                .ShouldBeTrue("the handler never started");
        }
        finally
        {
            await server.DisposeAsync();
        }

        try
        {
            await using var ctx = Fixture.CreateContext();
            var job = await ctx.Set<Job>()
                .AsNoTracking()
                .Where(x => x.Id == jobId)
                .SingleAsync(Xunit.TestContext.Current.CancellationToken);
            job.CurrentState.ShouldBe(State.Processing);
            job.CurrentWorkerId.ShouldNotBeNull();

            var terminal = await ctx.Set<JobLog>()
                .AsNoTracking()
                .Where(x => x.JobId == jobId)
                .Where(x => x.EventType == "Completed" || x.EventType == "Failed" || x.EventType == "Cancelled")
                .CountAsync(Xunit.TestContext.Current.CancellationToken);
            terminal.ShouldBe(0);
        }
        finally
        {
            // Let the orphaned handler end; nothing is waiting for it any more.
            barrier.CanFinish.Release();
        }
    }

    private Task<WarpTestServer> StartAsync(bool useDispatcher, BarrierSignal barrier, Action<IServiceCollection>? configureServices = null) =>
        WarpTestServer.StartAsync(
            Fixture,
            cfg =>
            {
                cfg.UseDispatcher = useDispatcher;
                cfg.WorkerCount = 2;
                cfg.CompletionBatchSize = 10;
                cfg.CompletionFlushInterval = TimeSpan.FromMilliseconds(50);
            },
            services =>
            {
                services.AddSingleton(barrier);
                configureServices?.Invoke(services);
            });

    // Throws once, on the statement the job monitor uses to renew a running job's keep-alive. The claim
    // and the dispatcher's guarded renewal also write that column, but they name the state as well, so
    // they are left alone.
    private sealed class FailFirstKeepAliveInterceptor : DbCommandInterceptor
    {
        private int _failures;

        public int Failures => Volatile.Read(ref _failures);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var sql = command.CommandText.Replace("_", string.Empty, StringComparison.Ordinal);

            // The monitor's renewal is the one update of the keep-alive that never mentions the state: the
            // claim, the dispatcher's guarded renewal and the finalizing write all do. (In dispatcher mode
            // it also stamps the worker's id, so that column cannot tell them apart.)
            var renewsKeepAliveOnly = sql.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("lastkeepalive", StringComparison.OrdinalIgnoreCase)
                && !sql.Contains("currentstate", StringComparison.OrdinalIgnoreCase);

            if (renewsKeepAliveOnly && Interlocked.CompareExchange(ref _failures, 1, 0) == 0)
            {
                throw new InvalidOperationException("Injected failure: the keep-alive renewal could not reach the database");
            }

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
