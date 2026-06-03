using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core.BackgroundServices;
using Warp.Core.Data.Entities;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.BackgroundServices;

namespace Warp.Tests.BackgroundServices;

[GenerateDatabaseTests]
public abstract class RestartBackoffTestsBase : IntegrationTestBase
{
    protected RestartBackoffTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact(20_000)]
    public async Task ThrowingService_FirstCallFailsSecondSucceeds_StatusWalksFaultedRestartingRunning()
    {
        var state = new ThrowingServiceState();

        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg => cfg.AddBackgroundService<ThrowingService>(),
            configureServices: services => services.AddSingleton(state));

        // Wait for the second attempt to reach user code — proves the fault-then-restart walk.
        // We don't poll for the transient Faulted status because the 1s backoff window is too
        // narrow to catch deterministically at 50ms poll intervals.
        var recovered = await state.Recovered.WaitAsync(
            TimeSpan.FromSeconds(15),
            Xunit.TestContext.Current.CancellationToken);
        recovered.ShouldBeTrue("ThrowingService should recover and reach user code on attempt 2");

        // After recovery the service is Running (pinned on barrier).
        await server.WaitForBackgroundServiceState(
            nameof(ThrowingService),
            BackgroundServiceStatus.Running,
            TimeSpan.FromSeconds(5));

        // RestartCount > 0 and LastError set prove the fault walk happened.
        var ctx = Fixture.CreateContext();
        var instance = await ctx.Set<BackgroundServiceInstance>()
            .Where(x => x.ServerId == server.ServerId)
            .Where(x => x.ServiceName == nameof(ThrowingService))
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);

        instance.ShouldNotBeNull();
        instance.RestartCount.ShouldBeGreaterThan(0, "at least one fault must have been recorded");
        instance.LastError.ShouldNotBeNull();

        state.CanFinish.Release();
    }

    [TimedFact(20_000)]
    public async Task ThrowingService_RestartCountIncrementsOnFault()
    {
        var state = new ThrowingServiceState();

        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg => cfg.AddBackgroundService<ThrowingService>(),
            configureServices: services => services.AddSingleton(state));

        // Wait until the second attempt starts (post-fault restart).
        var recovered = await state.Recovered.WaitAsync(
            TimeSpan.FromSeconds(15),
            Xunit.TestContext.Current.CancellationToken);
        recovered.ShouldBeTrue("ThrowingService should recover within 15s");

        // RestartCount should be 1 after the first fault.
        var ctx = Fixture.CreateContext();
        var instance = await ctx.Set<BackgroundServiceInstance>()
            .Where(x => x.ServerId == server.ServerId)
            .Where(x => x.ServiceName == nameof(ThrowingService))
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);

        instance.ShouldNotBeNull();
        instance.RestartCount.ShouldBeGreaterThanOrEqualTo(1);
        instance.LastError.ShouldNotBeNull();
        instance.LastError.ShouldContain("InvalidOperationException");

        state.CanFinish.Release();
    }
}
