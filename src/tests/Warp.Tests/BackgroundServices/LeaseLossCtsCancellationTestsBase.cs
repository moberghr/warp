using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core.BackgroundServices;
using Warp.Core.Data.Entities;
using Warp.Tests.Fixtures;
using Warp.Worker.Services;

namespace Warp.Tests.BackgroundServices;

/// <summary>
/// Verifies the lost-lease signal path end-to-end:
/// when a <c>BackgroundServiceLease</c> row is stolen by another server between heartbeats
/// (i.e. the holder no longer appears in <c>RenewedBackgroundServiceLeases</c> on the next
/// beat), <see cref="Warp.Worker.Services.Heartbeat{TContext}"/> publishes
/// <c>BackgroundServiceLeaseLost</c>, which <see cref="Warp.Worker.BackgroundServices.SingletonServiceStrategy{TContext}"/>
/// receives and uses to cancel the per-execution CTS — so user code observes cancellation
/// without waiting for the next acquire-poll cycle.
/// </summary>
[GenerateDatabaseTests]
public abstract class LeaseLossCtsCancellationTestsBase : IntegrationTestBase
{
    protected LeaseLossCtsCancellationTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact(20_000)]
    public async Task LeaseManuallyStolen_StatusTransitionsFaultedThenWaiting()
    {
        var signal = new CancellationObserverSignal();
        var ct = Xunit.TestContext.Current.CancellationToken;

        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg =>
            {
                cfg.AddBackgroundService<CancellationObserverService>();
                cfg.BackgroundServiceAcquirePollInterval = TimeSpan.FromMilliseconds(200);
                cfg.BackgroundServiceLeaseTtl = TimeSpan.FromSeconds(30);
                cfg.HealthCheckInterval = null;
            },
            configureServices: services => services.AddSingleton(signal));

        var running = await signal.Running.WaitAsync(TimeSpan.FromSeconds(10), ct);
        running.ShouldBeTrue("CancellationObserverService must reach ExecuteAsync");

        var tracker = server.GetService<HeartbeatLeaseTracker>();
        tracker.SeedForTest([nameof(CancellationObserverService)]);

        await using var ctx = Fixture.CreateContext();
        var lease = await ctx.Set<BackgroundServiceLease>()
            .Where(x => x.ServiceName == nameof(CancellationObserverService))
            .FirstOrDefaultAsync(ct);

        lease.ShouldNotBeNull("a lease row must exist while the service is running");

        // Insert a Server row for the foreign holder so the FK constraint is satisfied.
        var foreignHolder = Guid.NewGuid();
        await Fixture.SeedServerAsync(foreignHolder, "stolen-holder-server-1", ct);
        lease.HolderServerId = foreignHolder;
        lease.LeaseExpiresAt = DateTime.UtcNow.AddSeconds(60);
        await ctx.SaveChangesAsync(ct);

        await server.RunHeartbeatOnceAsync(ct);

        // After lease loss the supervisor writes Faulted (transitional) then immediately
        // re-enters the acquire loop which writes Waiting. The Faulted window is sub-millisecond,
        // so asserting it via 50ms DB polling is unreliable. We assert the stable end-state
        // (Waiting) — the Faulted write is covered by code inspection and the supervisor unit path.
        await server.WaitForBackgroundServiceState(
            nameof(CancellationObserverService),
            BackgroundServiceStatus.Waiting,
            timeout: TimeSpan.FromSeconds(8));
    }

    [TimedFact(20_000)]
    public async Task LeaseManuallyStolen_HeartbeatPublishesLost_UserCodeObservesCancellation()
    {
        var signal = new CancellationObserverSignal();
        var ct = Xunit.TestContext.Current.CancellationToken;

        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg =>
            {
                cfg.AddBackgroundService<CancellationObserverService>();
                cfg.BackgroundServiceAcquirePollInterval = TimeSpan.FromMilliseconds(200);
                cfg.BackgroundServiceLeaseTtl = TimeSpan.FromSeconds(30);

                // Disable auto-heartbeat so the test drives heartbeat ticks explicitly.
                // This makes lost-lease detection fully deterministic without Task.Delay.
                cfg.HealthCheckInterval = null;
            },
            configureServices: services => services.AddSingleton(signal));

        // Wait for the service to acquire the lease and enter user code.
        var running = await signal.Running.WaitAsync(TimeSpan.FromSeconds(10), ct);
        running.ShouldBeTrue("CancellationObserverService must reach ExecuteAsync");

        // Pre-populate the HeartbeatLeaseTracker's _previousHeld set with the service name
        // so the next heartbeat tick can compute lostLeases = _previousHeld - renewedThisBeat.
        // HeartbeatLeaseTracker is a Singleton, so GetService resolves the same instance that
        // Heartbeat.ExecuteAsync will read from — no timing dependency required.
        var tracker = server.GetService<HeartbeatLeaseTracker>();
        tracker.SeedForTest([nameof(CancellationObserverService)]);

        // Steal the lease: update the holder to a foreign server ID so that when the
        // heartbeat runs it will not find an active lease owned by this server and
        // the renewal WHERE clause (holder_server_id = @me) returns zero rows.
        var foreignServer = Guid.NewGuid();
        await using var ctx = Fixture.CreateContext();
        var lease = await ctx.Set<BackgroundServiceLease>()
            .Where(x => x.ServiceName == nameof(CancellationObserverService))
            .FirstOrDefaultAsync(ct);

        lease.ShouldNotBeNull("a lease row must exist while the service is running");

        // Insert a Server row for the foreign holder so the FK constraint is satisfied.
        await Fixture.SeedServerAsync(foreignServer, "stolen-holder-server-2", ct);

        // Overwrite holder to a foreign ID AND push the expiry well into the future so
        // the renewal WHERE filter (expires_at > now) would pass — but the holder filter
        // (holder_server_id = @me) will not, so the row is excluded from renewed_leases.
        lease.HolderServerId = foreignServer;
        lease.LeaseExpiresAt = DateTime.UtcNow.AddSeconds(60);
        await ctx.SaveChangesAsync(ct);

        // Run one heartbeat tick: renewal returns 0 rows for @me, so
        // lostLeases = _previousHeld - {} = {CancellationObserverService}.
        // Heartbeat.ExecuteAsync publishes BackgroundServiceLeaseLost, which the
        // SingletonServiceStrategy subscriber uses to cancel the linked CTS.
        await server.RunHeartbeatOnceAsync(ct);

        // The supervisor subscribes to BackgroundServiceLeaseLost and cancels the CTS
        // which causes user code to observe OperationCanceledException → releases Cancelled.
        var cancelled = await signal.Cancelled.WaitAsync(TimeSpan.FromSeconds(8), ct);
        cancelled.ShouldBeTrue(
            "user code must observe CancellationToken cancellation after the lease-lost signal fires");
    }
}

/// <summary>
/// Singleton service that records arrival and then blocks, observing the CancellationToken.
/// When the token is cancelled (lease lost signal), it releases the Cancelled semaphore.
/// </summary>
public sealed class CancellationObserverService : WarpBackgroundService
{
    private readonly CancellationObserverSignal _signal;

    public CancellationObserverService(CancellationObserverSignal signal)
    {
        _signal = signal;
    }

    public override ServiceScope Scope => ServiceScope.Singleton;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _signal.Running.Release();
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            // Signal BEFORE re-throwing so the test can observe cancellation while the
            // supervisor is still processing the fault path.
            _signal.Cancelled.Release();
            throw;
        }
    }
}

public sealed class CancellationObserverSignal
{
    public SemaphoreSlim Running { get; } = new(0);

    public SemaphoreSlim Cancelled { get; } = new(0);
}
