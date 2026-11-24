using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

    // Rule 6.14: a worker group renews all its running jobs in one statement. Two jobs renewed by one
    // statement carry the same instant, which separate per-job renewals would not.
    [TimedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenTwoLongRunningHandlersInOneGroup_WhileTheyRun_ThenOneRenewalStampsBoth(bool useDispatcher)
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var barrier = new BarrierSignal();
        await using var server = await StartAsync(useDispatcher, barrier);
        var publisher = server.CreatePublisher();
        var first = await publisher.Enqueue(new BarrierRequest());
        var second = await publisher.Enqueue(new BarrierRequest());
        await publisher.SaveChangesAsync(ct);

        try
        {
            (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), ct)).ShouldBeTrue("the first handler never started");
            (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), ct)).ShouldBeTrue("the second handler never started");

            var firstAtStart = (await server.GetJob(first)).LastKeepAlive;
            var secondAtStart = (await server.GetJob(second)).LastKeepAlive;

            // Once both have been renewed since they started, the latest renewal covered both.
            await WarpTestServer.WaitUntil(
                "Both running jobs renewed by the same statement",
                async () =>
                {
                    var a = (await server.GetJob(first)).LastKeepAlive;
                    var b = (await server.GetJob(second)).LastKeepAlive;

                    return a > firstAtStart && b > secondAtStart && a == b;
                },
                TimeSpan.FromSeconds(5),
                ct);
        }
        finally
        {
            barrier.CanFinish.Release(2);
        }

        await server.WaitForJobState(first, State.Completed, TimeSpan.FromSeconds(5));
        await server.WaitForJobState(second, State.Completed, TimeSpan.FromSeconds(5));
    }

    [TimedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenOneKeepAliveRenewalFails_WhileTheHandlerRuns_ThenLaterRenewalsStillHappen(bool useDispatcher)
    {
        // The group's job monitor shares nothing with the handler but the row. One failed renewal (a dropped
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

    [TimedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenAHandlerThatStagesAJobWithoutSaving_WhenItCompletes_ThenTheStagedJobIsNotCommitted(bool useDispatcher)
    {
        // The handler owns its DbContext (rule 6.17): the worker neither resolves nor saves it, so a job
        // the handler staged and never saved is discarded with the handler's scope, and the handler's
        // own job still completes.
        await using var server = await StartAsync(useDispatcher, new BarrierSignal());
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new StageChildWithoutSavingRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        await server.WaitForJobState(jobId, State.Completed);

        // Assert
        var staged = await Fixture.CreateContext().Set<Job>()
            .AsNoTracking()
            .Where(x => x.Id != jobId)
            .CountAsync(Xunit.TestContext.Current.CancellationToken);
        staged.ShouldBe(0);
    }

    [TimedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenAHandlerThatStagesAJobAndThrows_WhenItFails_ThenNoUnsavedJobsWarningIsLogged(bool useDispatcher)
    {
        // The unsaved-staged-jobs warning is for work lost by a missing save. A failed attempt's staged
        // jobs are meant to be discarded, so the warning must stay quiet for it.
        var capture = new CapturingLoggerProvider();
        await using var server = await StartAsync(useDispatcher, new BarrierSignal(), services =>
        {
            services.AddSingleton<ILoggerProvider>(capture);
            services.Configure<LoggerFilterOptions>(x => x.AddFilter<CapturingLoggerProvider>("Warp", LogLevel.Information));
        });
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new StageChildThenThrowRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        await server.WaitForJobState(jobId, State.Failed);

        // Assert
        capture.Messages.ShouldNotContain(x => x.Contains("without SaveChangesAsync", StringComparison.Ordinal));
    }

    [TimedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenAHandlerThatStagesAJobWithoutSaving_WhenItCompletes_ThenTheUnsavedJobsWarningIsLogged(bool useDispatcher)
    {
        // The worker does not save for the handler (rule 6.17), so this warning is the only sign that a
        // staged job was lost. It is raised when the handler's scope disposes the publisher.
        var capture = new CapturingLoggerProvider();
        await using var server = await StartAsync(useDispatcher, new BarrierSignal(), services =>
        {
            services.AddSingleton<ILoggerProvider>(capture);
            services.Configure<LoggerFilterOptions>(x => x.AddFilter<CapturingLoggerProvider>("Warp", LogLevel.Information));
        });
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new StageChildWithoutSavingRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        await server.WaitForJobState(jobId, State.Completed);

        // Assert
        capture.Messages.ShouldContain(x => x.Contains("without SaveChangesAsync", StringComparison.Ordinal));
    }

    [TimedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenInformationLoggingForTheWorker_WhenAJobRuns_ThenItsExecutingAndCompletedLinesAreLogged(bool useDispatcher)
    {
        // The per-job lines are guarded by IsEnabled (they boxed their arguments on every job even with
        // Information off); with it on they must still be written.
        var capture = new CapturingLoggerProvider();
        await using var server = await StartAsync(useDispatcher, new BarrierSignal(), services =>
        {
            services.AddSingleton<ILoggerProvider>(capture);
            services.Configure<LoggerFilterOptions>(x => x.AddFilter<CapturingLoggerProvider>("Warp.Worker", LogLevel.Information));
        });
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new UnitRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        await server.WaitForJobState(jobId, State.Completed);

        // Assert
        capture.Messages.ShouldContain(x => x.Contains("executing job " + jobId, StringComparison.Ordinal));
        capture.Messages.ShouldContain(x => x.Contains("completed job " + jobId, StringComparison.Ordinal));
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                messages.Enqueue(formatter(state, exception));
            }
        }
    }

    // #347: StaleJobRecovery can take a job back while its handler still runs (a long pause, lost keep-alives)
    // and a claim can take it again, on another server or on the same worker. Only the run id tells the two
    // runs apart: here the worker id and keep-alive are left exactly as the old run knows them, which is what a
    // re-claim by the same worker that has since renewed looks like. Single-worker mode had no guard at all.
    [TimedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenTheJobIsClaimedAgainWhileItsHandlerRuns_WhenTheOldRunFinishes_ThenItLeavesTheNewRunsRow(bool useDispatcher)
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var barrier = new BarrierSignal();
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            cfg =>
            {
                cfg.UseDispatcher = useDispatcher;
                cfg.WorkerCount = 1;
                cfg.PrefetchCount = 0;
                cfg.CompletionBatchSize = 10;
                cfg.CompletionFlushInterval = TimeSpan.FromMilliseconds(50);
            },
            services => services.AddSingleton(barrier));
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new BarrierRequest());
        await publisher.SaveChangesAsync(ct);

        var newRun = Guid.NewGuid();
        try
        {
            (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), ct)).ShouldBeTrue("the handler never started");
            (await server.GetJob(jobId)).RunId.ShouldNotBeNull("the claim stamped no run id");

            // The new claim: same row, same worker stamps, another run.
            await using var context = Fixture.CreateContext();
            await context.Set<Job>()
                .Where(x => x.Id == jobId)
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.RunId, newRun), ct);
        }
        finally
        {
            barrier.CanFinish.Release();
        }

        // One worker: the next job runs only once the old run's finalizing write has been made (single-worker
        // mode) or handed to the group's flusher, which writes buffers in order (dispatcher mode).
        var next = await publisher.Enqueue(new UnitRequest());
        await publisher.SaveChangesAsync(ct);
        await server.WaitForJobState(next, State.Completed, TimeSpan.FromSeconds(5));

        var row = await server.GetJob(jobId);
        row.CurrentState.ShouldBe(State.Processing);
        row.RunId.ShouldBe(newRun);
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

    // Throws once, on the statement the worker group's monitor uses to renew its running jobs' keep-alive
    // (rule 6.14). The provider SQL names its rows "beat", which nothing else does.
    private sealed class FailFirstKeepAliveInterceptor : DbCommandInterceptor
    {
        private int _failures;

        public int Failures => Volatile.Read(ref _failures);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            var renewsKeepAlive = command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains(" beat", StringComparison.OrdinalIgnoreCase);

            if (renewsKeepAlive && Interlocked.CompareExchange(ref _failures, 1, 0) == 0)
            {
                throw new InvalidOperationException("Injected failure: the keep-alive renewal could not reach the database");
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
