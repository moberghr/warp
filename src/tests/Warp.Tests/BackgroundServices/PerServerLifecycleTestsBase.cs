using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core.BackgroundServices;
using Warp.Core.Data.Entities;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.BackgroundServices;

namespace Warp.Tests.BackgroundServices;

[GenerateDatabaseTests]
public abstract class PerServerLifecycleTestsBase : IntegrationTestBase
{
    protected PerServerLifecycleTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact(15_000)]
    public async Task Start_PerServerService_InsertsInstanceWithStatusRunning()
    {
        var barrier = new BackgroundServiceBarrierSignal();

        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg => cfg.AddBackgroundService<BarrierPinnedService>(),
            configureServices: services => services.AddSingleton(barrier));

        // Wait for the service to enter ExecuteAsync (it will release Running).
        await barrier.Running.WaitAsync(Xunit.TestContext.Current.CancellationToken);

        var ctx = Fixture.CreateContext();
        var instance = await ctx.Set<BackgroundServiceInstance>()
            .Where(x => x.ServerId == server.ServerId)
            .Where(x => x.ServiceName == nameof(BarrierPinnedService))
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);

        instance.ShouldNotBeNull();
        instance.Status.ShouldBe(BackgroundServiceStatus.Running);

        barrier.CanFinish.Release();
    }

    [TimedFact(15_000)]
    public async Task Start_PerServerService_ReachesUserCodeBarrier()
    {
        var barrier = new BackgroundServiceBarrierSignal();

        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg => cfg.AddBackgroundService<BarrierPinnedService>(),
            configureServices: services => services.AddSingleton(barrier));

        // The service signals Running on entry — if we get here, user code was reached.
        var reached = await barrier.Running.WaitAsync(
            TimeSpan.FromSeconds(8),
            Xunit.TestContext.Current.CancellationToken);

        reached.ShouldBeTrue("BarrierPinnedService should reach ExecuteAsync within 8s");

        barrier.CanFinish.Release();
    }

    [TimedFact(15_000)]
    public async Task GracefulShutdown_DeletesInstanceRow()
    {
        var barrier = new BackgroundServiceBarrierSignal();

        var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg => cfg.AddBackgroundService<BarrierPinnedService>(),
            configureServices: services => services.AddSingleton(barrier));

        var serverId = server.ServerId;

        // Wait until the service is inside ExecuteAsync.
        await barrier.Running.WaitAsync(Xunit.TestContext.Current.CancellationToken);

        // Release the barrier so ExecuteAsync can return on cancellation.
        barrier.CanFinish.Release();

        // Stop the server — supervisor should delete the instance row on graceful exit.
        await server.DisposeAsync();

        var ctx = Fixture.CreateContext();
        var instance = await ctx.Set<BackgroundServiceInstance>()
            .Where(x => x.ServerId == serverId)
            .Where(x => x.ServiceName == nameof(BarrierPinnedService))
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);

        instance.ShouldBeNull("instance row should be deleted on graceful shutdown");
    }
}
