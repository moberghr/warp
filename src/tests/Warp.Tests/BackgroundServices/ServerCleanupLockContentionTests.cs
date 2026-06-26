using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.BackgroundServices;
using Warp.Core.Data.Entities;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;

namespace Warp.Tests.BackgroundServices;

/// <summary>
/// Regression for the ServerCleanup foreign-key race (1.0.0). ServerCleanup locks server rows
/// via <c>LockAllServersAsync</c>, deletes a stale server's <c>BackgroundServiceInstance</c> rows,
/// then the server itself — all in one transaction. While that lock was <c>FOR NO KEY UPDATE</c>,
/// it was *compatible* with the <c>FOR KEY SHARE</c> lock a child INSERT takes on its parent, so a
/// still-alive-but-stale server's <c>BackgroundServiceStateService</c> could insert a fresh instance
/// row referencing the server between cleanup's instance-SELECT and its server-DELETE — orphaning the
/// FK and crashing ServerCleanup with <c>23503</c>. The cleanup lock must instead block concurrent
/// child inserts for servers under cleanup.
///
/// This is a Postgres lock-mode behaviour, so it is asserted directly against the lock query and
/// proven deterministically with <c>lock_timeout</c> (a blocked insert raises 55P03 rather than
/// hanging) — no sleeps, no spray.
/// </summary>
[Trait("Category", "PostgreSql")]
public class ServerCleanupLockContentionTests : IAsyncLifetime, IClassFixture<PostgreSqlClassFixture>
{
    private readonly PostgreSqlClassFixture _fixture;
    private static readonly Guid StaleServerId = Guid.NewGuid();

    public ServerCleanupLockContentionTests(PostgreSqlClassFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetAsync();
        await _fixture.SeedServerAsync(StaleServerId, "stale-server");

        var ctx = _fixture.CreateContext();
        ctx.Set<BackgroundServiceDefinition>().Add(new BackgroundServiceDefinition
        {
            Name = "Svc",
            DeclaredScope = ServiceScope.PerServer,
            FirstSeenAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync(TestCancellation);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task LockAllServers_HeldDuringCleanup_BlocksConcurrentInstanceInsert()
    {
        // tx1 takes the exact lock ServerCleanup holds while it deletes a stale server.
        await using var lockCtx = _fixture.CreateContext();
        await using var lockTx = await lockCtx.Database.BeginTransactionAsync(TestCancellation);
        var queries = TestTasks.QueriesFor(lockCtx);
        await queries.LockAllServersAsync(lockCtx, TestCancellation);

        // A concurrent transaction (a stale-but-alive server's BackgroundServiceStateService)
        // inserts a fresh instance for the locked server. A blocking lock makes that insert
        // wait, and a short lock_timeout turns "waited" into a deterministic 55P03 rather than
        // an indefinite hang.
        await using var insertCtx = _fixture.CreateContext();
        await using var insertTx = await insertCtx.Database.BeginTransactionAsync(TestCancellation);
        await insertCtx.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '1s'", TestCancellation);
        insertCtx.Set<BackgroundServiceInstance>().Add(new BackgroundServiceInstance
        {
            ServerId = StaleServerId,
            ServiceName = "Svc",
            DeclaredScope = ServiceScope.PerServer,
            Status = BackgroundServiceStatus.Running,
            StartedAt = DateTime.UtcNow,
            LastHeartbeatAt = DateTime.UtcNow,
            RestartCount = 0,
        });

        async Task<int> Insert() => await insertCtx.SaveChangesAsync(TestCancellation);

        // FOR NO KEY UPDATE (pre-fix): the insert's FOR KEY SHARE is compatible with the held
        // lock, so it proceeds immediately and nothing is thrown — this assertion fails.
        // FOR UPDATE (post-fix): the insert blocks on the held lock and trips lock_timeout.
        var ex = await Should.ThrowAsync<Exception>((Func<Task<int>>)Insert);

        // The insert must have been blocked by the held cleanup lock and tripped lock_timeout
        // (55P03). Assert on the exception chain rather than a specific wrapper type — EF surfaces
        // an in-SaveChanges lock timeout through more than one wrapper across versions.
        ex.ToString().ShouldContain("55P03");

        await insertTx.RollbackAsync(TestCancellation);
        await lockTx.RollbackAsync(TestCancellation);
    }

    private static CancellationToken TestCancellation => Xunit.TestContext.Current.CancellationToken;
}
