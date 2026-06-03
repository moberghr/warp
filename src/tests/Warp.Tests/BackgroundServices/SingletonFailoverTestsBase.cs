using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core.BackgroundServices;
using Warp.Core.Data.Entities;
using Warp.Tests.Fixtures;
using Warp.Worker;

namespace Warp.Tests.BackgroundServices;

/// <summary>
/// End-to-end failover scenarios for <see cref="ServiceScope.Singleton"/> services:
/// when the current holder dies (TTL expiry path) or shuts down gracefully (immediate
/// lease deletion), a waiting server must take over the singleton.
/// </summary>
[GenerateDatabaseTests]
public abstract class SingletonFailoverTestsBase : IntegrationTestBase
{
    protected SingletonFailoverTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// Two servers compete for a singleton lease. The holder's lease is forcibly expired
    /// via a direct DB write; the waiting server must then acquire the freed lease on its
    /// next poll cycle.
    /// </summary>
    [TimedFact(30_000)]
    public async Task HolderLeaseForciblyExpired_WaiterTakesOver()
    {
        // CountingBarrierSignal: each acquisition releases Entry once; each holder blocks on CanFinish.
        var barrier = new CountingBarrierSignal();

        void Configure(WarpWorkerBuilder<TestContext> cfg)
        {
            cfg.AddBackgroundService<CountingBarrierService>();
            cfg.BackgroundServiceAcquirePollInterval = TimeSpan.FromMilliseconds(200);
            cfg.BackgroundServiceLeaseTtl = TimeSpan.FromSeconds(30);
        }

        void WithBarrier(IServiceCollection services) => services.AddSingleton(barrier);

        await using var server1 = await WarpTestServer.StartAsync(Fixture, Configure, WithBarrier);
        await using var server2 = await WarpTestServer.StartAsync(Fixture, Configure, WithBarrier);

        // Wait for one server to acquire the singleton.
        var firstAcquired = await barrier.Entry.WaitAsync(
            TimeSpan.FromSeconds(10),
            Xunit.TestContext.Current.CancellationToken);
        firstAcquired.ShouldBeTrue("One server must acquire the singleton lease");

        // Force-expire the lease so the other server can take over.
        await using var ctx = Fixture.CreateContext();
        var lease = await ctx.Set<BackgroundServiceLease>()
            .Where(x => x.ServiceName == nameof(CountingBarrierService))
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);
        lease.ShouldNotBeNull();
        lease.LeaseExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Release the current holder so it exits user code (CTS also cancelled when lease expires).
        barrier.CanFinish.Release();

        // The other server must acquire and enter user code.
        var secondAcquired = await barrier.Entry.WaitAsync(
            TimeSpan.FromSeconds(15),
            Xunit.TestContext.Current.CancellationToken);
        secondAcquired.ShouldBeTrue("The second server must take over after the lease expires");

        // Release the second holder so the hosts can shut down cleanly.
        barrier.CanFinish.Release();
    }

    /// <summary>
    /// Holder shuts down gracefully — <c>WarpServerRegistration.StopAsync</c> deletes the lease
    /// immediately. The waiting server must acquire the lease without having to wait for the TTL.
    /// </summary>
    [TimedFact(30_000)]
    public async Task HolderGracefulShutdown_LeaseDeletedImmediately_WaiterTakesOverWithoutTtlWait()
    {
        var barrier = new CountingBarrierSignal();

        void Configure(WarpWorkerBuilder<TestContext> cfg)
        {
            cfg.AddBackgroundService<CountingBarrierService>();
            cfg.BackgroundServiceAcquirePollInterval = TimeSpan.FromMilliseconds(200);

            // Long TTL — the only way the waiter can acquire within the test budget is
            // if StopAsync deleted the lease immediately (not via TTL expiry).
            cfg.BackgroundServiceLeaseTtl = TimeSpan.FromSeconds(30);
        }

        void WithBarrier(IServiceCollection services) => services.AddSingleton(barrier);

        // Start both servers without `await using` so we can control disposal order explicitly.
        var server1 = await WarpTestServer.StartAsync(Fixture, Configure, WithBarrier);
        var server2 = await WarpTestServer.StartAsync(Fixture, Configure, WithBarrier);

        try
        {
            // Wait for the first server to acquire.
            var entered = await barrier.Entry.WaitAsync(
                TimeSpan.FromSeconds(10),
                Xunit.TestContext.Current.CancellationToken);
            entered.ShouldBeTrue("One server must acquire the singleton");

            // Identify which server is the holder.
            var holderLease = await Fixture.CreateContext().Set<BackgroundServiceLease>()
                .Where(x => x.ServiceName == nameof(CountingBarrierService))
                .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);
            holderLease.ShouldNotBeNull();

            var holderServer = server1.ServerId == holderLease.HolderServerId ? server1 : server2;

            // Stop the holder WITHOUT releasing the barrier first. The host's stoppingToken
            // cancels the service's linked CTS, so CountingBarrierService.ExecuteAsync throws
            // OperationCanceledException from WaitAsync(ct) — correct cancellation semantics.
            // The SingletonRelease.DisposeAsync calls coordinator.ReleaseAsync (deletes the lease)
            // during graceful shutdown, which lets the waiting server acquire immediately —
            // without having to wait for the lease TTL to expire.
            await holderServer.DisposeAsync();
            if (ReferenceEquals(holderServer, server1))
            {
                server1 = null!;
            }
            else
            {
                server2 = null!;
            }

            // The remaining server must now acquire the lease within a short window — far shorter
            // than the 30s TTL. This proves the lease was released immediately on graceful
            // shutdown rather than being held until TTL expiry.
            var tookOver = await barrier.Entry.WaitAsync(
                TimeSpan.FromSeconds(5),
                Xunit.TestContext.Current.CancellationToken);
            tookOver.ShouldBeTrue(
                "The remaining server must take over within 5s of graceful shutdown " +
                "(shorter than the 30s TTL — proving immediate release, not TTL-expiry)");

            // Release so the waiter can shut down cleanly.
            barrier.CanFinish.Release();
        }
        finally
        {
            // Release a spare permit so any holder blocked on CanFinish can exit.
            barrier.CanFinish.Release();
            if (server1 != null)
            {
                await server1.DisposeAsync();
            }

            if (server2 != null)
            {
                await server2.DisposeAsync();
            }
        }
    }
}

/// <summary>
/// Singleton service that signals each acquisition entry and then blocks on a shared barrier.
/// Multiple acquisitions (after failover) release the semaphore multiple times, so the test
/// can <c>WaitAsync</c> twice to observe two sequential holders.
/// </summary>
public sealed class CountingBarrierService : WarpBackgroundService
{
    private readonly CountingBarrierSignal _barrier;

    public CountingBarrierService(CountingBarrierSignal barrier)
    {
        _barrier = barrier;
    }

    public override ServiceScope Scope => ServiceScope.Singleton;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _barrier.Entry.Release();
        await _barrier.CanFinish.WaitAsync(ct);
    }
}

public sealed class CountingBarrierSignal
{
    /// <summary>Released once per acquisition (one per holder entering ExecuteAsync).</summary>
    public SemaphoreSlim Entry { get; } = new(0);

    /// <summary>Test releases one permit to unblock each holder.</summary>
    public SemaphoreSlim CanFinish { get; } = new(0);
}
