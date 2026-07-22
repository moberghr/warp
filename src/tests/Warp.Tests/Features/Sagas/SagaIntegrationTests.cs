using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Sagas;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Sagas;

namespace Warp.Tests.Features.Sagas;

[GenerateDatabaseTests(SerializeInCollection = "HeavyIntegration")]
public abstract class SagaIntegrationTestsBase : IntegrationTestBase
{
    protected SagaIntegrationTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact(20_000)]
    public async Task ThreeMessageFlow_InOrder_SagaCompletesAndRowRemoved()
    {
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg => cfg.AddSagas(),
            configureServices: services => services.AddSagaHandler<OrderSagaHandler>());

        var publisher = server.CreatePublisher();
        var placed = await publisher.Publish(new OrderPlaced { OrderId = "O-1" });
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        await WaitForSagaState(server, "O-1", paymentCaptured: false, inventoryReserved: false);

        var payment = await publisher.Publish(new PaymentCaptured { OrderId = "O-1" });
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        await WaitForSagaState(server, "O-1", paymentCaptured: true, inventoryReserved: false);

        var inventory = await publisher.Publish(new InventoryReserved { OrderId = "O-1" });
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        await WaitForSagaRemoved(server, "O-1");

        // All three message-routed handler jobs ended in Completed state.
        // WaitForSagaRemoved returns when the saga row is deleted inside the handler's
        // SaveChanges, but the worker writes State.Completed for the handler-job in a
        // separate UPDATE after the handler returns — poll for that transition rather
        // than snapshot, otherwise the last child can still be Processing here.
        var jobIds = new[] { placed, payment, inventory };
        foreach (var jobId in jobIds)
        {
            await WaitForChildrenCompleted(server, jobId);
        }
    }

    [TimedFact(20_000)]
    public async Task ThreeMessageFlow_OutOfOrder_SagaCompletes()
    {
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg => cfg.AddSagas(),
            configureServices: services => services.AddSagaHandler<OrderSagaHandler>());

        var publisher = server.CreatePublisher();
        await publisher.Publish(new OrderPlaced { OrderId = "O-2" });
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        await WaitForSagaState(server, "O-2", paymentCaptured: false, inventoryReserved: false);

        await publisher.Publish(new InventoryReserved { OrderId = "O-2" });
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        await WaitForSagaState(server, "O-2", paymentCaptured: false, inventoryReserved: true);

        await publisher.Publish(new PaymentCaptured { OrderId = "O-2" });
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        await WaitForSagaRemoved(server, "O-2");
    }

    [TimedFact(20_000)]
    public async Task NonStartsSagaMessageForUnknownKey_FailsTheJob_DefaultNotFound()
    {
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg => cfg.AddSagas(),
            configureServices: services => services.AddSagaHandler<OrderSagaHandler>());

        var publisher = server.CreatePublisher();
        var msgId = await publisher.Publish(new PaymentCaptured { OrderId = "never-started" });
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Wait until at least one child handler-job is in Failed state.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var failed = await server.CreateContext().Set<Warp.Core.Entities.Job>()
                .Where(x => x.ParentJobId == msgId)
                .Where(x => x.CurrentState == Warp.Core.Enums.State.Failed)
                .AnyAsync(Xunit.TestContext.Current.CancellationToken);
            if (failed)
            {
                break;
            }

            await Task.Delay(100, Xunit.TestContext.Current.CancellationToken);
        }

        var sagaExists = await server.CreateContext().Set<SagaState>()
            .Where(x => x.CorrelationKey == "never-started")
            .AnyAsync(Xunit.TestContext.Current.CancellationToken);
        sagaExists.ShouldBeFalse();
    }

    [TimedFact(20_000)]
    public async Task CompletedSaga_NewMessageForSameCorrelation_StartsFreshSaga()
    {
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg => cfg.AddSagas(),
            configureServices: services => services.AddSagaHandler<OrderSagaHandler>());

        var publisher = server.CreatePublisher();
        await publisher.Publish(new OrderPlaced { OrderId = "O-reuse" });
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        await WaitForSagaState(server, "O-reuse", paymentCaptured: false, inventoryReserved: false);

        await publisher.Publish(new PaymentCaptured { OrderId = "O-reuse" });
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        await publisher.Publish(new InventoryReserved { OrderId = "O-reuse" });
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        await WaitForSagaRemoved(server, "O-reuse");

        // Start a fresh saga with the same correlation key — must succeed (unique index covers
        // only live rows because the previous row was deleted on completion).
        await publisher.Publish(new OrderPlaced { OrderId = "O-reuse" });
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        await WaitForSagaState(server, "O-reuse", paymentCaptured: false, inventoryReserved: false);
    }

    [TimedFact(20_000)]
    public async Task TwoConcurrentMessagesSameSaga_OneRequeuesWithBusyOutcome()
    {
        // Per memory feedback_no_spray_n_tests: pin a worker in the handler with a BarrierSignal
        // and use N=2, not spray-50. Asserting the deterministic outcome: first message holds
        // the mutex (blocked at barrier), second message hits busy → Enqueued + ClearHandlerType=false.
        var signal = new Warp.Tests.TestData.Handlers.BarrierSignal();
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg => cfg.AddSagas(),
            configureServices: services =>
            {
                services.AddSingleton(signal);
                services.AddSagaHandler<Warp.Tests.TestData.Sagas.BarrierSagaHandler>();
            });

        var publisher = server.CreatePublisher();
        await publisher.Publish(new Warp.Tests.TestData.Sagas.BarrierStartsMessage { CorrelationKey = "contended" });
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Wait for the first handler to be pinned at the barrier. Assert the wait actually
        // succeeded — SemaphoreSlim.WaitAsync returns false on timeout, and silently proceeding
        // would run every downstream assertion on the false premise that the mutex is held,
        // masking the real failure (starved worker / handler never picked up) behind a generic
        // 20s hang. Fail fast and descriptively instead.
        var pinned = await signal.Running.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);
        pinned.ShouldBeTrue("first saga handler was not picked up and pinned at the barrier within 10s");

        // Now publish a second message for the same correlation key. The proxy will fail to
        // acquire the mutex (held by the first handler) and set Outcome = Enqueued requeue.
        await publisher.Publish(new Warp.Tests.TestData.Sagas.BarrierStartMessage { CorrelationKey = "contended" });
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Wait until at least one routed handler-job has a JobLog with the "busy" message.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var sawBusy = false;
        while (DateTime.UtcNow < deadline)
        {
            sawBusy = await server.CreateContext().Set<Warp.Core.Data.Entities.JobLog>()
                .Where(x => x.Message != null && x.Message.Contains("busy for 'contended'"))
                .AnyAsync(Xunit.TestContext.Current.CancellationToken);
            if (sawBusy)
            {
                break;
            }

            await Task.Delay(100, Xunit.TestContext.Current.CancellationToken);
        }

        sawBusy.ShouldBeTrue("expected a 'busy for contended' requeue log within 10s");

        // Verify jitter survives to the DB: the requeued routed-message job sits in Scheduled
        // with ScheduleTime in the near future, not Enqueued at now. Without jitter, N workers
        // hitting the same busy mutex would lock-step into a tight fetch→busy→fetch loop. The
        // 50–250ms jitter prevents sympathy.
        var jitteredJob = await server.CreateContext().Set<Warp.Core.Entities.Job>()
            .AsNoTracking()
            .Where(j => j.CurrentState == Warp.Core.Enums.State.Scheduled)
            .Where(j => j.Type != null && j.Type.Contains("BarrierStartMessage"))
            .OrderByDescending(j => j.CreateTime)
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);

        jitteredJob.ShouldNotBeNull("expected the requeued message-job to sit in Scheduled state");
        var delay = jitteredJob.ScheduleTime - jitteredJob.CreateTime;
        delay.TotalMilliseconds.ShouldBeGreaterThan(0, "ScheduleTime must be in the future relative to CreateTime (jitter applied)");
        delay.TotalMilliseconds.ShouldBeLessThan(1000, "jitter should be capped well under 1s");

        // Release the barrier so the first handler completes and the test doesn't hang in cleanup.
        signal.CanFinish.Release(10);
    }

    [TimedFact(20_000)]
    public async Task SagaHandlerPublishesChildJob_ChildReachesEnqueuedAndCompletes()
    {
        // Validates the S1 contract end-to-end: a saga handler that calls IPublisher.Enqueue
        // without explicitly saving must still have its child rows committed (and notifications
        // fired) via SagaStore.SaveChangesAsync. If the proxy skipped save in any branch the
        // child would be stuck in the change tracker until the worker's outbox commit, which
        // wouldn't fire notifications for already-flushed rows.
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg => cfg.AddSagas(),
            configureServices: services => services.AddSagaHandler<OrderPlacedPublishingChildHandler>());

        var publisher = server.CreatePublisher();
        await publisher.Publish(new OrderPlacedPublishingChild { OrderId = "child-test" });
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Wait for the saga to be created (proxy committed → saga row exists).
        await WaitForSagaState(server, "child-test", paymentCaptured: false, inventoryReserved: false);

        // The saga handler published a FollowUpJob. Wait for it to reach Completed via the worker.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var followUp = await server.CreateContext().Set<Warp.Core.Entities.Job>()
                .Where(x => x.Type != null && x.Type.Contains("FollowUpJob"))
                .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);
            if (followUp != null && followUp.CurrentState == Warp.Core.Enums.State.Completed)
            {
                followUp.Kind.ShouldBe(Warp.Core.Enums.JobKind.Job);
                return;
            }

            await Task.Delay(100, Xunit.TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("FollowUpJob published from saga handler did not reach Completed within 10s");
    }

    [TimedFact(20_000)]
    public async Task TimeoutMessage_FiresAfterDelay_WhenSagaStillLive()
    {
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg => cfg.AddSagas(),
            configureServices: services => services.AddSagaHandler<OrderSagaHandler>());

        var publisher = server.CreatePublisher();
        await publisher.Publish(new OrderPlaced { OrderId = "O-timeout" });
        await publisher.Publish(new OrderTimeout { OrderId = "O-timeout", Delay = TimeSpan.FromMilliseconds(500) });
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // The saga should be removed (timeout handler calls MarkCompleted).
        await WaitForSagaRemoved(server, "O-timeout");
    }

    [TimedFact(20_000)]
    public async Task TimeoutMessage_FiresAfterSagaCompleted_SilentlyDeletesJob()
    {
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg => cfg.AddSagas(),
            configureServices: services => services.AddSagaHandler<OrderSagaHandler>());

        var publisher = server.CreatePublisher();

        // Scenario: saga is fully completed (row deleted) before the timeout fires. When the
        // timeout arrives at the missing saga, the proxy must silently transition the routed
        // handler-job to Deleted (not Failed) — Wolverine's "moot timeout" semantics. Without
        // this, every saga that completes before its timeout fires would produce a spurious
        // Failed job and a noisy alert.
        //
        // Serialize the publishes via WaitForSagaState between steps: the messages all share
        // the same ScheduleTime so MessageRouter may pick them up in any order, and an
        // out-of-order arrival (e.g. PaymentCaptured before OrderPlaced) hits the default
        // NotFoundAsync = Failed path and breaks the scenario.
        await publisher.Publish(new OrderPlaced { OrderId = "O-late" });
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        await WaitForSagaState(server, "O-late", paymentCaptured: false, inventoryReserved: false);

        await publisher.Publish(new PaymentCaptured { OrderId = "O-late" });
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        await WaitForSagaState(server, "O-late", paymentCaptured: true, inventoryReserved: false);

        await publisher.Publish(new InventoryReserved { OrderId = "O-late" });
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        await WaitForSagaRemoved(server, "O-late");

        // Now publish the timeout — saga is already gone.
        var timeoutMsgId = await publisher.Publish(new OrderTimeout { OrderId = "O-late", Delay = TimeSpan.FromMilliseconds(300) });
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // After the timeout fires (delay 500ms), its routed handler-job should be Deleted, not Failed.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var ctx = server.CreateContext();
            var children = await ctx.Set<Warp.Core.Entities.Job>()
                .Where(x => x.ParentJobId == timeoutMsgId)
                .ToListAsync(Xunit.TestContext.Current.CancellationToken);
            if (children.Count > 0 && children.All(c => c.CurrentState == Warp.Core.Enums.State.Deleted))
            {
                return;
            }

            await Task.Delay(100, Xunit.TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("Timeout-message handler-job was not transitioned to Deleted after the saga completed");
    }

    private static async Task WaitForSagaState(WarpTestServer server, string correlationKey, bool paymentCaptured, bool inventoryReserved)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var typeName = typeof(OrderSaga).FullName!;
        while (DateTime.UtcNow < deadline)
        {
            var json = await server.CreateContext().Set<SagaState>()
                .Where(x => x.Type == typeName && x.CorrelationKey == correlationKey)
                .Select(x => x.StateJson)
                .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);

            if (json != null)
            {
                var saga = System.Text.Json.JsonSerializer.Deserialize<OrderSaga>(json);
                if (saga != null && saga.PaymentCaptured == paymentCaptured && saga.InventoryReserved == inventoryReserved)
                {
                    return;
                }
            }

            await Task.Delay(100, Xunit.TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Saga '{correlationKey}' did not reach expected state (paymentCaptured={paymentCaptured}, inventoryReserved={inventoryReserved})");
    }

    private static async Task WaitForChildrenCompleted(WarpTestServer server, Guid parentJobId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        List<Warp.Core.Entities.Job> children = [];
        while (DateTime.UtcNow < deadline)
        {
            children = await server.CreateContext().Set<Warp.Core.Entities.Job>()
                .AsNoTracking()
                .Where(x => x.ParentJobId == parentJobId)
                .ToListAsync(Xunit.TestContext.Current.CancellationToken);

            if (children.Count > 0 && children.All(c => c.CurrentState == Warp.Core.Enums.State.Completed))
            {
                return;
            }

            await Task.Delay(100, Xunit.TestContext.Current.CancellationToken);
        }

        var snapshot = children.Count == 0
            ? "<no children>"
            : string.Join(", ", children.Select(c => $"{c.Id}={c.CurrentState}"));
        throw new TimeoutException($"Children of {parentJobId} did not all reach Completed within 10s. States: {snapshot}");
    }

    private static async Task WaitForSagaRemoved(WarpTestServer server, string correlationKey)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var typeName = typeof(OrderSaga).FullName!;
        while (DateTime.UtcNow < deadline)
        {
            var exists = await server.CreateContext().Set<SagaState>()
                .Where(x => x.Type == typeName && x.CorrelationKey == correlationKey)
                .AnyAsync(Xunit.TestContext.Current.CancellationToken);

            if (!exists)
            {
                return;
            }

            await Task.Delay(100, Xunit.TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Saga '{correlationKey}' was not removed within 10s");
    }
}
