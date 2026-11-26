using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Warp.Core;
using Warp.Core.Data;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Events;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;
using Warp.Worker.Services;

namespace Warp.Tests.Worker;

/// <summary>
/// The cluster task scheduler (rule 6.23): one server per tick runs a cluster-wide task, an idle task
/// backs off, a signal reruns a task already running here, a run that loses its lease stops, and a
/// server from before the lease keeps the old lock meaningful. Passes are driven one at a time and runs
/// are pinned with <see cref="BarrierSignal"/>, so nothing waits on the clock.
/// </summary>
[GenerateDatabaseTests]
public abstract class ClusterTaskSchedulerTestsBase : IAsyncLifetime
{
    private const string TaskLockKey = "warp:test-cluster-task";

    private readonly IDatabaseFixture _fixture;

    protected ClusterTaskSchedulerTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task PassAsync_TwoServersAndOneDueTask_OnlyOneRunsIt()
    {
        var task = new PinnedTask(TimeSpan.FromHours(1));
        using var first = await BuildAsync(task);
        using var second = await BuildAsync(task);

        // Act: the first server claims and starts it; the second passes while it runs.
        await first.PassAsync(Ct);
        (await task.Barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue("the first server did not run the task");
        await second.PassAsync(Ct);
        var running = first.RunningTask(PinnedTask.TaskName) ?? throw new InvalidOperationException("the first server is not running the task");
        task.Barrier.CanFinish.Release();
        await running;

        // Assert
        task.Runs.ShouldBe(1);
        second.RunningTask(PinnedTask.TaskName).ShouldBeNull();
    }

    [TimedFact]
    public async Task PassAsync_TaskThatKeepsFindingNothing_StaysOnItsInterval()
    {
        // No idle backoff: an idle task is claimed every interval, and its lease lapses as it falls due.
        var task = new IdleTask(TimeSpan.FromSeconds(10));
        using var scheduler = await BuildAsync(task);

        // Act: two idle runs in a row.
        await RunOnceAsync(scheduler, IdleTask.TaskName);
        await MakeDueAsync(scheduler, IdleTask.TaskName);
        await RunOnceAsync(scheduler, IdleTask.TaskName);

        // Assert: the second claim moved the next run one interval out, and the lease ends with it.
        task.Runs.ShouldBe(2);
        var row = await ReadAsync(IdleTask.TaskName);
        var fromClaim = row.NextRunAt - row.ClaimedAt!.Value;
        fromClaim.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(5));
        fromClaim.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(15));
        row.LeaseUntil.ShouldBe(row.NextRunAt);
    }

    [TimedFact]
    public async Task PassAsync_EveryTaskClaimedByThePass_SleepsUntilTheNextOneIsDue()
    {
        // Tasks share due times, so one pass claims them all; an idle run wakes nobody, so the pass itself
        // must return the next due time. It slept the one-minute maximum, and a 10 s task ran once a minute.
        var task = new IdleTask(TimeSpan.FromSeconds(10));
        using var scheduler = await BuildAsync(task);

        // Act
        var sleep = await scheduler.PassAsync(Ct);

        // Assert
        sleep.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(15));
        sleep.ShouldBeGreaterThan(TimeSpan.FromSeconds(4));
        await (scheduler.RunningTask(IdleTask.TaskName) ?? Task.CompletedTask);
    }

    [TimedFact]
    public async Task RunningTask_IdleRunThatWritesNothing_DoesNotWakeTheLoop()
    {
        // A run that wrote no completion still holds its lease, so a pass right after it could claim
        // nothing; waking for one doubled the idle claim statements. The first idle run records itself
        // (a completion, so a wake); the next within a minute writes nothing.
        var task = new IdleTask(TimeSpan.FromSeconds(10));
        using var scheduler = await BuildAsync(task);
        await RunOnceAsync(scheduler, IdleTask.TaskName);
        await MakeDueAsync(scheduler, IdleTask.TaskName);
        var before = scheduler.Wakes;

        // Act
        await RunOnceAsync(scheduler, IdleTask.TaskName);

        // Assert
        task.Runs.ShouldBe(2);
        scheduler.Wakes.ShouldBe(before);
    }

    [TimedFact]
    public async Task PassAsync_SignalOnAnotherServerAfterAnIdleScheduledRun_RunsTheTaskThereAtOnce()
    {
        // Two servers, signals in-process only. The first wins the tick and finds nothing; a job then
        // finishes on the second. The first's lease runs to the next tick, and a held lease turns the
        // second's signal into a request nobody reads before then: the idle run has to free it. The first
        // idle run records itself (a completion either way), so the case is the second one.
        var task = new SignaledTask(TimeSpan.FromHours(1));
        using var first = await BuildAsync(task);
        using var second = await BuildAsync(task);
        await RunOnceAsync(first, SignaledTask.TaskName);
        await MakeDueAsync(first, SignaledTask.TaskName);
        await RunOnceAsync(first, SignaledTask.TaskName);
        task.Runs.ShouldBe(2);

        // Act
        second.Signal(SignaledTask.TaskName);
        await RunOnceAsync(second, SignaledTask.TaskName);

        // Assert: run on the second server, although the first claimed the task until an hour from now.
        task.Runs.ShouldBe(3);
    }

    [TimedFact]
    public async Task RunningTask_IdleRunOfASignalTaskThatOnlyFreesItsLease_DoesNotWakeTheLoop()
    {
        // The release leaves the task due when it was, so a claim right after it would find nothing:
        // the idle run of a signal task costs its one release, not a release and a claim.
        var task = new SignaledTask(TimeSpan.FromSeconds(10));
        using var scheduler = await BuildAsync(task);
        await AddServerAsync();
        await RunOnceAsync(scheduler, SignaledTask.TaskName);
        await MakeDueAsync(scheduler, SignaledTask.TaskName);
        var before = scheduler.Wakes;

        // Act
        await RunOnceAsync(scheduler, SignaledTask.TaskName);

        // Assert
        task.Runs.ShouldBe(2);
        scheduler.Wakes.ShouldBe(before);
        (await ReadAsync(SignaledTask.TaskName)).LeaseUntil.ShouldBeNull();
        (await scheduler.PassAsync(Ct)).ShouldBeGreaterThan(TimeSpan.FromSeconds(4), "the pass after it should sleep until the next tick");
    }

    [TimedFact]
    public async Task RunningTask_IdleRunOfASignalTaskOnALoneServer_KeepsItsLease()
    {
        // The release is only for another server's signal; with no other server alive nobody else can claim
        // the task, so the idle run writes nothing (the idle benchmark read the release as +6% statements).
        var task = new SignaledTask(TimeSpan.FromSeconds(10));
        using var scheduler = await BuildAsync(task);
        await RunOnceAsync(scheduler, SignaledTask.TaskName);
        await MakeDueAsync(scheduler, SignaledTask.TaskName);

        // Act
        await RunOnceAsync(scheduler, SignaledTask.TaskName);

        // Assert: still leased to the next tick, and a signal here still runs it at once.
        task.Runs.ShouldBe(2);
        (await ReadAsync(SignaledTask.TaskName)).LeaseUntil.ShouldNotBeNull();
        scheduler.Signal(SignaledTask.TaskName);
        await RunOnceAsync(scheduler, SignaledTask.TaskName);
        task.Runs.ShouldBe(3);
    }

    [TimedFact]
    public async Task PassAsync_RunClaimedOnItsSchedule_TellsTheTaskItIsAScheduledRun()
    {
        // The Orchestrator sweeps every parent on a run its schedule started (rule 6.11), and only checks
        // the named parents on a run a signal started.
        var task = new ScheduleAwareTask();
        using var scheduler = await BuildAsync(task);

        // Act: the first run is the claim's (due at once); the second a signal's.
        await RunOnceAsync(scheduler, ScheduleAwareTask.TaskName);
        scheduler.Signal(ScheduleAwareTask.TaskName);
        await RunOnceAsync(scheduler, ScheduleAwareTask.TaskName);

        // Assert
        task.Runs.ShouldBe([true, false]);
    }

    [TimedFact]
    public async Task PassAsync_SignalBeforeTheFirstClaimWhileAnOldServerHoldsTheOldLock_TaskDoesNotRun()
    {
        // A signal that arrived during registration is honoured by the first pass before any claim has
        // said whether a server from before the lease is alive. That run must take the old lock too
        // (rule 6.23): started without it, the task ran beside the old server's.
        var task = new SignaledTask(TimeSpan.FromHours(1));
        using var scheduler = await BuildAsync(task);
        await using (var ctx = _fixture.CreateContext())
        {
            ctx.Set<Server>().Add(new Server { Id = Guid.NewGuid(), StartedTime = DateTime.UtcNow, LastHeartbeatTime = DateTime.UtcNow });
            await ctx.SaveChangesAsync(Ct);
        }

        var barrier = new BarrierSignal();
        var holderContext = _fixture.CreateContext();
        var holder = TestTasks.QueriesFor(holderContext).RunUnderTransactionLockAsync(
            holderContext,
            TaskLockKey,
            async (_, ct) =>
            {
                barrier.Running.Release();
                await barrier.CanFinish.WaitAsync(ct);

                return 0;
            },
            Ct);
        (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue("the old server's lock was not taken");

        try
        {
            // Act: the signal claims the task and starts it; with every task running, this pass sends no claim.
            scheduler.Signal(SignaledTask.TaskName);
            await RunOnceAsync(scheduler, SignaledTask.TaskName);

            // Assert
            task.Runs.ShouldBe(0);
        }
        finally
        {
            barrier.CanFinish.Release();
            await holder;
        }
    }

    [TimedFact]
    public async Task PassAsync_MoreTimerTasksDueThanSlots_RunsTwoAtATime()
    {
        // Arrange: three timer-only tasks fall due together (the grid makes them), pinned in their run. Run all at
        // once, each held a connection of its own and the pool kept them; two slots is the cap.
        var barrier = new BarrierSignal();
        var tasks = new[] { new SlotTask("slot-a", barrier), new SlotTask("slot-b", barrier), new SlotTask("slot-c", barrier) };
        using var scheduler = await BuildAsync(tasks, TimeSpan.Zero);

        try
        {
            // Act
            await scheduler.PassAsync(Ct);

            // Assert: two run, the third waits for a slot, and runs once one frees.
            (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue("the first task did not start");
            (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue("the second task did not start");
            (await barrier.Running.WaitAsync(TimeSpan.FromMilliseconds(300), Ct)).ShouldBeFalse("a third timer task ran beside two");

            barrier.CanFinish.Release();
            (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue("the third task did not run once a slot freed");
        }
        finally
        {
            barrier.CanFinish.Release(3);
            foreach (var task in tasks)
            {
                if (scheduler.RunningTask(task.Name) is { } running)
                {
                    await running;
                }
            }
        }

        tasks.Sum(x => x.Runs).ShouldBe(3);
    }

    [TimedFact]
    public async Task PassAsync_TimerSlotsFull_SignalDrivenTaskStillRuns()
    {
        // Arrange: both timer slots held by pinned runs, and a signal-driven task (the Orchestrator's kind) due.
        var barrier = new BarrierSignal();
        var pinned = new[] { new SlotTask("slot-a", barrier), new SlotTask("slot-b", barrier) };
        var signaled = new SignaledTask(TimeSpan.FromHours(1));
        using var scheduler = await BuildAsync([.. pinned, signaled], TimeSpan.Zero);

        try
        {
            // Act
            await scheduler.PassAsync(Ct);
            (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue("the first timer task did not start");
            (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue("the second timer task did not start");

            // Assert: it is on the job path and does not wait for a timer slot.
            (await signaled.Ran.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue("the signal-driven task waited behind the timer tasks");
        }
        finally
        {
            barrier.CanFinish.Release(2);
            foreach (var task in pinned)
            {
                if (scheduler.RunningTask(task.Name) is { } running)
                {
                    await running;
                }
            }
        }
    }

    [TimedFact]
    public async Task PassAsync_SignalWhileTheTaskRunsHere_RunsItAgainRightAfter()
    {
        var task = new PinnedTask(TimeSpan.FromHours(1));
        using var scheduler = await BuildAsync(task);
        await scheduler.PassAsync(Ct);
        (await task.Barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue("the task did not start");

        // Act: a signal arrives while it runs, then the run ends.
        scheduler.Signal(PinnedTask.TaskName);
        await scheduler.PassAsync(Ct);
        task.Barrier.CanFinish.Release();
        await (scheduler.RunningTask(PinnedTask.TaskName) ?? Task.CompletedTask);
        task.Barrier.CanFinish.Release();
        await scheduler.PassAsync(Ct);
        await (scheduler.RunningTask(PinnedTask.TaskName) ?? Task.CompletedTask);

        // Assert: run again although its next scheduled run is an hour away.
        task.Runs.ShouldBe(2);
    }

    [TimedFact]
    public async Task PassAsync_SignalsElsewhereWhileASignaledRunHolds_FoldIntoOneRerun()
    {
        // A run a signal started holds its lease for the rest of the signal interval (rule 6.18): a
        // signal on another server meanwhile is a run request, not a second run, and the completion
        // written when the hold ends turns it into one rerun.
        var clock = new TimerWatchingTimeProvider();
        var task = new SignaledTask(TimeSpan.FromHours(1));
        using var first = await BuildAsync([task], TimeSpan.FromMinutes(30), clock);
        using var second = await BuildAsync([task], TimeSpan.FromMinutes(30), clock);
        first.Signal(SignaledTask.TaskName);
        await first.PassAsync(Ct);
        (await task.Ran.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue("the signal did not run the task");
        (await clock.WaitForTimerAsync(x => x > TimeSpan.FromMinutes(25), Ct)).ShouldBeTrue("the run did not hold");

        // Act
        second.Signal(SignaledTask.TaskName);
        await second.PassAsync(Ct);

        // Assert: one run, the lease still held, the request on the row.
        task.Runs.ShouldBe(1);
        second.RunningTask(SignaledTask.TaskName).ShouldBeNull();
        var held = await ReadAsync(SignaledTask.TaskName);
        held.LeaseUntil.ShouldNotBeNull();
        held.RunRequestedAt.ShouldNotBeNull();

        // Assert: when the hold ends the completion makes the task due, and the next pass reruns it.
        clock.Advance(TimeSpan.FromMinutes(30));
        await (first.RunningTask(SignaledTask.TaskName) ?? Task.CompletedTask);
        await RunOnceAsync(first, SignaledTask.TaskName);
        task.Runs.ShouldBe(2);
    }

    [TimedFact]
    public async Task PassAsync_RunOnSchedule_DoesNotHold()
    {
        // Only a run a signal started holds: a scheduled run of a task with signals ends at once.
        var clock = new TimerWatchingTimeProvider();
        var task = new SignaledTask(TimeSpan.FromHours(1));
        using var scheduler = await BuildAsync([task], TimeSpan.FromMinutes(30), clock);

        // Act
        await RunOnceAsync(scheduler, SignaledTask.TaskName);

        // Assert
        task.Runs.ShouldBe(1);
        clock.Timers.ShouldNotContain(x => x > TimeSpan.FromMinutes(25));
    }

    [TimedFact]
    public async Task RunningTask_AnotherServerTakesTheLease_TheRunIsCancelled()
    {
        // A run that stalled past its lease must stop once another server has taken the task, or the
        // task would run on two servers at once. The lease is renewed every third of the interval.
        var task = new PinnedTask(TimeSpan.FromSeconds(3));
        using var scheduler = await BuildAsync(task);
        await scheduler.PassAsync(Ct);
        (await task.Barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue("the task did not start");

        // Act
        await using (var ctx = _fixture.CreateContext())
        {
            await ctx.Set<ClusterTask>()
                .Where(x => x.Name == PinnedTask.TaskName)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.ClaimedBy, Guid.NewGuid()), Ct);
        }

        // Assert
        await (scheduler.RunningTask(PinnedTask.TaskName) ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        task.Cancelled.ShouldBeTrue();
    }

    [TimedFact]
    public async Task PassAsync_ServerFromBeforeTheLeaseHoldsTheOldLock_TaskDoesNotRun()
    {
        // While a server from before the lease is alive, a run also takes the task's old lock; when that
        // server holds it, this server's run does nothing (rule 6.23, rolling deploy).
        var task = new IdleTask(TimeSpan.FromHours(1));
        using var scheduler = await BuildAsync(task);
        await using (var ctx = _fixture.CreateContext())
        {
            ctx.Set<Server>().Add(new Server { Id = Guid.NewGuid(), StartedTime = DateTime.UtcNow, LastHeartbeatTime = DateTime.UtcNow });
            await ctx.SaveChangesAsync(Ct);
        }

        var barrier = new BarrierSignal();
        var holderContext = _fixture.CreateContext();
        var holder = TestTasks.QueriesFor(holderContext).RunUnderTransactionLockAsync(
            holderContext,
            TaskLockKey,
            async (_, ct) =>
            {
                barrier.Running.Release();
                await barrier.CanFinish.WaitAsync(ct);

                return 0;
            },
            Ct);
        (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue("the old server's lock was not taken");

        try
        {
            // Act
            await RunOnceAsync(scheduler, IdleTask.TaskName);

            // Assert
            task.Runs.ShouldBe(0);
        }
        finally
        {
            barrier.CanFinish.Release();
            await holder;
        }
    }

    private Task<ClusterTaskScheduler<TestContext>> BuildAsync(IServerTask task) => BuildAsync([task], TimeSpan.Zero);

    private async Task<ClusterTaskScheduler<TestContext>> BuildAsync(IServerTask[] tasks, TimeSpan signalInterval, TimeProvider? time = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _fixture.CreateContext());
        services.AddTestServerContext<TestContext>();
        services.AddSingleton(TestTasks.QueriesFor(_fixture.CreateContext()));
        foreach (var task in tasks)
        {
            services.AddSingleton(task);
        }

        var provider = services.BuildServiceProvider();

        // Each scheduler is a live server, so two of them see each other in the claim's liveness check.
        var serverId = await AddServerAsync();
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();
        var scheduler = new ClusterTaskScheduler<TestContext>(
            tasks.Select(ServerTaskInfo.From),
            new ServerTaskRunner<TestContext>(scopes, new NoopLockProvider()),
            scopes,
            time ?? TimeProvider.System,
            serverId,
            NullLogger.Instance,
            signalInterval,
            TimeSpan.FromMinutes(5));
        await scheduler.RegisterAsync(Ct);

        return scheduler;
    }

    // A live server row (rule 6.23 liveness is read on the database clock; the window is five minutes).
    private async Task<Guid> AddServerAsync()
    {
        var id = Guid.NewGuid();
        await using var ctx = _fixture.CreateContext();
        ctx.Set<Server>().Add(new Server { Id = id, StartedTime = DateTime.UtcNow, LastHeartbeatTime = DateTime.UtcNow, CoordinationVersion = ServerCoordination.LeaseVersion });
        await ctx.SaveChangesAsync(Ct);

        return id;
    }

    private static async Task RunOnceAsync(ClusterTaskScheduler<TestContext> scheduler, string name)
    {
        await scheduler.PassAsync(Ct);
        if (scheduler.RunningTask(name) is { } running)
        {
            await running;
        }
    }

    // Makes the task due on its row, behind the scheduler's back, and has the scheduler forget the due
    // time it was told, as it would only after a completion.
    private async Task MakeDueAsync(ClusterTaskScheduler<TestContext> scheduler, string name)
    {
        scheduler.ForgetDueTimes();
        await using var ctx = _fixture.CreateContext();
        await ctx.Set<ClusterTask>()
            .Where(x => x.Name == name)
            .ExecuteUpdateAsync(
                x => x
                    .SetProperty(y => y.NextRunAt, DateTime.UnixEpoch)
                    .SetProperty(y => y.LeaseUntil, (DateTime?)null),
                Ct);
    }

    private async Task<ClusterTask> ReadAsync(string name)
    {
        await using var ctx = _fixture.CreateContext();

        return await ctx.Set<ClusterTask>().AsNoTracking().SingleAsync(x => x.Name == name, Ct);
    }

    private sealed class PinnedTask(TimeSpan interval) : IServerTask
    {
        public const string TaskName = "pinned";

        private int _runs;

        public BarrierSignal Barrier { get; } = new();

        public int Runs => Volatile.Read(ref _runs);

        public bool Cancelled { get; private set; }

        public string Name => TaskName;

        public string? LockKey => ClusterTaskSchedulerTestsBase.TaskLockKey;

        public TimeSpan? DefaultInterval => interval;

        public bool RerunImmediately => false;

        public async Task<string?> ExecuteAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _runs);
            Barrier.Running.Release();
            try
            {
                await Barrier.CanFinish.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }

            return null;
        }
    }

    // A timer-only task pinned in its run by a barrier shared with others, under a lock key of its own.
    private sealed class SlotTask(string name, BarrierSignal barrier) : IServerTask
    {
        private int _runs;

        public int Runs => Volatile.Read(ref _runs);

        public string Name => name;

        public string? LockKey => "warp:test:slot:" + name;

        public TimeSpan? DefaultInterval => TimeSpan.FromHours(1);

        public bool RerunImmediately => false;

        public async Task<string?> ExecuteAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _runs);
            barrier.Running.Release();
            await barrier.CanFinish.WaitAsync(ct);

            return null;
        }
    }

    private sealed class IdleTask(TimeSpan interval) : IServerTask
    {
        public const string TaskName = "idle";

        private int _runs;

        public int Runs => Volatile.Read(ref _runs);

        public string Name => TaskName;

        public string? LockKey => ClusterTaskSchedulerTestsBase.TaskLockKey;

        public TimeSpan? DefaultInterval => interval;

        public Task<string?> ExecuteAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _runs);

            return Task.FromResult<string?>(null);
        }
    }

    private sealed class SignaledTask(TimeSpan interval) : IServerTask
    {
        public const string TaskName = "signaled";

        private int _runs;

        public SemaphoreSlim Ran { get; } = new(0);

        public int Runs => Volatile.Read(ref _runs);

        public string Name => TaskName;

        public string? LockKey => ClusterTaskSchedulerTestsBase.TaskLockKey;

        public TimeSpan? DefaultInterval => interval;

        public IEnumerable<ServerTaskSignal> Signals => [ServerTaskSignal.JobFinalized];

        public Task<string?> ExecuteAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _runs);
            Ran.Release();

            return Task.FromResult<string?>(null);
        }
    }

    // Records, per run, whether the runner said its schedule started it.
    private sealed class ScheduleAwareTask : IServerTask, IScheduledRunAware
    {
        public const string TaskName = "schedule-aware";

        private bool _scheduled;

        public ConcurrentQueue<bool> Runs { get; } = new();

        public string Name => TaskName;

        public string? LockKey => ClusterTaskSchedulerTestsBase.TaskLockKey;

        public TimeSpan? DefaultInterval => TimeSpan.FromHours(1);

        public IEnumerable<ServerTaskSignal> Signals => [ServerTaskSignal.JobFinalized];

        public void BeginRun(bool scheduled) => _scheduled = scheduled;

        public Task<string?> ExecuteAsync(CancellationToken ct)
        {
            Runs.Enqueue(_scheduled);

            return Task.FromResult<string?>(null);
        }
    }

    // A fake clock that reports every timer started on it, so a test can tell when a run begins its hold.
    private sealed class TimerWatchingTimeProvider : FakeTimeProvider
    {
        private readonly Channel<TimeSpan> _started = Channel.CreateUnbounded<TimeSpan>();
        private readonly ConcurrentQueue<TimeSpan> _timers = new();

        public TimerWatchingTimeProvider()
            : base(DateTimeOffset.UtcNow)
        {
        }

        public IEnumerable<TimeSpan> Timers => _timers;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _timers.Enqueue(dueTime);
            _started.Writer.TryWrite(dueTime);

            return base.CreateTimer(callback, state, dueTime, period);
        }

        public async Task<bool> WaitForTimerAsync(Func<TimeSpan, bool> match, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                while (true)
                {
                    if (match(await _started.Reader.ReadAsync(timeout.Token)))
                    {
                        return true;
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false;
            }
        }
    }

    private sealed class NoopLockProvider : IWarpLockProvider
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(string name, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult<IAsyncDisposable?>(new NoopHandle());

        private sealed class NoopHandle : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
