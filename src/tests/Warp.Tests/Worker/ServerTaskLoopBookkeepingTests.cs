using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Events;
using Warp.Tests.Fixtures;
using Warp.Worker.Services;

namespace Warp.Tests.Worker;

// Bookkeeping-overhead regressions:
//   1. GetIntervalAsync caches the interval_seconds DB read for ~1 minute so each task
//      iteration doesn't issue a fresh SELECT when nothing has changed.
//   2. TryUpdateServerTaskAsync flushes every call — operators want LastRun to reflect
//      "the task is alive right now", not "the task last did real work N minutes ago".
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
    public async Task TryUpdateServerTaskAsync_ConsecutiveCalls_AlwaysFlush()
    {
        var serverTaskId = await SeedServerTaskAsync(intervalSeconds: 1.0);
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var loop = BuildLoop(time);
        loop.SetServerTaskIdForTest(serverTaskId);

        await loop.TryUpdateServerTaskAsync("Completed", null, 100);
        var afterFirst = await ReadAsync(serverTaskId);
        afterFirst.LastStatus.ShouldBe("Completed");
        afterFirst.LastDurationMs.ShouldBe(100);

        // Second call with no time elapsed — must still flush. Operators want LastRun
        // to track "the task is alive right now", not "last did real work N minutes ago".
        await loop.TryUpdateServerTaskAsync("Completed", null, 200);
        var afterSecond = await ReadAsync(serverTaskId);
        afterSecond.LastDurationMs.ShouldBe(200);
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
