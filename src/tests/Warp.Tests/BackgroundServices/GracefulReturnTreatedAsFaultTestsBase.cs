using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Warp.Core.BackgroundServices;
using Warp.Core.Data.Entities;
using Warp.Tests.Fixtures;

namespace Warp.Tests.BackgroundServices;

[GenerateDatabaseTests]
public abstract class GracefulReturnTreatedAsFaultTestsBase : IntegrationTestBase
{
    protected GracefulReturnTreatedAsFaultTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact(15_000)]
    public async Task ServiceReturnsWithoutCancellation_StatusGoesFaulted_LifecycleLogRecordsGracefulExit()
    {
        var state = new ImmediateReturnServiceState();

        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg => cfg.AddBackgroundService<ImmediateReturnService>(),
            configureServices: services => services.AddSingleton(state));

        // Wait until the fault path was taken: RestartCount > 0 means the supervisor incremented
        // it, which only happens via RecordFaultAsync — i.e. the graceful-return was treated as fault.
        // Using WaitUntil avoids Task.Delay polling loops (§4.5).
        await WarpTestServer.WaitUntil(
            async () =>
            {
                var ctx = Fixture.CreateContext();
                var restartCount = await ctx.Set<BackgroundServiceInstance>()
                    .Where(x => x.ServerId == server.ServerId)
                    .Where(x => x.ServiceName == nameof(ImmediateReturnService))
                    .Select(x => (int?)x.RestartCount)
                    .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);

                return restartCount > 0;
            },
            timeout: TimeSpan.FromSeconds(10),
            ct: Xunit.TestContext.Current.CancellationToken);

        // Signal the service to block on next attempt so the test can shut down cleanly.
        state.StopOnNextAttempt = true;

        var ctx = Fixture.CreateContext();
        var instance = await ctx.Set<BackgroundServiceInstance>()
            .Where(x => x.ServerId == server.ServerId)
            .Where(x => x.ServiceName == nameof(ImmediateReturnService))
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);

        instance.ShouldNotBeNull();
        instance.LastError.ShouldNotBeNull("LastError must be set after graceful-return fault");

        // Assert that a Lifecycle/Error row was emitted by LogFaulted (§FIX 6: Q2 lifecycle log assertion).
        // The supervisor calls LogFaulted when ExecuteAsync returns without cancellation.
        // Wait for the log to flush from collector to DB before asserting.
        await WarpTestServer.WaitUntil(
            async () =>
            {
                var logCtx = Fixture.CreateContext();
                return await logCtx.Set<BackgroundServiceLog>()
                    .Where(x => x.ServiceName == nameof(ImmediateReturnService))
                    .Where(x => x.Source == BackgroundServiceLogSource.Lifecycle)
                    .Where(x => x.Level == LogLevel.Error)
                    .AnyAsync(Xunit.TestContext.Current.CancellationToken);
            },
            timeout: TimeSpan.FromSeconds(5),
            ct: Xunit.TestContext.Current.CancellationToken);

        var logCtx2 = Fixture.CreateContext();
        var faultedLogCount = await logCtx2.Set<BackgroundServiceLog>()
            .Where(x => x.ServiceName == nameof(ImmediateReturnService))
            .Where(x => x.Source == BackgroundServiceLogSource.Lifecycle)
            .Where(x => x.Level == LogLevel.Error)
            .CountAsync(Xunit.TestContext.Current.CancellationToken);

        faultedLogCount.ShouldBeGreaterThan(
            0,
            "At least one Lifecycle/Error row must be written by LogFaulted after graceful-return-as-fault");
    }
}

/// <summary>
/// Service that immediately returns from <c>ExecuteAsync</c> without waiting for cancellation.
/// Tests the "graceful return = fault" invariant.
/// </summary>
public sealed class ImmediateReturnService : WarpBackgroundService
{
    private readonly ImmediateReturnServiceState _state;

    public ImmediateReturnService(ImmediateReturnServiceState state)
    {
        _state = state;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_state.StopOnNextAttempt)
        {
            await ct.WhenCancelledAsync();
            return;
        }

        // Return immediately — supervisor should treat this as a fault.
        await Task.CompletedTask;
    }
}

public sealed class ImmediateReturnServiceState
{
    public volatile bool StopOnNextAttempt;
}

file static class CancellationTokenHelper
{
    internal static Task WhenCancelledAsync(this CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(static s => ((TaskCompletionSource)s!).TrySetResult(), tcs);

        return tcs.Task;
    }
}
