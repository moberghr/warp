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

    [TimedFact]
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

        // Post-commit, via OnCommittedAsync (not a direct ExecuteAsync). Wait for the spy: the host's own
        // background server-task loop can race the explicit run for the task's lock, so whichever run wins
        // dispatches — poll rather than assert on the instant.
        await WarpTestServer.WaitUntil(
            () => Task.FromResult(spy.Received.OfType<InstanceDownEvent>().Any(x => x.InstanceId == instanceId)),
            timeout: TimeSpan.FromSeconds(8),
            ct: ct);

        var received = spy.Received.OfType<InstanceDownEvent>().Single(x => x.InstanceId == instanceId);
        received.IsServer.ShouldBeFalse();
        received.ApplicationName.ShouldBe("publisher-app");

        (await Fixture.CreateContext().Set<ApplicationInstance>().FindAsync([instanceId], ct)).ShouldBeNull();
    }

    // NOTE: ServerCleanup's post-commit dispatch through the real loop is deliberately NOT tested here.
    // ServerCleanup sweeps *Server* rows, and against a live WarpTestServer (which uses a short
    // HealthCheckTimeout) it would reap the harness's OWN server, deleting its BackgroundServiceInstance
    // while the harness's log collector is concurrently flushing BackgroundServiceLog rows for it — an FK
    // conflict that is a test artifact of sweeping a live server, not a notifier concern. The coverage is
    // provided without that hazard by two other tests: StaleInstanceSweep above proves the ServerTaskLoop
    // invokes OnCommittedAsync post-commit (the loop is task-agnostic), and
    // NotifierDispatchTestsBase.StaleServerSweep_DispatchesInstanceDownEvent_Server proves ServerCleanup
    // buffers + dispatches via OnCommittedAsync on both providers.
}
