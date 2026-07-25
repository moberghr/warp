using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Notifiers;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Worker;
using Warp.Worker.Services;

namespace Warp.Tests.Notifiers;

/// <summary>
/// Verifies the three v1 dispatch sites hand a redaction-safe <see cref="WarpOperationalEvent"/> to the
/// notifier set POST-COMMIT: saga force-complete, the non-server instance stale-sweep, and the stale-server
/// sweep. (Webhook exhaustion is covered end-to-end in <c>WebhookExecutionTestsBase</c>.)
/// </summary>
[GenerateDatabaseTests]
public abstract class NotifierDispatchTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected NotifierDispatchTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task ForceComplete_DispatchesSagaForceCompletedEvent_PostCommit()
    {
        var sagaId = Guid.NewGuid();
        var ctx = _fixture.CreateContext();
        ctx.Set<SagaState>().Add(new SagaState
        {
            Id = sagaId,
            Type = "Test.NotifierSaga",
            CorrelationKey = "notify-key",
            StateJson = "{}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync(Ct);

        var spy = new SpyNotifier();
        var command = new Warp.Core.Services.SagaCommandService<TestContext>(
            _fixture.CreateContext(),
            new FakeLockProvider(),
            TestNotifiers.SpyDispatcher(spy),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Warp.Core.Services.SagaCommandService<TestContext>>.Instance);

        (await command.ForceComplete(sagaId)).ShouldBeTrue();

        var evt = spy.Received.ShouldHaveSingleItem().ShouldBeOfType<SagaForceCompletedEvent>();
        evt.SagaId.ShouldBe(sagaId);
        evt.SagaType.ShouldBe("Test.NotifierSaga");
        evt.CorrelationKey.ShouldBe("notify-key");

        // Post-commit: the saga row is already gone when the notifier sees the event.
        (await _fixture.CreateContext().Set<SagaState>().Where(x => x.Id == sagaId).CountAsync(Ct)).ShouldBe(0);
    }

    [TimedFact]
    public async Task StaleInstanceSweep_DispatchesInstanceDownEvent_NonServer()
    {
        var instanceId = Guid.NewGuid();
        var lastSeen = DateTime.UtcNow.AddMinutes(-10);
        var ctx = _fixture.CreateContext();
        ctx.Set<ApplicationInstance>().Add(new ApplicationInstance
        {
            Id = instanceId,
            ApplicationName = "publisher-app",
            MachineName = "test-host",
            StartedAt = lastSeen,
            LastHeartbeatAt = lastSeen,
        });
        await ctx.SaveChangesAsync(Ct);

        // The sweep BUFFERS events during ExecuteAsync (it runs inside the host's lock transaction) and the
        // host dispatches them from OnCommittedAsync post-commit. Here we drive both directly: nothing is
        // dispatched until OnCommittedAsync. (End-to-end through the real loop wrapper:
        // NotifierPostCommitIntegrationTestsBase.)
        var spy = new SpyNotifier();
        var cleanup = new ExpirationCleanup<TestContext>(
            new TestServerContext(_fixture.CreateContext()),
            TimeProvider.System,
            Options.Create(new WarpServerConfiguration()),
            TestNotifiers.SpyDispatcher(spy));

        await cleanup.CleanupStaleApplicationInstancesAsync(Ct);
        spy.Received.ShouldBeEmpty("nothing dispatches until the post-commit hook");

        await cleanup.OnCommittedAsync(Ct);

        var evt = spy.Received.ShouldHaveSingleItem().ShouldBeOfType<InstanceDownEvent>();
        evt.InstanceId.ShouldBe(instanceId);
        evt.ApplicationName.ShouldBe("publisher-app");
        evt.IsServer.ShouldBeFalse();
    }

    [TimedFact]
    public async Task StaleServerSweep_DispatchesInstanceDownEvent_Server()
    {
        var serverId = Guid.NewGuid();
        var lastSeen = DateTime.UtcNow.AddHours(-1);
        var ctx = _fixture.CreateContext();
        ctx.Set<Server>().Add(new Server
        {
            Id = serverId,
            ServerName = "worker-1",
            Application = "worker-app",
            StartedTime = lastSeen,
            LastHeartbeatTime = lastSeen,
        });
        await ctx.SaveChangesAsync(Ct);

        var spy = new SpyNotifier();
        var cleanup = new ServerCleanup<TestContext>(
            new TestServerContext(_fixture.CreateContext()),
            TimeProvider.System,
            TestTasks.QueriesFor(_fixture.CreateContext()),
            Options.Create(new WarpServerConfiguration()),
            TestNotifiers.SpyDispatcher(spy));

        await cleanup.CleanUpServersAsync(Ct);
        spy.Received.ShouldBeEmpty("nothing dispatches until the post-commit hook");

        await cleanup.OnCommittedAsync(Ct);

        var evt = spy.Received.ShouldHaveSingleItem().ShouldBeOfType<InstanceDownEvent>();
        evt.InstanceId.ShouldBe(serverId);
        evt.ApplicationName.ShouldBe("worker-app");
        evt.IsServer.ShouldBeTrue();
    }
}
