using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Worker.Services;

namespace Warp.Tests.Worker;

// The loop for a task every server runs for itself (the heartbeat), outside the lease scheduler (rule
// 6.23): a run that succeeds writes nothing, a failure leaves a ServerLog row with the task's name, and
// a signal ends the interval wait at once.
[GenerateDatabaseTests]
public abstract class PerServerTaskLoopTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected PerServerTaskLoopTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task RunOnceAsync_TaskSucceeds_WritesNothing()
    {
        // The heartbeat beat every five seconds, and each beat also updated a per-server status row that
        // said the same thing. What the heartbeat does is its record; a successful run writes nothing else.
        var task = new StubTask(_ => Task.FromResult<string?>(null));
        using var loop = BuildLoop(task, TimeSpan.Zero);

        // Act
        var didWork = await loop.RunOnceAsync(Ct);

        // Assert
        didWork.ShouldBeFalse();
        task.Runs.ShouldBe(1);
        await using var ctx = _fixture.CreateContext();
        (await ctx.Set<ServerLog>().CountAsync(Ct)).ShouldBe(0);
        (await ctx.Set<ServerTask>().CountAsync(Ct)).ShouldBe(0);
    }

    [TimedFact]
    public async Task RunOnceAsync_TaskThrows_LogsAFailureWithTheTaskName()
    {
        var task = new StubTask(_ => throw new InvalidOperationException("database unreachable"));
        var serverId = Guid.NewGuid();
        await using (var seed = _fixture.CreateContext())
        {
            seed.Set<Server>().Add(new Server { Id = serverId, StartedTime = DateTime.UtcNow, LastHeartbeatTime = DateTime.UtcNow });
            await seed.SaveChangesAsync(Ct);
        }

        using var loop = BuildLoop(task, TimeSpan.Zero, serverId);

        // Act
        await loop.RunOnceAsync(Ct);

        // Assert
        await using var ctx = _fixture.CreateContext();
        var log = await ctx.Set<ServerLog>().AsNoTracking().SingleAsync(Ct);
        log.TaskName.ShouldBe(StubTask.TaskName);
        log.Status.ShouldBe("Failed");
        log.Message.ShouldBe("database unreachable");
    }

    [TimedFact]
    public async Task RunAsync_WaitingForItsInterval_ASignalRunsItAtOnce()
    {
        // The first run is at once; the second would wait the one-hour interval. A signal ends that wait
        // at once (no signal interval configured here, rule 6.18).
        var ran = new SemaphoreSlim(0);
        var task = new StubTask(
            _ =>
            {
                ran.Release();

                return Task.FromResult<string?>(null);
            },
            TimeSpan.FromHours(1));
        using var loop = BuildLoop(task, TimeSpan.Zero);
        using var stop = new CancellationTokenSource();
        var running = loop.RunAsync(stop.Token);
        try
        {
            (await ran.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue("the first run did not happen at once");

            // Act
            loop.Signal();

            // Assert
            (await ran.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue("the signal did not cut the interval wait short");
        }
        finally
        {
            await stop.CancelAsync();
            await running;
        }
    }

    private PerServerTaskLoop<TestContext> BuildLoop(StubTask task, TimeSpan signalInterval, Guid? serverId = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _fixture.CreateContext());
        services.AddTestServerContext<TestContext>();
        services.AddSingleton(TestTasks.QueriesFor(_fixture.CreateContext()));
        services.AddSingleton<IServerTask>(task);
        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return new PerServerTaskLoop<TestContext>(
            ServerTaskInfo.From(task),
            new ServerTaskRunner<TestContext>(scopes, new NoopLockProvider()),
            scopes,
            TimeProvider.System,
            serverId ?? Guid.NewGuid(),
            NullLogger.Instance,
            signalInterval);
    }

    private sealed class StubTask(Func<CancellationToken, Task<string?>> body, TimeSpan? interval = null) : IServerTask
    {
        public const string TaskName = "PerServerStub";

        private int _runs;

        public int Runs => Volatile.Read(ref _runs);

        public string Name => TaskName;

        public string? LockKey => null;

        public TimeSpan? DefaultInterval => interval ?? TimeSpan.FromSeconds(1);

        public Task<string?> ExecuteAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _runs);

            return body(ct);
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
