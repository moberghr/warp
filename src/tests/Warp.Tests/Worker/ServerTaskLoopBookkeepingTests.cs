using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Events;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Worker.Services;

namespace Warp.Tests.Worker;

// Bookkeeping-overhead regressions:
//   1. GetIntervalAsync caches the interval_seconds DB read for ~1 minute so each task
//      iteration doesn't issue a fresh SELECT when nothing has changed.
//   2. TryUpdateServerTaskAsync records an idle run at most once a minute; a run that did
//      something, failed or changed status always records (rule 6.20).
//      ExpirationCleanup keeps server_log rows bounded (interval × 300s retention).
[GenerateDatabaseTests]
public abstract class ServerTaskLoopBookkeepingTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected ServerTaskLoopBookkeepingTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task GetIntervalAsync_WithinCacheWindow_ReturnsCachedValueWhenDbChanges()
    {
        var serverTaskId = await SeedServerTaskAsync(intervalSeconds: 5.0);
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var loop = BuildLoop(time);
        loop.SetServerTaskIdForTest(serverTaskId);

        var first = await loop.GetIntervalAsync(CancellationToken.None);
        first.ShouldBe(TimeSpan.FromSeconds(5));

        // Mutate the underlying row OUT-OF-BAND: a different process editing IntervalSeconds.
        // Within the cache window the loop should still return its cached value, not re-read.
        await using (var ctx = _fixture.CreateContext())
        {
            await ctx.Set<ServerTask>()
                .Where(x => x.Id == serverTaskId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IntervalSeconds, 999.0));
        }

        // 30s elapsed — still inside the 1m cache window.
        time.Advance(TimeSpan.FromSeconds(30));
        var second = await loop.GetIntervalAsync(CancellationToken.None);
        second.ShouldBe(TimeSpan.FromSeconds(5));
    }

    [TimedFact]
    public async Task GetIntervalAsync_AfterCacheExpires_RereadsFromDb()
    {
        var serverTaskId = await SeedServerTaskAsync(intervalSeconds: 5.0);
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var loop = BuildLoop(time);
        loop.SetServerTaskIdForTest(serverTaskId);

        await loop.GetIntervalAsync(CancellationToken.None);

        await using (var ctx = _fixture.CreateContext())
        {
            await ctx.Set<ServerTask>()
                .Where(x => x.Id == serverTaskId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IntervalSeconds, 999.0));
        }

        // Push past the 1m cache lifetime — next call should hit the DB.
        time.Advance(TimeSpan.FromMinutes(2));
        var refreshed = await loop.GetIntervalAsync(CancellationToken.None);

        refreshed.ShouldBe(TimeSpan.FromSeconds(999));
    }

    [TimedFact]
    public async Task TryUpdateServerTaskAsync_IdleRunsWithinAMinute_RecordOnlyTheFirst()
    {
        // An idle run records itself at most once a minute; the heartbeat's row was an UPDATE per server
        // every five seconds that said the same thing. A minute later it records again.
        var serverTaskId = await SeedServerTaskAsync(intervalSeconds: 1.0);
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var loop = BuildLoop(time);
        loop.SetServerTaskIdForTest(serverTaskId);

        await loop.TryUpdateServerTaskAsync("Completed", null, 100);
        (await ReadAsync(serverTaskId)).LastDurationMs.ShouldBe(100);

        // Act
        await loop.TryUpdateServerTaskAsync("Completed", null, 200);
        var withinTheMinute = await ReadAsync(serverTaskId);
        time.Advance(TimeSpan.FromMinutes(1));
        await loop.TryUpdateServerTaskAsync("Completed", null, 300);

        // Assert
        withinTheMinute.LastDurationMs.ShouldBe(100);
        (await ReadAsync(serverTaskId)).LastDurationMs.ShouldBe(300);
    }

    [TimedFact]
    public async Task TryUpdateServerTaskAsync_RunThatDidWorkOrChangedStatus_AlwaysRecords()
    {
        var serverTaskId = await SeedServerTaskAsync(intervalSeconds: 1.0);
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var loop = BuildLoop(time);
        loop.SetServerTaskIdForTest(serverTaskId);
        await loop.TryUpdateServerTaskAsync("Completed", null, 100);

        // Act
        await loop.TryUpdateServerTaskAsync("Completed", "did 3", 200);
        var afterWork = await ReadAsync(serverTaskId);
        await loop.TryUpdateServerTaskAsync("Failed", "boom", 300);
        var afterFailure = await ReadAsync(serverTaskId);
        await loop.TryUpdateServerTaskAsync("Completed", null, 400);

        // Assert
        afterWork.LastMessage.ShouldBe("did 3");
        afterFailure.LastStatus.ShouldBe("Failed");
        var recovered = await ReadAsync(serverTaskId);
        recovered.LastStatus.ShouldBe("Completed");
        recovered.LastDurationMs.ShouldBe(400);
    }

    [TimedFact]
    public async Task RunOneIterationAsync_TaskReturnsNull_WritesCompletedStatus()
    {
        // Regression: idle iterations used to write "Skipped" with a 5-min UPDATE throttle.
        // Now every iteration writes "Completed" so the dashboard reflects liveness.
        var serverTaskId = await SeedServerTaskAsync(intervalSeconds: 1.0);
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var loop = BuildLoop(time);
        loop.SetServerTaskIdForTest(serverTaskId);

        var didWork = await loop.RunOneIterationAsync(CancellationToken.None);

        didWork.ShouldBeFalse("null message means no work, so RerunImmediately must not tight-loop");
        var row = await ReadAsync(serverTaskId);
        row.LastStatus.ShouldBe("Completed");
        row.LastMessage.ShouldBeNull();
    }

    [TimedFact]
    public async Task RunAsync_WokenAgainWithinTheSignalInterval_RunsAgainWhenItEnds()
    {
        // Wakes are folded into at most one pass per signal interval (rule 6.18), but a wake that arrives
        // during the hold must not be lost: it is still pending, so the next pass runs when the hold ends.
        var serverTaskId = await SeedServerTaskAsync(intervalSeconds: 3600);
        var task = new CountingTask { Signals = [ServerTaskSignal.JobFinalized] };
        using var loop = new ServerTaskLoop<TestContext>(
            task,
            new FixtureScopeFactory(_fixture, task),
            new NoopLockProvider(),
            TimeProvider.System,
            Guid.NewGuid(),
            NullLogger.Instance,
            signalInterval: TimeSpan.FromMilliseconds(500));
        loop.SetServerTaskIdForTest(serverTaskId);

        using var stop = new CancellationTokenSource();
        var running = loop.RunAsync(stop.Token);
        try
        {
            // The first pass runs at once.
            (await task.Ran.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken))
                .ShouldBeTrue("the first pass did not run");

            // Act
            loop.Signal();

            // Assert
            (await task.Ran.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken))
                .ShouldBeTrue("a wake during the hold was lost");
        }
        finally
        {
            await stop.CancelAsync();
            await running;
        }
    }

    [TimedFact]
    public async Task RunAsync_WokenWithinTheSignalInterval_DoesNotRunUntilTheClockPassesIt()
    {
        // The hold is on the loop's clock. An hour-long interval on a fake clock: nothing but advancing
        // that clock can end the hold, so a loop without a hold, or with one on the wall clock, fails here.
        var serverTaskId = await SeedServerTaskAsync(intervalSeconds: 36_000);
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var task = new CountingTask { Signals = [ServerTaskSignal.JobFinalized] };
        using var loop = new ServerTaskLoop<TestContext>(
            task,
            new FixtureScopeFactory(_fixture, task),
            new NoopLockProvider(),
            time,
            Guid.NewGuid(),
            NullLogger.Instance,
            signalInterval: TimeSpan.FromHours(1));
        loop.SetServerTaskIdForTest(serverTaskId);
        using var stop = new CancellationTokenSource();
        var running = loop.RunAsync(stop.Token);
        try
        {
            (await task.Ran.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken))
                .ShouldBeTrue("the first pass did not run");

            // Act + Assert: a wake during the hold waits for it.
            loop.Signal();
            (await task.Ran.WaitAsync(TimeSpan.FromMilliseconds(400), Xunit.TestContext.Current.CancellationToken))
                .ShouldBeFalse("a wake within the signal interval ran at once");

            // The clock passes the interval: the pending wake runs.
            time.Advance(TimeSpan.FromHours(1));
            (await task.Ran.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken))
                .ShouldBeTrue("the wake did not run when the hold ended");
        }
        finally
        {
            await stop.CancelAsync();
            await running;
        }
    }

    [TimedFact]
    public void SignalHold_TaskThatNoSignalWakes_HasNone()
    {
        // Heartbeat, scheduled activation and the other timer-only tasks keep their configured cadence: the
        // hold folds wakes, and nothing wakes them.
        var timerOnly = new CountingTask();
        using var timerLoop = new ServerTaskLoop<TestContext>(timerOnly, new FixtureScopeFactory(_fixture, timerOnly), new NoopLockProvider(), TimeProvider.System, Guid.NewGuid(), NullLogger.Instance, signalInterval: TimeSpan.FromMilliseconds(500));
        var signalled = new CountingTask { Signals = [ServerTaskSignal.JobFinalized] };
        using var signalledLoop = new ServerTaskLoop<TestContext>(signalled, new FixtureScopeFactory(_fixture, signalled), new NoopLockProvider(), TimeProvider.System, Guid.NewGuid(), NullLogger.Instance, signalInterval: TimeSpan.FromMilliseconds(500));

        timerLoop.SignalHold.ShouldBe(TimeSpan.Zero);
        signalledLoop.SignalHold.ShouldBe(TimeSpan.FromMilliseconds(500));
    }

    [TimedTheory]
    [InlineData(10_000, 400, 9_600)]
    [InlineData(200, 200, 0)]
    [InlineData(200, 0, 200)]
    public void WaitAfterHold_TheHoldCountsTowardsTheTasksInterval(int intervalMs, int holdMs, int expectedMs)
    {
        // Coalescing wakes must not lengthen the configured cadence: a pass that held for part of its
        // interval waits only for the rest.
        ServerTaskLoop<TestContext>.WaitAfterHold(TimeSpan.FromMilliseconds(intervalMs), TimeSpan.FromMilliseconds(holdMs))
            .ShouldBe(TimeSpan.FromMilliseconds(expectedMs));
    }

    [TimedTheory]
    [InlineData(500, 100, 3600, 400)]
    [InlineData(500, 600, 3600, 0)]
    [InlineData(500, 100, 0.2, 200)]
    [InlineData(0, 100, 3600, 0)]
    public void HoldBeforeNextRun_RestOfTheSignalIntervalCappedByTheTaskInterval(int signalMs, int sincePassStartedMs, double intervalSeconds, int expectedMs)
    {
        var hold = ServerTaskLoop<TestContext>.HoldBeforeNextRun(
            TimeSpan.FromMilliseconds(signalMs),
            TimeSpan.FromMilliseconds(sincePassStartedMs),
            TimeSpan.FromSeconds(intervalSeconds));

        hold.ShouldBe(TimeSpan.FromMilliseconds(expectedMs));
    }

    private async Task<int> SeedServerTaskAsync(double intervalSeconds)
    {
        await using var ctx = _fixture.CreateContext();
        var serverId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        ctx.Set<Server>().Add(new Server
        {
            Id = serverId,
            StartedTime = now,
            LastHeartbeatTime = now,
        });
        var entity = new ServerTask
        {
            ServerId = serverId,
            TaskName = $"test-task-{Guid.NewGuid()}",
            IntervalSeconds = intervalSeconds,
        };
        ctx.Set<ServerTask>().Add(entity);
        await ctx.SaveChangesAsync();

        return entity.Id;
    }

    private async Task<ServerTask> ReadAsync(int id)
    {
        await using var ctx = _fixture.CreateContext();

        return await ctx.Set<ServerTask>().AsNoTracking().FirstAsync(x => x.Id == id);
    }

    private ServerTaskLoop<TestContext> BuildLoop(TimeProvider time)
    {
        var stub = new StubTask();

        return new ServerTaskLoop<TestContext>(
            stub,
            new FixtureScopeFactory(_fixture, stub),
            new NoopLockProvider(),
            time,
            Guid.NewGuid(),
            NullLogger.Instance);
    }

    private sealed class StubTask : IServerTask
    {
        public string Name => "Stub";

        public string? LockKey => null;

        public TimeSpan? DefaultInterval => TimeSpan.FromSeconds(1);

        public Task<string?> ExecuteAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private sealed class CountingTask : IServerTask
    {
        public SemaphoreSlim Ran { get; } = new(0);

        public IEnumerable<ServerTaskSignal> Signals { get; init; } = [];

        public string Name => "Counting";

        public string? LockKey => null;

        public TimeSpan? DefaultInterval => TimeSpan.FromHours(1);

        public Task<string?> ExecuteAsync(CancellationToken ct)
        {
            Ran.Release();

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

    private sealed class FixtureScopeFactory : IServiceScopeFactory
    {
        private readonly IDatabaseFixture _fixture;
        private readonly IServerTask _task;

        public FixtureScopeFactory(IDatabaseFixture fixture, IServerTask task)
        {
            _fixture = fixture;
            _task = task;
        }

        public IServiceScope CreateScope() => new FixtureScope(_fixture, _task);

        private sealed class FixtureScope : IServiceScope, IAsyncDisposable
        {
            private readonly TestContext _context;

            public FixtureScope(IDatabaseFixture fixture, IServerTask task)
            {
                _context = fixture.CreateContext();
                ServiceProvider = new ContextProvider(_context, task);
            }

            public IServiceProvider ServiceProvider { get; }

            public void Dispose() => _context.Dispose();

            public ValueTask DisposeAsync() => _context.DisposeAsync();
        }

        private sealed class ContextProvider : IServiceProvider
        {
            private readonly TestContext _context;
            private readonly IServerTask _task;

            public ContextProvider(TestContext context, IServerTask task)
            {
                _context = context;
                _task = task;
            }

            public object? GetService(Type serviceType)
            {
                if (serviceType == typeof(TestContext))
                {
                    return _context;
                }

                // ServerTaskLoop's bookkeeping (ServerTask registration/interval/status, ServerLog)
                // runs on the server context (§2.14), backed here by the fixture's TestContext.
                if (serviceType == typeof(IWarpServerContext))
                {
                    return new TestServerContext(_context);
                }

                if (serviceType == typeof(IEnumerable<IServerTask>))
                {
                    return new[] { _task };
                }

                return null;
            }
        }
    }

    // Minimal monotonic time provider for tests — we don't need scheduler integration
    // (no Task.Delay anywhere in the throttle / cache paths), just GetUtcNow control.
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public FakeTimeProvider(DateTimeOffset start) => _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
