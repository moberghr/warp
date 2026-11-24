using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Logging;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Worker;

namespace Warp.Tests.Worker;

// The group's monitor (rule 6.14) when something fails: a handler's cancellation callback that blocks, a
// log save that fails, a worker that reuses its "Processing" row holder for the next job.
[GenerateDatabaseTests]
public abstract class RunningJobMonitorFailureTestsBase : IAsyncLifetime
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

    private readonly IDatabaseFixture _fixture;

    protected RunningJobMonitorFailureTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task Tick_ACancellationCallbackBlocks_TheGroupsOtherJobsAreStillRenewed()
    {
        // Arrange: the renewal asks to cancel job A, whose handler's cancellation callback blocks.
        var ct = Xunit.TestContext.Current.CancellationToken;
        var jobA = await InsertProcessingJobAsync();
        var jobB = await InsertProcessingJobAsync();
        using var blocked = new ManualResetEventSlim();
        using var renewals = new SemaphoreSlim(0);
        var calls = 0;
        var monitor = CreateMonitor(
            (_, _, _, _) =>
            {
                renewals.Release();
                IReadOnlyCollection<Guid> cancel = Interlocked.Increment(ref calls) == 1 ? [jobA] : [];

                return Task.FromResult(cancel);
            },
            new RecordingLogger());
        using var ctsA = new CancellationTokenSource();
        using var ctsB = new CancellationTokenSource();
        ctsA.Token.Register(() => blocked.Wait(TimeSpan.FromSeconds(8)));
        using var entryA = Register(monitor, jobA, ctsA, new DeferredJobLog());
        using var entryB = Register(monitor, jobB, ctsB, new DeferredJobLog());

        try
        {
            // Act + Assert: the next renewal still comes while A's callback is blocked.
            (await renewals.WaitAsync(TimeSpan.FromSeconds(2), ct)).ShouldBeTrue("the first renewal never ran");
            (await renewals.WaitAsync(TimeSpan.FromSeconds(2), ct)).ShouldBeTrue("a blocked cancellation callback held the group's monitor");
        }
        finally
        {
            blocked.Set();
        }
    }

    [TimedFact]
    public async Task Tick_TheLogSaveFails_TheNextTickWritesTheRows()
    {
        // Arrange: a row with the "Processing" row's id already exists, so the save fails on the key.
        var ct = Xunit.TestContext.Current.CancellationToken;
        var jobId = await InsertProcessingJobAsync();
        var logger = new RecordingLogger();
        using var ticks = new TickCounter();
        var monitor = CreateMonitor(ticks.Renew(NoCancellations), logger);
        var processing = new DeferredJobLog();
        var row = ProcessingRow(jobId);
        await InsertClashingLogAsync(row.Id);
        processing.Set(row);
        using var cts = new CancellationTokenSource();
        var logs = new JobLogCollector { JobId = jobId };
        logs.Add("Information", "handler line");
        using var entry = monitor.Register(Run(jobId), logs, new JobProgressCollector { JobId = jobId }, processing, cts, CancellationToken.None);
        (await logger.Warnings.WaitAsync(TimeSpan.FromSeconds(3), ct)).ShouldBeTrue("the save never failed");

        // Act: the clash goes away; the next tick saves again.
        await DeleteLogAsync(row.Id);
        await ticks.WaitForTickAfterNowAsync(ct);

        // Assert
        (await CountLogsAsync(jobId)).ShouldBe(2);
    }

    [TimedFact]
    public async Task Tick_TheLogSaveFails_TheCancellationTheRenewalReturnedIsStillApplied()
    {
        // Arrange: the tick's save fails on a clashing key.
        var ct = Xunit.TestContext.Current.CancellationToken;
        var jobId = await InsertProcessingJobAsync();
        var calls = 0;
        var monitor = CreateMonitor(
            (_, _, _, _) =>
            {
                IReadOnlyCollection<Guid> cancel = Interlocked.Increment(ref calls) == 1 ? [jobId] : [];

                return Task.FromResult(cancel);
            },
            new RecordingLogger());
        var processing = new DeferredJobLog();
        var row = ProcessingRow(jobId);
        await InsertClashingLogAsync(row.Id);
        processing.Set(row);
        using var cts = new CancellationTokenSource();

        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = cts.Token.Register(() => cancelled.TrySetResult());

        // Act
        using var entry = monitor.Register(Run(jobId), new JobLogCollector { JobId = jobId }, new JobProgressCollector { JobId = jobId }, processing, cts, CancellationToken.None);

        // Assert
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        cts.IsCancellationRequested.ShouldBeTrue();
    }

    [TimedFact]
    public async Task Tick_TheHolderHoldsAnotherJobsProcessingRow_ItIsNotTaken()
    {
        // Arrange: a worker reuses one holder for every job it runs; this one already holds job B's row.
        var ct = Xunit.TestContext.Current.CancellationToken;
        var jobA = await InsertProcessingJobAsync();
        var jobB = await InsertProcessingJobAsync();
        using var ticks = new TickCounter();
        var monitor = CreateMonitor(ticks.Renew(NoCancellations), new RecordingLogger());
        var holder = new DeferredJobLog();
        holder.Set(ProcessingRow(jobB));
        using var cts = new CancellationTokenSource();
        var logs = new JobLogCollector { JobId = jobA };
        logs.Add("Information", "a line, so the tick saves");

        // Act
        using (var entry = Register(monitor, jobA, cts, holder, logs))
        {
            await ticks.WaitForTickAfterNowAsync(ct);
        }

        // Assert
        (await CountLogsAsync(jobA)).ShouldBe(1);
        (await CountLogsAsync(jobB)).ShouldBe(0);
        holder.Take().ShouldNotBeNull().JobId.ShouldBe(jobB);
    }

    private static Task<IReadOnlyCollection<Guid>> NoCancellations(DbContext context, IReadOnlyList<JobRunOwner> runs, DateTime now, CancellationToken ct) =>
        Task.FromResult<IReadOnlyCollection<Guid>>([]);

    private static JobLog ProcessingRow(Guid jobId) => new()
    {
        JobId = jobId,
        EventType = "Processing",
        Timestamp = DateTime.UtcNow,
        Level = "Information",
        Message = $"The job {jobId} is being processed",
    };

    private static JobRunOwner Run(Guid jobId) => new(jobId, Guid.NewGuid(), null, null);

    private static IDisposable Register(RunningJobMonitor monitor, Guid jobId, CancellationTokenSource cts, DeferredJobLog holder, JobLogCollector? logs = null) =>
        monitor.Register(Run(jobId), logs ?? new JobLogCollector { JobId = jobId }, new JobProgressCollector { JobId = jobId }, holder, cts, CancellationToken.None);

    private RunningJobMonitor CreateMonitor(RunningJobMonitor.RenewRunningJobs renew, ILogger logger)
    {
        var services = new ServiceCollection();
        services.AddScoped<TestContext>(_ => _fixture.CreateContext());
        services.AddTestServerContext<TestContext>();
        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var configuration = new WarpServerConfiguration { LogFlushInterval = Tick, CancellationCheckInterval = Tick };

        return new RunningJobMonitor(scopes, TimeProvider.System, configuration, renew, logger);
    }

    private async Task<Guid> InsertProcessingJobAsync(Guid? id = null)
    {
        await using var context = _fixture.CreateContext();
        var job = new Job
        {
            Id = id ?? Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Processing,
            Type = "test",
            Message = "{}",
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            LastKeepAlive = DateTime.UtcNow,
        };
        context.Set<Job>().Add(job);
        await context.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        return job.Id;
    }

    // A row of another job holding the given id, so a save of a row with that id fails.
    private async Task InsertClashingLogAsync(Guid id)
    {
        await using var context = _fixture.CreateContext();
        context.Set<JobLog>().Add(new JobLog { Id = id, JobId = Guid.NewGuid(), EventType = "Clash", Timestamp = DateTime.UtcNow, Level = "Information", Message = "clash" });
        await context.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
    }

    private async Task DeleteLogAsync(Guid id)
    {
        await using var context = _fixture.CreateContext();
        await context.Set<JobLog>().Where(x => x.Id == id).ExecuteDeleteAsync(Xunit.TestContext.Current.CancellationToken);
    }

    private async Task<int> CountLogsAsync(Guid jobId)
    {
        await using var context = _fixture.CreateContext();

        return await context.Set<JobLog>().CountAsync(x => x.JobId == jobId, Xunit.TestContext.Current.CancellationToken);
    }

    // Counts the monitor's renewals, its first step on every tick, so a test can wait for a whole tick to
    // have run after a point without polling: ticks run one after another, so the second renewal to START
    // after that point follows a tick that began after it and has finished (its log save included).
    private sealed class TickCounter : IDisposable
    {
        private readonly SemaphoreSlim _renewed = new(0);
        private int _calls;

        public RunningJobMonitor.RenewRunningJobs Renew(RunningJobMonitor.RenewRunningJobs inner) =>
            (context, runs, now, ct) =>
            {
                Interlocked.Increment(ref _calls);
                _renewed.Release();

                return inner(context, runs, now, ct);
            };

        public async Task WaitForTickAfterNowAsync(CancellationToken ct)
        {
            var target = Volatile.Read(ref _calls) + 2;
            while (Volatile.Read(ref _calls) < target)
            {
                (await _renewed.WaitAsync(TimeSpan.FromSeconds(5), ct)).ShouldBeTrue("the monitor stopped ticking");
            }
        }

        public void Dispose() => _renewed.Dispose();
    }

    private sealed class RecordingLogger : ILogger
    {
        public SemaphoreSlim Warnings { get; } = new(0);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Release();
            }
        }
    }
}
