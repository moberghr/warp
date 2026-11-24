using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Warp.Core;
using Warp.Core.Data;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;
using Warp.Worker.Services;

namespace Warp.Tests.Worker;

/// <summary>
/// The cluster task scheduler (rule 6.20): one server per tick runs a cluster-wide task, an idle task
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
        task.Barrier.CanFinish.Release();
        await first.RunningTask(PinnedTask.TaskName)!;

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
        await scheduler.RunningTask(IdleTask.TaskName)!;
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
        await scheduler.RunningTask(PinnedTask.TaskName)!;
        task.Barrier.CanFinish.Release();
        await scheduler.PassAsync(Ct);
        await scheduler.RunningTask(PinnedTask.TaskName)!;

        // Assert: run again although its next scheduled run is an hour away.
        task.Runs.ShouldBe(2);
    }

    [TimedFact]
    public async Task PassAsync_SignalDuringTheStartupDelay_RunsOnlyTheSignaledTask()
    {
        // Each task waits its own share of the startup delay (rule 6.18); a signal ends that task's wait
        // alone. A shared wait let the first job's signal run every cluster task at once.
        var pinned = new PinnedTask(TimeSpan.FromHours(1));
        var idle = new IdleTask(TimeSpan.FromHours(1));
        using var scheduler = await BuildAsync([pinned, idle], TimeSpan.FromHours(1));
        scheduler.SpreadFirstRuns(() => 0.5);

        // Act
        scheduler.Signal(PinnedTask.TaskName);
        await scheduler.PassAsync(Ct);
        (await pinned.Barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue("the signaled task did not run");
        pinned.Barrier.CanFinish.Release();
        await scheduler.RunningTask(PinnedTask.TaskName)!;

        // Assert
        idle.Runs.ShouldBe(0);
        scheduler.RunningTask(IdleTask.TaskName).ShouldBeNull();
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
        await scheduler.RunningTask(PinnedTask.TaskName)!.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        task.Cancelled.ShouldBeTrue();
    }

    [TimedFact]
    public async Task PassAsync_ServerFromBeforeTheLeaseHoldsTheOldLock_TaskDoesNotRun()
    {
        // While a server from before the lease is alive, a run also takes the task's old lock; when that
        // server holds it, this server's run does nothing (rule 6.20, rolling deploy).
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

    private async Task<ClusterTaskScheduler<TestContext>> BuildAsync(IServerTask[] tasks, TimeSpan startupDelay)
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

        var scopes = provider.GetRequiredService<IServiceScopeFactory>();
        var scheduler = new ClusterTaskScheduler<TestContext>(
            tasks.Select(ServerTaskInfo.From),
            new ServerTaskRunner<TestContext>(scopes, new NoopLockProvider()),
            scopes,
            TimeProvider.System,
            Guid.NewGuid(),
            NullLogger.Instance,
            startupDelay,
            TimeSpan.FromMinutes(5));
        await scheduler.RegisterAsync(Ct);

        return scheduler;
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
