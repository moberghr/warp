using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core.BackgroundServices;
using Warp.Core.Data.Entities;
using Warp.Tests.Fixtures;
using Warp.Worker;

namespace Warp.Tests.BackgroundServices;

[GenerateDatabaseTests]
public abstract class SingletonAcquisitionTestsBase : IntegrationTestBase
{
    protected SingletonAcquisitionTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// Two servers both register the same singleton-scope service. Only one should reach the
    /// barrier (enter ExecuteAsync); the other must remain in Waiting status.
    /// </summary>
    [TimedFact(20_000)]
    public async Task TwoServers_OneSingletonService_OnlyOneReachesBarrier()
    {
        // Both servers share the same barrier signal so we can detect if more than one instance
        // enters ExecuteAsync.
        var barrier = new SingletonBarrierSignal();

        void Configure(WarpWorkerBuilder<TestContext> cfg)
        {
            cfg.AddBackgroundService<SingletonBarrierService>();

            // Use a short acquire-poll interval so the waiting server quickly checks the lease.
            cfg.BackgroundServiceAcquirePollInterval = TimeSpan.FromMilliseconds(200);
            cfg.BackgroundServiceLeaseTtl = TimeSpan.FromSeconds(30);
        }

        void WithBarrier(IServiceCollection services) => services.AddSingleton(barrier);

        await using var server1 = await WarpTestServer.StartAsync(Fixture, Configure, WithBarrier);
        await using var server2 = await WarpTestServer.StartAsync(Fixture, Configure, WithBarrier);

        // Exactly one server should reach the Running signal.
        var firstEntry = await barrier.Running.WaitAsync(
            TimeSpan.FromSeconds(8),
            Xunit.TestContext.Current.CancellationToken);
        firstEntry.ShouldBeTrue("One server must acquire the singleton lease and enter ExecuteAsync");

        // A second concurrent entry should NOT happen — wait briefly to surface a race.
        var spurious = await barrier.Running.WaitAsync(
            TimeSpan.FromMilliseconds(400),
            Xunit.TestContext.Current.CancellationToken);
        spurious.ShouldBeFalse("Only one server should hold the singleton lease at a time");

        barrier.CanFinish.Release();
    }

    [TimedFact(20_000)]
    public async Task TwoServers_HolderHasStatusRunningAndLeaseRow()
    {
        var barrier = new SingletonBarrierSignal();

        void Configure(WarpWorkerBuilder<TestContext> cfg)
        {
            cfg.AddBackgroundService<SingletonBarrierService>();
            cfg.BackgroundServiceAcquirePollInterval = TimeSpan.FromMilliseconds(200);
            cfg.BackgroundServiceLeaseTtl = TimeSpan.FromSeconds(30);
        }

        void WithBarrier(IServiceCollection services) => services.AddSingleton(barrier);

        await using var server1 = await WarpTestServer.StartAsync(Fixture, Configure, WithBarrier);
        await using var server2 = await WarpTestServer.StartAsync(Fixture, Configure, WithBarrier);

        // Wait for the holder to enter ExecuteAsync.
        await barrier.Running.WaitAsync(Xunit.TestContext.Current.CancellationToken);

        var ctx = Fixture.CreateContext();

        // Verify lease row exists.
        var lease = await ctx.Set<BackgroundServiceLease>()
            .Where(x => x.ServiceName == nameof(SingletonBarrierService))
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);

        lease.ShouldNotBeNull("a BackgroundServiceLease row must exist for the singleton holder");

        // The holder's instance row should be Running.
        var holderInstance = await ctx.Set<BackgroundServiceInstance>()
            .Where(x => x.ServerId == lease.HolderServerId)
            .Where(x => x.ServiceName == nameof(SingletonBarrierService))
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);

        holderInstance.ShouldNotBeNull();
        holderInstance.Status.ShouldBe(BackgroundServiceStatus.Running);

        barrier.CanFinish.Release();
    }

    [TimedFact(20_000)]
    public async Task TwoServers_WaiterHasStatusWaiting()
    {
        var barrier = new SingletonBarrierSignal();

        void Configure(WarpWorkerBuilder<TestContext> cfg)
        {
            cfg.AddBackgroundService<SingletonBarrierService>();
            cfg.BackgroundServiceAcquirePollInterval = TimeSpan.FromMilliseconds(200);
            cfg.BackgroundServiceLeaseTtl = TimeSpan.FromSeconds(30);
        }

        void WithBarrier(IServiceCollection services) => services.AddSingleton(barrier);

        await using var server1 = await WarpTestServer.StartAsync(Fixture, Configure, WithBarrier);
        await using var server2 = await WarpTestServer.StartAsync(Fixture, Configure, WithBarrier);

        // Wait for the holder.
        await barrier.Running.WaitAsync(Xunit.TestContext.Current.CancellationToken);

        var ctx = Fixture.CreateContext();
        var lease = await ctx.Set<BackgroundServiceLease>()
            .Where(x => x.ServiceName == nameof(SingletonBarrierService))
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);
        lease.ShouldNotBeNull();

        // The server that is NOT the holder should be in Waiting status.
        var waiterServerId = server1.ServerId == lease.HolderServerId
            ? server2.ServerId
            : server1.ServerId;

        // Waiting status may take a poll cycle to appear — use the helper.
        var waiterServer = server1.ServerId == waiterServerId ? server1 : server2;
        await waiterServer.WaitForBackgroundServiceState(
            nameof(SingletonBarrierService),
            BackgroundServiceStatus.Waiting,
            TimeSpan.FromSeconds(5));

        barrier.CanFinish.Release();
    }
}

/// <summary>
/// Singleton-scope service that signals arrival and blocks. Used for cross-server lease tests.
/// </summary>
public sealed class SingletonBarrierService : WarpBackgroundService
{
    private readonly SingletonBarrierSignal _signal;

    public SingletonBarrierService(SingletonBarrierSignal signal)
    {
        _signal = signal;
    }

    public override ServiceScope Scope => ServiceScope.Singleton;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _signal.Running.Release();
        await _signal.CanFinish.WaitAsync(ct);
    }
}

public sealed class SingletonBarrierSignal
{
    public SemaphoreSlim Running { get; } = new(0);

    public SemaphoreSlim CanFinish { get; } = new(0);
}
