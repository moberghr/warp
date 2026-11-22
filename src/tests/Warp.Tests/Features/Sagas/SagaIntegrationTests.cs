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

        // The handler job fails (after its retry). WaitUntil throws on timeout, where the loop this
        // replaced fell through and let the test pass with the job still running.
        await WarpTestServer.WaitUntil(
            async () => await server.CreateContext().Set<Warp.Core.Entities.Job>()
                .Where(x => x.ParentJobId == msgId)
                .Where(x => x.CurrentState == Warp.Core.Enums.State.Failed)
                .AnyAsync(Xunit.TestContext.Current.CancellationToken),
            TimeSpan.FromSeconds(10),
            Xunit.TestContext.Current.CancellationToken);

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

        try
        {
            var ct = Xunit.TestContext.Current.CancellationToken;
            var publisher = server.CreatePublisher();
            await publisher.Publish(new Warp.Tests.TestData.Sagas.BarrierStartsMessage { CorrelationKey = "contended" });
            await publisher.SaveChangesAsync(ct);

            // Wait for the first handler to be pinned at the barrier, and assert it: WaitAsync returns
            // false on timeout, and proceeding would run everything below on the false premise that the
            // mutex is held.
            var pinned = await signal.Running.WaitAsync(TimeSpan.FromSeconds(8), ct);
            pinned.ShouldBeTrue("first saga handler was not picked up and pinned at the barrier within 8s");

            // A second message for the same correlation key: the proxy fails to acquire the mutex the
            // first handler holds and requeues with a jittered ScheduleTime.
            await publisher.Publish(new Warp.Tests.TestData.Sagas.BarrierStartMessage { CorrelationKey = "contended" });
            await publisher.SaveChangesAsync(ct);

            // While the first handler stays pinned, the second job keeps cycling: busy, Scheduled a
            // jittered 50-250 ms ahead, activated, busy again. So neither its state nor its ScheduleTime
            // holds still, and a single read of the row can land between two cycles (this test used to
            // do that, and also compared ScheduleTime with the job's CreateTime, which every requeue
            // pushes further apart). Instead wait for one observation of the property itself: the job
            // parked in Scheduled, a short jitter after the busy requeue that put it there. Without
            // jitter it would be Enqueued at now and N workers would lock-step into a tight
            // fetch, busy, fetch loop on the same mutex.
            var sawBusy = false;
            await WarpTestServer.WaitUntil(
                async () =>
                {
                    await using var ctx = server.CreateContext();
                    var job = await ctx.Set<Warp.Core.Entities.Job>()
                        .AsNoTracking()
                        .Where(x => x.Type != null && x.Type.Contains("BarrierStartMessage"))
                        .Where(x => x.CurrentState == Warp.Core.Enums.State.Scheduled)
                        .Select(x => new { x.Id, x.ScheduleTime })
                        .FirstOrDefaultAsync(ct);
                    if (job == null)
                    {
                        return false;
                    }

                    var busyAt = await ctx.Set<Warp.Core.Data.Entities.JobLog>()
                        .AsNoTracking()
                        .Where(x => x.JobId == job.Id)
                        .Where(x => x.Message != null && x.Message.Contains("busy for 'contended'"))
                        .OrderByDescending(x => x.Timestamp)
                        .Select(x => (DateTime?)x.Timestamp)
                        .FirstOrDefaultAsync(ct);
                    if (busyAt == null)
                    {
                        return false;
                    }

                    sawBusy = true;
                    var jitter = job.ScheduleTime - busyAt.Value;

                    return jitter > TimeSpan.Zero && jitter < TimeSpan.FromSeconds(1);
                },
                TimeSpan.FromSeconds(8),
                ct);

            sawBusy.ShouldBeTrue();
        }
        finally
        {
            // Always, not only on the passing path: with the handler still pinned, stopping the server
            // waits on it, and an assertion that failed above would surface as a bare 20 s test timeout
            // instead of its own message (#215).
            signal.CanFinish.Release(10);
        }
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
