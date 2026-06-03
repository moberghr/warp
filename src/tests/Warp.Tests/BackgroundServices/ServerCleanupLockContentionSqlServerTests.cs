using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.BackgroundServices;
using Warp.Core.Data.Entities;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;

namespace Warp.Tests.BackgroundServices;

/// <summary>
/// SQL Server sibling of <see cref="ServerCleanupLockContentionTests"/>. Same ServerCleanup
/// foreign-key race: the cleanup lock must block a concurrent BackgroundServiceInstance insert
/// for a server being cleaned up, or that insert orphans the FK and crashes ServerCleanup.
/// SQL Server uses <c>SET LOCK_TIMEOUT</c>, and a blocked request raises error 1222
/// ("Lock request time out period exceeded"), which makes "blocked" deterministically observable.
/// </summary>
[Trait("Category", "SqlServer")]
public class ServerCleanupLockContentionSqlServerTests : IAsyncLifetime, IClassFixture<SqlServerClassFixture>
{
    private readonly SqlServerClassFixture _fixture;
    private static readonly Guid StaleServerId = Guid.NewGuid();

    public ServerCleanupLockContentionSqlServerTests(SqlServerClassFixture fixture) => _fixture = fixture;

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
        await using var lockCtx = _fixture.CreateContext();
        await using var lockTx = await lockCtx.Database.BeginTransactionAsync(TestCancellation);
        var queries = TestTasks.QueriesFor(lockCtx);
        await queries.LockAllServersAsync(lockCtx, TestCancellation);

        await using var insertCtx = _fixture.CreateContext();
        await using var insertTx = await insertCtx.Database.BeginTransactionAsync(TestCancellation);
        await insertCtx.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT 1000", TestCancellation);
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

        var insert = async () => await insertCtx.SaveChangesAsync(TestCancellation);

        // A held cleanup lock that blocks the insert trips SQL Server error 1222. If the lock is
        // too weak (compatible with the insert's FK-check lock), the insert proceeds and nothing
        // is thrown — failing this assertion, exactly as the orphaning race would in production.
        var ex = await Should.ThrowAsync<Exception>(insert);
        ex.ToString().ShouldContain("1222");

        await insertTx.RollbackAsync(TestCancellation);
        await lockTx.RollbackAsync(TestCancellation);
    }

    private static CancellationToken TestCancellation => Xunit.TestContext.Current.CancellationToken;
}
