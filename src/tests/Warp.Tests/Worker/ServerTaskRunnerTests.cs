using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Events;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Worker.Services;

namespace Warp.Tests.Worker;

// Pins how ServerTaskRunner executes a task (rule 6.23): a task that LocksWithTransaction runs in one
// transaction and its post-commit hook runs after the commit; a throw rolls back and skips the hook; and
// under the rolling-deploy lock (LegacyTaskLock), a caller that finds the lock held skips the body.
[GenerateDatabaseTests]
public abstract class ServerTaskRunnerTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected ServerTaskRunnerTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task RunAsync_LocksWithTransaction_CommitsTheWorkInOneTransaction()
    {
        // The task inserts a Job through the scoped server context: its SaveChanges joins the transaction
        // the runner opened, and the row is visible once that commits.
        var lockKey = "warp:test:routing-xact-" + Guid.NewGuid().ToString("N");
        var jobId = Guid.NewGuid();
        var sawAmbientTransaction = false;

        var stub = new StubTask
        {
            LockKey = lockKey,
            LocksWithTransaction = true,
            Body = (ctx, ct) =>
            {
                sawAmbientTransaction = ctx.Database.CurrentTransaction != null;
                ctx.Set<Job>().Add(new Job
                {
                    Id = jobId,
                    Kind = JobKind.Job,
                    CurrentState = State.Enqueued,
                    Queue = "default",
                    Type = "TestType",
                    Message = "{}",
                    CreateTime = DateTime.UtcNow,
                    ScheduleTime = DateTime.UtcNow,
                });

                return ctx.SaveChangesAsync(ct);
            },
        };

        await BuildRunner(stub).RunAsync(ServerTaskInfo.From(stub), underLegacyLock: false, CancellationToken.None);

        sawAmbientTransaction.ShouldBeTrue("a task that LocksWithTransaction must run inside a transaction");

        await using var readCtx = _fixture.CreateContext();
        (await readCtx.Set<Job>().AsNoTracking().AnyAsync(j => j.Id == jobId)).ShouldBeTrue();
    }

    [TimedFact]
    public async Task RunAsync_UnderTheLegacyLockHeldByAnother_SkipsTheBody()
    {
        // During a rolling deploy each run also takes the task's old lock. The holder parks inside its body
        // to keep that lock open; a second caller on the same key must not run its body.
        var lockKey = "warp:test:routing-contended-" + Guid.NewGuid().ToString("N");
        var holderInside = new TaskCompletionSource();
        var holderRelease = new TaskCompletionSource();
        var contenderRan = false;

        var holder = new StubTask
        {
            LockKey = lockKey,
            LocksWithTransaction = true,
            Body = async (_, _) =>
            {
                holderInside.SetResult();
                await holderRelease.Task;
            },
        };
        var contender = new StubTask
        {
            LockKey = lockKey,
            LocksWithTransaction = true,
            Body = (_, _) =>
            {
                contenderRan = true;

                return Task.CompletedTask;
            },
        };

        var holderTask = Task.Run(() => BuildRunner(holder).RunAsync(ServerTaskInfo.From(holder), underLegacyLock: true, CancellationToken.None));

        await holderInside.Task;

        var (ran, _) = await BuildRunner(contender).RunAsync(ServerTaskInfo.From(contender), underLegacyLock: true, CancellationToken.None);

        ran.ShouldBeFalse();
        contenderRan.ShouldBeFalse("a caller that finds the legacy lock held must skip the body");

        holderRelease.SetResult();
        await holderTask;
    }

    [TimedFact]
    public async Task RunAsync_TaskSucceeds_CallsOnCommittedAsyncAfterTheCommit()
    {
        // The post-commit hook (§8.25) must fire after the task's transaction commits, and by then the
        // task's own work is durable. Assert the hook ran AND that at hook time the committed Job is visible
        // in a fresh connection (i.e. genuinely post-commit).
        var lockKey = "warp:test:routing-oncommitted-" + Guid.NewGuid().ToString("N");
        var jobId = Guid.NewGuid();
        var visibleAtHook = false;

        var stub = new StubTask
        {
            LockKey = lockKey,
            LocksWithTransaction = true,
            Body = (ctx, ct) =>
            {
                ctx.Set<Job>().Add(new Job
                {
                    Id = jobId,
                    Kind = JobKind.Job,
                    CurrentState = State.Enqueued,
                    Queue = "default",
                    Type = "TestType",
                    Message = "{}",
                    CreateTime = DateTime.UtcNow,
                    ScheduleTime = DateTime.UtcNow,
                });

                return ctx.SaveChangesAsync(ct);
            },
            OnCommitted = () =>
            {
                using var probe = _fixture.CreateContext();
                visibleAtHook = probe.Set<Job>().AsNoTracking().Any(j => j.Id == jobId);
            },
        };

        await BuildRunner(stub).RunAsync(ServerTaskInfo.From(stub), underLegacyLock: false, CancellationToken.None);

        visibleAtHook.ShouldBeTrue("OnCommittedAsync must run after the task's work is committed and visible");
    }

    [TimedFact]
    public async Task RunAsync_TaskThrows_DoesNotCallOnCommittedAsync()
    {
        // The core post-commit guarantee: if ExecuteAsync throws, the transaction rolls back and the host must
        // NOT invoke the post-commit hook — so a task that buffers an operational event and dispatches it in
        // OnCommittedAsync never alerts for a change that rolled back. Guards the regression the notifier fix
        // addressed.
        var lockKey = "warp:test:routing-throw-" + Guid.NewGuid().ToString("N");
        var hookCalled = false;

        var stub = new StubTask
        {
            LockKey = lockKey,
            LocksWithTransaction = true,
            Body = (_, _) => throw new InvalidOperationException("boom"),
            OnCommitted = () => hookCalled = true,
        };

        await Should.ThrowAsync<InvalidOperationException>(
            () => BuildRunner(stub).RunAsync(ServerTaskInfo.From(stub), underLegacyLock: false, CancellationToken.None));

        hookCalled.ShouldBeFalse("a rolled-back iteration must not run the post-commit hook");
    }

    [TimedFact]
    public async Task RunAsync_WorkRolledBackAfterTheOrchestratorReturned_GivesItsParentsBack()
    {
        // A commit that fails, or a run cancelled for a lost lease, comes after the Orchestrator's
        // ExecuteAsync returned: its work is rolled back, and the parents the wakes named were taken by
        // that pass. They have to come back, or those parents wait for the next sweep (rule 6.11).
        var parent = Guid.NewGuid();
        var signals = SignalsNamingAParent(parent);
        await using var provider = OrchestratorServices(signals, failAfterExecute: true);
        var runner = new ServerTaskRunner<TestContext>(provider.GetRequiredService<IServiceScopeFactory>(), new NoopLockProvider());

        // Act
        await Should.ThrowAsync<InvalidOperationException>(
            () => runner.RunAsync(OrchestratorInfo<FailsAfterTheOrchestrator>(), underLegacyLock: false, Xunit.TestContext.Current.CancellationToken));

        // Assert
        signals.TakeFinalizedParents(TimeSpan.FromDays(1)).Parents.ShouldBe([parent]);
    }

    [TimedFact]
    public async Task RunAsync_OrchestratorPassCommitted_KeepsNoParentsToRepeat()
    {
        var signals = SignalsNamingAParent(Guid.NewGuid());
        await using var provider = OrchestratorServices(signals, failAfterExecute: false);
        var runner = new ServerTaskRunner<TestContext>(provider.GetRequiredService<IServiceScopeFactory>(), new NoopLockProvider());

        // Act
        await runner.RunAsync(OrchestratorInfo<Orchestrator<TestContext>>(), underLegacyLock: false, Xunit.TestContext.Current.CancellationToken);

        // Assert
        var (parents, sweep) = signals.TakeFinalizedParents(TimeSpan.FromDays(1));
        parents.ShouldBeEmpty();
        sweep.ShouldBeFalse();
    }

    private static ServerTaskSignals<TestContext> SignalsNamingAParent(Guid parent)
    {
        var signals = new ServerTaskSignals<TestContext>();
        signals.TakeFinalizedParents(TimeSpan.FromDays(1));
        signals.SignalJobFinalized(parent);

        return signals;
    }

    private static ServerTaskInfo OrchestratorInfo<TTask>() =>
        new(typeof(TTask), "Orchestration", "warp:test:orchestration-" + Guid.NewGuid().ToString("N"), LocksWithTransaction: true, TimeSpan.FromDays(1), RerunImmediately: false, LogOnSuccess: false, []);

    // A real container, so the scope disposes the task it created, as the server's does.
    private ServiceProvider OrchestratorServices(ServerTaskSignals<TestContext> signals, bool failAfterExecute)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _fixture.CreateContext());
        services.AddTestServerContext<TestContext>();
        services.AddScoped(x => TestTasks.CreateOrchestrator(
            x.GetRequiredService<TestContext>(),
            TimeProvider.System,
            TimeSpan.FromDays(1),
            signals: signals,
            orchestrationInterval: TimeSpan.FromDays(1)));
        if (failAfterExecute)
        {
            services.AddScoped<IServerTask>(x => new FailsAfterTheOrchestrator(x.GetRequiredService<Orchestrator<TestContext>>()));
        }
        else
        {
            services.AddScoped<IServerTask>(x => x.GetRequiredService<Orchestrator<TestContext>>());
        }

        return services.BuildServiceProvider();
    }

    private ServerTaskRunner<TestContext> BuildRunner(StubTask stub) =>
        new(new TaskAwareScopeFactory(_fixture, stub), new NoopLockProvider());

    // Runs the Orchestrator's pass to its end and then fails, as a failed commit does: by then the
    // Orchestrator's own ExecuteAsync has returned.
    private sealed class FailsAfterTheOrchestrator(Orchestrator<TestContext> inner) : IServerTask
    {
        public string Name => inner.Name;

        public string? LockKey => inner.LockKey;

        public TimeSpan? DefaultInterval => inner.DefaultInterval;

        public async Task<string?> ExecuteAsync(CancellationToken ct)
        {
            await inner.ExecuteAsync(ct);

            throw new InvalidOperationException("the commit failed");
        }
    }

    // Test-side IServerTask. Body closure is the test's hook into the scoped DbContext
    // the production code creates inside ServerTaskRunner. Mirrors the production task
    // shape (single-transaction work, returns a status message) but stays minimal.
    private sealed class StubTask : IServerTask
    {
        public string Name => "routing-stub";

        public string? LockKey { get; init; }

        public TimeSpan? DefaultInterval => TimeSpan.FromSeconds(1);

        public bool LocksWithTransaction { get; init; } = true;

        public required Func<TestContext, CancellationToken, Task> Body { get; init; }

        public Action? OnCommitted { get; init; }

        public async Task<string?> ExecuteAsync(CancellationToken ct)
        {
            // The body closure can't reach the scoped context directly — ServerTaskLoop
            // creates the scope and resolves us *and* the context from it. The bridge is
            // the scope factory: it stamps the freshly-resolved TestContext into a thread-
            // local before resolving us so the body sees the same instance.
            var ctx = ScopedContextHolder.Current
                ?? throw new InvalidOperationException("body invoked outside a TaskAwareScopeFactory scope");
            await Body(ctx, ct);

            return "ok";
        }

        public Task OnCommittedAsync(CancellationToken ct)
        {
            OnCommitted?.Invoke();

            return Task.CompletedTask;
        }
    }

    // Pins the most-recently-created TestContext for the scope so StubTask.Body can pick
    // it up without taking a DI dep. The production code resolves DbContext and stub task
    // from the same scope; we mimic that with a per-scope AsyncLocal stash.
    private static class ScopedContextHolder
    {
        private static readonly AsyncLocal<TestContext?> _current = new();

        public static TestContext? Current
        {
            get => _current.Value;
            set => _current.Value = value;
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

    private sealed class TaskAwareScopeFactory : IServiceScopeFactory
    {
        private readonly IDatabaseFixture _fixture;
        private readonly StubTask _stub;

        public TaskAwareScopeFactory(IDatabaseFixture fixture, StubTask stub)
        {
            _fixture = fixture;
            _stub = stub;
        }

        public IServiceScope CreateScope() => new RoutingScope(_fixture, _stub);

        private sealed class RoutingScope : IServiceScope, IAsyncDisposable
        {
            private readonly TestContext _context;

            public RoutingScope(IDatabaseFixture fixture, StubTask stub)
            {
                _context = fixture.CreateContext();
                ScopedContextHolder.Current = _context;
                ServiceProvider = new RoutingServiceProvider(_context, stub);
            }

            public IServiceProvider ServiceProvider { get; }

            public void Dispose()
            {
                ScopedContextHolder.Current = null;
                _context.Dispose();
            }

            public async ValueTask DisposeAsync()
            {
                ScopedContextHolder.Current = null;
                await _context.DisposeAsync();
            }
        }

        private sealed class RoutingServiceProvider : IServiceProvider
        {
            private readonly TestContext _context;
            private readonly StubTask _stub;
            private readonly IWarpSqlQueries<TestContext> _queries;

            public RoutingServiceProvider(TestContext context, StubTask stub)
            {
                _context = context;
                _stub = stub;
                _queries = TestTasks.QueriesFor(context);
            }

            public object? GetService(Type serviceType)
            {
                if (serviceType == typeof(TestContext))
                {
                    return _context;
                }

                if (serviceType == typeof(IWarpSqlQueries<TestContext>))
                {
                    return _queries;
                }

                if (serviceType == typeof(Warp.Core.IWarpServerContext))
                {
                    return new TestServerContext(_context);
                }

                if (serviceType == typeof(IEnumerable<IServerTask>))
                {
                    return new IServerTask[] { _stub };
                }

                return null;
            }
        }
    }
}
