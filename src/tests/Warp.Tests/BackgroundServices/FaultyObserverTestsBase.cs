using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core.BackgroundServices;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.BackgroundServices;

namespace Warp.Tests.BackgroundServices;

/// <summary>
/// Verifies that a user-supplied <see cref="IBackgroundServiceStatusObserver"/> that throws
/// on <c>OnStatusChanged</c> does NOT propagate through the supervisor (FIX 2).
/// The supervisor must log a warning and continue normally after an observer exception.
/// </summary>
[GenerateDatabaseTests]
public abstract class FaultyObserverTestsBase : IntegrationTestBase
{
    protected FaultyObserverTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact(15_000)]
    public async Task FaultyObserver_ThrowsOnStatusChanged_SupervisorContinuesNormally()
    {
        var barrier = new BackgroundServiceBarrierSignal();

        // Register a throwing observer. Because TryAddSingleton is used for the null default,
        // this AddSingleton (registered after AddWarpWorker) takes precedence via last-wins DI.
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg => cfg.AddBackgroundService<BarrierPinnedService>(),
            configureServices: services =>
            {
                services.AddSingleton(barrier);
                services.AddSingleton<IBackgroundServiceStatusObserver, ThrowingStatusObserver>();
            });

        // Wait for the service to enter ExecuteAsync — proves the supervisor survived the
        // observer throw on the Running status transition.
        await barrier.Running.WaitAsync(Xunit.TestContext.Current.CancellationToken);

        barrier.CanFinish.Release();
    }
}

/// <summary>
/// Observer that always throws. Used to verify that observer exceptions are isolated by the
/// supervisor and do not propagate to the host.
/// </summary>
file sealed class ThrowingStatusObserver : IBackgroundServiceStatusObserver
{
    public void OnStatusChanged(string serviceName, BackgroundServiceStatus status)
    {
        throw new InvalidOperationException($"Simulated observer failure for {serviceName} → {status}");
    }
}
