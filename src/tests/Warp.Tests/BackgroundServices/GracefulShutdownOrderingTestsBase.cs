using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core.BackgroundServices;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.BackgroundServices;

namespace Warp.Tests.BackgroundServices;

[GenerateDatabaseTests]
public abstract class GracefulShutdownOrderingTestsBase : IntegrationTestBase
{
    protected GracefulShutdownOrderingTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// Pin user code with a barrier. Cancel the host. Assert that the instance row is deleted
    /// before (or while) ExecuteAsync is still blocked, then release the barrier so the host
    /// can finish shutting down cleanly within the timeout.
    /// </summary>
    [TimedFact(15_000)]
    public async Task Shutdown_LeaseDeletedBeforeWaitingOnExecuteAsync()
    {
        var barrier = new BackgroundServiceBarrierSignal();

        var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg => cfg.AddBackgroundService<BarrierPinnedService>(),
            configureServices: services => services.AddSingleton(barrier));

        // Wait for service to enter ExecuteAsync.
        var reached = await barrier.Running.WaitAsync(
            TimeSpan.FromSeconds(8),
            Xunit.TestContext.Current.CancellationToken);
        reached.ShouldBeTrue("BarrierPinnedService must reach ExecuteAsync");

        // Initiate host shutdown — don't await yet. The fire-and-forget DELETE in
        // BackgroundServiceHost.StopAsync should run before user code gets a chance to return.
        var stopTask = server.DisposeAsync().AsTask();

        // Wait until the instance row is deleted — this must happen before the barrier is
        // released, confirming that the "DELETE before waiting on user code" ordering holds.
        // WaitForBackgroundServiceDeleted polls at 50ms cadence without a fixed Task.Delay budget.
        var rowDeleted = false;
        try
        {
            await server.WaitForBackgroundServiceDeleted(
                nameof(BarrierPinnedService),
                timeout: TimeSpan.FromSeconds(8));
            rowDeleted = true;
        }
        catch (TimeoutException)
        {
            // rowDeleted stays false — assertion below will fail with a clear message.
        }

        // Release the barrier so the host can shut down cleanly.
        barrier.CanFinish.Release();
        await stopTask;

        rowDeleted.ShouldBeTrue(
            "BackgroundServiceInstance row should be deleted by the fire-and-forget cleanup " +
            "in BackgroundServiceHost.StopAsync before the host waits on ExecuteAsync");
    }
}
