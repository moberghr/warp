using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Worker.Services;

namespace Warp.Tests.Worker;

// Pins ServerTaskLoop's lock-routing decision. The production code in
// TryAcquireLockAndExecuteAsync picks between three paths based on LockKey + the
// LocksWithTransaction flag: xact-scoped advisory lock when both are set, Medallion
// session lock when only LockKey is set, and direct execute when neither is set.
//
// Existing ServerTaskLoopBookkeepingTests use a stub whose LockKey is null, which
// short-circuits both lock branches and leaves the routing decision uncovered. This file
// exercises the routing with a stub task that owns a real LockKey, proving:
//   - xact-lock branch commits work via the ambient transaction (F2)
//   - xact-lock contention causes the second caller to skip its body entirely (F3)
[GenerateDatabaseTests]
public abstract class ServerTaskLoopRoutingTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected ServerTaskLoopRoutingTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task RunOnceAsync_LockKeySetAndLocksWithTransactionTrue_CommitsWorkViaXactPath()
    {
        // The task inserts a Job inside its body using the scoped DbContext. If routing
        // takes the xact-lock branch, the SaveChangesAsync inside the task joins the outer
        // RunUnderTransactionLockAsync transaction and the row is visible after the
        // primitive commits. If routing accidentally fell through to the Medallion path,
        // the task would still work (Medallion holds the lock externally) — so this test
        // distinguishes the two by asserting the work succeeds AND by checking that the
        // task body received a non-null `IWarpSqlQueries`-provided ambient transaction
        // signal (the xact path passes a context whose Database.CurrentTransaction is set).
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

        var loop = BuildLoop(stub);
        await loop.RunOnceAsync(CancellationToken.None);

        sawAmbientTransaction.ShouldBeTrue("xact-lock path must run the body inside an ambient transaction");

        await using var readCtx = _fixture.CreateContext();
        (await readCtx.Set<Job>().AsNoTracking().AnyAsync(j => j.Id == jobId)).ShouldBeTrue();
    }

    [TimedFact]
    public async Task RunOnceAsync_LockHeldByAnother_SkipsBodyAndReturnsWithoutCommit()
    {
        // Holder parks inside its body on a TCS gate to keep the xact-lock open; second
        // caller on the same key must observe `LockHeld = false` from RunUnderTransactionLock
        // and NOT execute the body. If the routing accidentally fell through to the no-lock
        // branch, the body would run twice and the second insert would either succeed
        // (no concurrency token on Job) or PK-violate. Either way the assertion catches it.
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

        var holderLoop = BuildLoop(holder);
        var holderTask = Task.Run(() => holderLoop.RunOnceAsync(CancellationToken.None));

        await holderInside.Task;

        // Holder is parked inside the xact-lock. Contender must skip its body.
        var contenderLoop = BuildLoop(contender);
        await contenderLoop.RunOnceAsync(CancellationToken.None);

        contenderRan.ShouldBeFalse("contender must skip body when xact-lock is held by another");

        holderRelease.SetResult();
        await holderTask;
    }

    private ServerTaskLoop<TestContext> BuildLoop(StubTask stub)
    {
        return new ServerTaskLoop<TestContext>(
            stub,
            new TaskAwareScopeFactory(_fixture, stub),
            new NoopLockProvider(),
            TimeProvider.System,
            Guid.NewGuid(),
            NullLogger.Instance);
    }

    // Test-side IServerTask. Body closure is the test's hook into the scoped DbContext
    // the production code creates inside ServerTaskLoop. Mirrors the production task
    // shape (single-transaction work, returns a status message) but stays minimal.
    private sealed class StubTask : IServerTask
    {
        public string Name => "routing-stub";

        public string? LockKey { get; init; }

        public TimeSpan? DefaultInterval => TimeSpan.FromSeconds(1);

        public bool LocksWithTransaction { get; init; } = true;

        public required Func<TestContext, CancellationToken, Task> Body { get; init; }

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

                // The loop drains + dispatches operational events post-commit (§8.25); AddWarp registers both
                // in production, so this minimal test double must supply them too.
                if (serviceType == typeof(Warp.Core.Notifiers.PendingOperationalEvents))
                {
                    return new Warp.Core.Notifiers.PendingOperationalEvents();
                }

                if (serviceType == typeof(Warp.Core.Notifiers.WarpNotifierDispatcher))
                {
                    return new Warp.Core.Notifiers.WarpNotifierDispatcher(
                        [],
                        Microsoft.Extensions.Logging.Abstractions.NullLogger<Warp.Core.Notifiers.WarpNotifierDispatcher>.Instance);
                }

                return null;
            }
        }
    }
}
