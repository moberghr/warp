using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Worker;

namespace Warp.Tests.BackgroundServices;

/// <summary>
/// Pins the contract of <see cref="IWarpSqlQueries{TContext}.HeartbeatAsync"/> with respect to
/// the BackgroundServices addon: the same round-trip that refreshes the server heartbeat also
/// renews held <c>BackgroundServiceLease</c> rows and bumps
/// <c>BackgroundServiceInstance.LastHeartbeatAt</c>, returning the renewed service names so
/// <see cref="Warp.Worker.Services.Heartbeat{TContext}"/> can detect lost leases.
/// </summary>
[GenerateDatabaseTests]
public abstract class LeaseRenewalTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;
    private static readonly Guid MyServerId = Guid.NewGuid();
    private static readonly Guid OtherServerId = Guid.NewGuid();

    protected LeaseRenewalTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    // NOTE: kept inline rather than using _fixture.SeedServerAsync — these tests deliberately
    // seed a server whose LastHeartbeatTime is 3s in the past (AddSeconds(-3)) to simulate
    // a server that has already sent at least one heartbeat, which the HeartbeatAsync renewal
    // WHERE clause validates.  The shared extension always uses DateTime.UtcNow for both
    // timestamps, which would not exercise the same invariant.
    private async Task SeedServerAsync(Guid serverId)
    {
        await using var ctx = _fixture.CreateContext();
        ctx.Set<Server>().Add(new Server
        {
            Id = serverId,
            StartedTime = DateTime.UtcNow.AddMinutes(-1),
            LastHeartbeatTime = DateTime.UtcNow.AddSeconds(-3),
        });
        await ctx.SaveChangesAsync(Ct);
    }

    private async Task SeedDefinitionAsync(string serviceName)
    {
        await using var ctx = _fixture.CreateContext();

        if (await ctx.Set<BackgroundServiceDefinition>().AnyAsync(x => x.Name == serviceName, Ct))
        {
            return;
        }

        ctx.Set<BackgroundServiceDefinition>().Add(new BackgroundServiceDefinition
        {
            Name = serviceName,
            DeclaredScope = Warp.Core.BackgroundServices.ServiceScope.Singleton,
            FirstSeenAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync(Ct);
    }

    private async Task SeedLeaseAsync(string serviceName, Guid holderId, DateTime expiresAt)
    {
        await using var ctx = _fixture.CreateContext();
        ctx.Set<BackgroundServiceLease>().Add(new BackgroundServiceLease
        {
            ServiceName = serviceName,
            HolderServerId = holderId,
            LeaseExpiresAt = expiresAt,
        });
        await ctx.SaveChangesAsync(Ct);
    }

    private async Task SeedInstanceAsync(Guid serverId, string serviceName)
    {
        await using var ctx = _fixture.CreateContext();
        ctx.Set<BackgroundServiceInstance>().Add(new BackgroundServiceInstance
        {
            ServerId = serverId,
            ServiceName = serviceName,
            Status = Warp.Core.BackgroundServices.BackgroundServiceStatus.Running,
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            LastHeartbeatAt = DateTime.UtcNow.AddSeconds(-5),
        });
        await ctx.SaveChangesAsync(Ct);
    }

    [TimedFact]
    public async Task HeartbeatAsync_HolderRenewsLease_LeaseExpiresAtAdvancesAndNameReturned()
    {
        await SeedServerAsync(MyServerId);
        await SeedDefinitionAsync("RenewalSvc");
        await SeedInstanceAsync(MyServerId, "RenewalSvc");

        var originalExpiry = DateTime.UtcNow.AddSeconds(10);
        await SeedLeaseAsync("RenewalSvc", MyServerId, originalExpiry);

        var queries = TestTasks.QueriesFor(_fixture.CreateContext());
        await using var ctx = _fixture.CreateContext();
        var result = await queries.HeartbeatAsync(ctx, MyServerId, DateTime.UtcNow, null, null, writeLocalTime: false, Ct);

        result.ShouldNotBeNull();
        result.RenewedBackgroundServiceLeases.ShouldContain("RenewalSvc");

        // Verify the lease was actually extended in the DB.
        var readCtx = _fixture.CreateContext();
        var lease = await readCtx.Set<BackgroundServiceLease>()
            .Where(x => x.ServiceName == "RenewalSvc")
            .FirstOrDefaultAsync(Ct);

        lease.ShouldNotBeNull();
        lease.LeaseExpiresAt.ShouldBeGreaterThan(originalExpiry);
    }

    [TimedFact]
    public async Task HeartbeatAsync_NotHolder_NoLeaseUpdate()
    {
        await SeedServerAsync(MyServerId);
        await SeedServerAsync(OtherServerId);
        await SeedDefinitionAsync("OtherHolderSvc");
        await SeedInstanceAsync(MyServerId, "OtherHolderSvc");

        var originalExpiry = DateTime.UtcNow.AddSeconds(20);
        await SeedLeaseAsync("OtherHolderSvc", OtherServerId, originalExpiry);

        var queries = TestTasks.QueriesFor(_fixture.CreateContext());
        await using var ctx = _fixture.CreateContext();
        var result = await queries.HeartbeatAsync(ctx, MyServerId, DateTime.UtcNow, null, null, writeLocalTime: false, Ct);

        result.ShouldNotBeNull();
        result.RenewedBackgroundServiceLeases.ShouldNotContain("OtherHolderSvc");

        // Verify the lease was NOT extended.
        var readCtx = _fixture.CreateContext();
        var lease = await readCtx.Set<BackgroundServiceLease>()
            .Where(x => x.ServiceName == "OtherHolderSvc")
            .FirstOrDefaultAsync(Ct);

        lease.ShouldNotBeNull();
        lease.LeaseExpiresAt.ShouldBe(originalExpiry, TimeSpan.FromSeconds(1));
    }

    [TimedFact]
    public async Task HeartbeatAsync_ExpiredLeaseOwnedByMe_NotRenewed()
    {
        // An already-expired lease should NOT be renewed — the renewal WHERE clause requires
        // lease_expires_at > now. This simulates a lease that expired between heartbeats.
        await SeedServerAsync(MyServerId);
        await SeedDefinitionAsync("ExpiredOwnSvc");
        await SeedInstanceAsync(MyServerId, "ExpiredOwnSvc");

        var alreadyExpired = DateTime.UtcNow.AddSeconds(-5);
        await SeedLeaseAsync("ExpiredOwnSvc", MyServerId, alreadyExpired);

        var queries = TestTasks.QueriesFor(_fixture.CreateContext());
        await using var ctx = _fixture.CreateContext();
        var result = await queries.HeartbeatAsync(ctx, MyServerId, DateTime.UtcNow, null, null, writeLocalTime: false, Ct);

        result.ShouldNotBeNull();
        result.RenewedBackgroundServiceLeases.ShouldNotContain(
            "ExpiredOwnSvc",
            "an already-expired lease must not appear in the renewed list — the holder lost it");
    }

    [TimedFact]
    public async Task HeartbeatAsync_NoLeases_RenewedListEmpty()
    {
        await SeedServerAsync(MyServerId);

        var queries = TestTasks.QueriesFor(_fixture.CreateContext());
        await using var ctx = _fixture.CreateContext();
        var result = await queries.HeartbeatAsync(ctx, MyServerId, DateTime.UtcNow, null, null, writeLocalTime: false, Ct);

        result.ShouldNotBeNull();
        result.RenewedBackgroundServiceLeases.ShouldBeEmpty();
    }
}
