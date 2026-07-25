using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Notifiers;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Worker.Services;

namespace Warp.Tests.Notifiers;

/// <summary>
/// Proves the server-task operational-event dispatch is genuinely POST-COMMIT on the production path.
/// <c>ExpirationCleanup</c> runs inside the <see cref="ServerTaskHost{TContext}"/> lock transaction, so it
/// buffers <c>InstanceDown</c> into <see cref="PendingOperationalEvents"/> and the <c>ServerTaskLoop</c>
/// dispatches only after that transaction commits — driven here through the real loop wrapper (not a direct
/// <c>ExecuteAsync</c> call), so a regression that dispatched pre-commit would be caught.
/// </summary>
[GenerateDatabaseTests(SerializeInCollection = "HeavyIntegration")]
public abstract class NotifierPostCommitIntegrationTestsBase : IntegrationTestBase
{
    protected NotifierPostCommitIntegrationTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact(15_000)]
    public async Task StaleInstanceSweep_DispatchesInstanceDown_ThroughLoop_PostCommit()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var instanceId = Guid.NewGuid();
        var staleAt = DateTime.UtcNow.AddMinutes(-10);

        var seedCtx = Fixture.CreateContext();
        seedCtx.Set<ApplicationInstance>().Add(new ApplicationInstance
        {
            Id = instanceId,
            ApplicationName = "publisher-app",
            MachineName = "test-host",
            StartedAt = staleAt,
            LastHeartbeatAt = staleAt,
        });
        await seedCtx.SaveChangesAsync(ct);

        var spy = new SpyNotifier();
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg => cfg.DisableWorker(),
            configureServices: services => services.AddSingleton<IWarpNotifier>(spy));

        await server.RunServerTaskThroughLoopAsync<ExpirationCleanup<TestContext>>(ct);

        // The row was reaped and the InstanceDown event dispatched to the registered notifier — post-commit,
        // via the loop drain (not a direct ExecuteAsync).
        var received = spy.Received.OfType<InstanceDownEvent>().SingleOrDefault(x => x.InstanceId == instanceId);
        received.ShouldNotBeNull();
        received.IsServer.ShouldBeFalse();
        received.ApplicationName.ShouldBe("publisher-app");

        (await Fixture.CreateContext().Set<ApplicationInstance>().FindAsync([instanceId], ct)).ShouldBeNull();
    }
}
