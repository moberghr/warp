using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.NoRestart;
using Warp.Core.Webhooks;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Worker;
using Warp.Worker.Services;

namespace Warp.Tests.Webhooks;

/// <summary>
/// Stuck-delivery recovery coverage (BUG-A second half). A persistence fault on the executor's outcome
/// commit leaves a delivery <c>Pending</c> with a claimed attempt and NO live executor job — nothing scans
/// <c>NextAttemptAt</c> and <c>Redeliver</c> rejects <c>Pending</c>, so the <c>StaleJobRecovery</c> sweep
/// is the only path back. The sweep finds <c>Pending</c> rows whose <c>NextAttemptAt</c> is more than
/// <c>WebhookStuckDeliveryGrace</c> past, stages an executor job on the server context, and defers the
/// row's next sweep by bumping <c>NextAttemptAt</c> with a guarded update (the claim pattern — no
/// duplicate enqueue).
/// </summary>
[GenerateDatabaseTests]
public abstract class WebhookStuckRecoveryTestsBase : IntegrationTestBase
{
    protected WebhookStuckRecoveryTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact]
    public async Task Recover_StuckPendingDelivery_EnqueuesExecutorJobAndDefersNextSweep()
    {
        await using var server = await StartIdleServerAsync();
        var deliveryId = await SeedDeliveryAsync(server, nextAttemptAt: DateTime.UtcNow.AddHours(-1));

        using var scope = server.GetService<IServiceScopeFactory>().CreateScope();
        var recovered = await CreateRecovery(scope).RecoverStuckWebhookDeliveriesAsync(Ct);

        recovered.ShouldBe(1);
        (await WebhookJobCountAsync(server)).ShouldBe(1);

        // The bump defers the next sweep a full grace so a slow enqueue can never double-fire.
        var delivery = await GetDeliveryAsync(server, deliveryId);
        delivery.Status.ShouldBe(WebhookDeliveryStatus.Pending);
        delivery.NextAttemptAt.ShouldNotBeNull();
        delivery.NextAttemptAt!.Value.ShouldBeGreaterThan(DateTime.UtcNow);
    }

    [TimedFact]
    public async Task Recover_StuckPendingDelivery_StagesJobThatAlwaysRestarts()
    {
        // Staging the job directly bypasses NoRestartPublishBehavior, which is what would otherwise read
        // [Restart] off ExecuteWebhookDelivery — and it only runs when the host called AddNoRestart(). The
        // sweep must stamp the metadata itself: a recovered executor that crashes mid-attempt has to be
        // re-run for the delivery-completes guarantee to hold, whatever RestartStaleJobsByDefault says.
        await using var server = await StartIdleServerAsync();
        await SeedDeliveryAsync(server, nextAttemptAt: DateTime.UtcNow.AddHours(-1));

        using var scope = server.GetService<IServiceScopeFactory>().CreateScope();
        await CreateRecovery(scope).RecoverStuckWebhookDeliveriesAsync(Ct);

        var job = await server.CreateContext().Set<Job>()
            .AsNoTracking()
            .Where(x => x.Queue == "warp:webhooks")
            .FirstAsync(Ct);

        var metadata = MetadataFactory.Create<ICanBeRestartedMetadata>(MetadataSerializer.Deserialize(job.Metadata));
        metadata.CanBeRestarted.ShouldBe(true);
    }

    [TimedFact]
    public async Task Recover_PendingAwaitingScheduledRetry_Untouched()
    {
        // A Pending row whose NextAttemptAt is still in the future is a healthy delivery waiting on its
        // scheduled retry job — the sweep must not touch it.
        await using var server = await StartIdleServerAsync();
        var future = DateTime.UtcNow.AddMinutes(5);
        var deliveryId = await SeedDeliveryAsync(server, nextAttemptAt: future);

        using var scope = server.GetService<IServiceScopeFactory>().CreateScope();
        var recovered = await CreateRecovery(scope).RecoverStuckWebhookDeliveriesAsync(Ct);

        recovered.ShouldBe(0);
        (await WebhookJobCountAsync(server)).ShouldBe(0);

        var delivery = await GetDeliveryAsync(server, deliveryId);
        delivery.NextAttemptAt.ShouldNotBeNull();
        delivery.NextAttemptAt!.Value.ShouldBe(future, TimeSpan.FromSeconds(1));
    }

    [TimedFact]
    public async Task Recover_RunTwiceOnOneStuckRow_EnqueuesExactlyOneJob()
    {
        await using var server = await StartIdleServerAsync();
        await SeedDeliveryAsync(server, nextAttemptAt: DateTime.UtcNow.AddHours(-1));

        using var scope = server.GetService<IServiceScopeFactory>().CreateScope();
        await CreateRecovery(scope).RecoverStuckWebhookDeliveriesAsync(Ct);
        var second = await CreateRecovery(scope).RecoverStuckWebhookDeliveriesAsync(Ct);

        second.ShouldBe(0);
        (await WebhookJobCountAsync(server)).ShouldBe(1);
    }

    [TimedFact]
    public async Task Recover_StuckRowWithLiveExecutorJob_SkipsWithoutDuplicate()
    {
        // A Pending row past the grace whose executor job still EXISTS (workers merely backlogged, not a
        // lost job) must not get a second job — a duplicate would race the real one into an extra
        // (at-least-once) attempt. The sweep checks for a live executor job before recovering.
        await using var server = await StartIdleServerAsync();
        var deliveryId = await SeedDeliveryAsync(server, nextAttemptAt: DateTime.UtcNow.AddHours(-1));

        // The delivery's own (delayed) executor job, still sitting on the webhooks queue.
        var publisher = server.CreatePublisher();
        await publisher.Enqueue(new ExecuteWebhookDelivery { DeliveryId = deliveryId }, "warp:webhooks");
        await publisher.SaveChangesAsync(Ct);

        using var scope = server.GetService<IServiceScopeFactory>().CreateScope();
        var recovered = await CreateRecovery(scope).RecoverStuckWebhookDeliveriesAsync(Ct);

        recovered.ShouldBe(0);
        (await WebhookJobCountAsync(server)).ShouldBe(1);

        // Untouched: the sweep must not bump NextAttemptAt for a row it did not recover.
        var delivery = await GetDeliveryAsync(server, deliveryId);
        delivery.NextAttemptAt.ShouldNotBeNull();
        delivery.NextAttemptAt!.Value.ShouldBeLessThan(DateTime.UtcNow);
    }

    [TimedFact]
    public async Task Recover_StaleExecutorJobInSameSweep_CompletesWithoutBlocking()
    {
        // Both halves of ExecuteAsync in one tick, run the way the task host runs them: inside the
        // xact-lock transaction. The stale-job sweep locks and requeues delivery B's crashed executor job
        // and holds those row locks until the transaction commits — which cannot happen until ExecuteAsync
        // returns. The webhook sweep then probes the webhooks queue for delivery A's job, which must read
        // B's locked row. While that probe ran on the user's TContext it was a SECOND connection, so under
        // read-committed locking (SQL Server without RCSI) it waited on a transaction that could never
        // commit — a self-block until the command timeout. On the server context it is the same connection
        // and sees its own uncommitted write, so the sweep completes and recovers A.
        await using var server = await StartIdleServerAsync();

        var stuckId = await SeedDeliveryAsync(server, nextAttemptAt: DateTime.UtcNow.AddHours(-1));
        var crashedId = await SeedDeliveryAsync(server, nextAttemptAt: DateTime.UtcNow.AddHours(-1));
        var staleJobId = await SeedStaleExecutorJobAsync(server, crashedId);

        using var scope = server.GetService<IServiceScopeFactory>().CreateScope();
        var serverContext = scope.ServiceProvider.GetRequiredService<IWarpServerContext>().Context;
        var queries = scope.ServiceProvider.GetRequiredService<IWarpSqlQueries<TestContext>>();

        var outcome = await queries.RunUnderTransactionLockAsync(
            serverContext,
            "warp:stale-job-recovery",
            async (_, ct) => await CreateRecovery(scope).ExecuteAsync(ct),
            Ct);

        outcome.LockHeld.ShouldBeTrue();

        // The crashed executor was requeued by the job sweep, so its delivery counts as having live work
        // and gets no second job; only the genuinely job-less delivery is recovered.
        var staleJob = await server.CreateContext().Set<Job>().AsNoTracking().FirstAsync(x => x.Id == staleJobId, Ct);
        staleJob.CurrentState.ShouldBe(State.Enqueued);

        (await WebhookJobCountAsync(server)).ShouldBe(2);
        (await GetDeliveryAsync(server, stuckId)).NextAttemptAt!.Value.ShouldBeGreaterThan(DateTime.UtcNow);
        (await GetDeliveryAsync(server, crashedId)).NextAttemptAt!.Value.ShouldBeLessThan(DateTime.UtcNow);
    }

    [TimedFact]
    public async Task Recover_SettledDelivery_Ignored()
    {
        await using var server = await StartIdleServerAsync();
        await SeedDeliveryAsync(server, nextAttemptAt: DateTime.UtcNow.AddHours(-1), status: WebhookDeliveryStatus.Exhausted);

        using var scope = server.GetService<IServiceScopeFactory>().CreateScope();
        var recovered = await CreateRecovery(scope).RecoverStuckWebhookDeliveriesAsync(Ct);

        recovered.ShouldBe(0);
        (await WebhookJobCountAsync(server)).ShouldBe(0);
    }

    [TimedFact]
    public async Task Recover_StuckDelivery_FullCircle_WorkerDelivers()
    {
        // The money path: stuck row → sweep (via the public IServerTask surface) → executor job → worker
        // runs it → Delivered. Proves the recovered job is a real, executable executor job.
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg =>
            {
                cfg.Queues = ["default"];
                cfg.AddWebhooks();
                cfg.StaleJobRecoveryInterval = null;
            },
            configureServices: services =>
                services.AddHttpClient("warp-webhooks")
                    .ConfigurePrimaryHttpMessageHandler(() => new StubWebhookHandler(HttpStatusCode.OK)));

        var deliveryId = await SeedDeliveryAsync(server, nextAttemptAt: DateTime.UtcNow.AddHours(-1));

        using var scope = server.GetService<IServiceScopeFactory>().CreateScope();
        await CreateRecovery(scope).ExecuteAsync(Ct);

        await WarpTestServer.WaitUntil(
            async () => await server.CreateContext().Set<WebhookDelivery>()
                .Where(x => x.Id == deliveryId)
                .Select(x => (WebhookDeliveryStatus?)x.Status)
                .FirstOrDefaultAsync(Ct) == WebhookDeliveryStatus.Delivered,
            timeout: TimeSpan.FromSeconds(8),
            ct: Ct);
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private Task<WarpTestServer> StartIdleServerAsync()
    {
        // Webhook delivery is now always-on (§8.20): the implicit default worker group polls warp:webhooks
        // unconditionally and there is deliberately no config to opt a server out. To keep the seeded and
        // recovered executor jobs off a live worker deterministically (no pause-timing race), the implicit
        // default group is given zero workers so registration skips it and its webhooks subscription is never
        // polled, while the sole worker runs in an explicit group bound to the default queue only (explicit
        // groups get no webhooks subscription). The stale-recovery task is still wired because worker mode is
        // on, so the test resolves it and invokes recovery manually. A null recovery interval keeps the
        // server's own background sweep off, leaving the manual call as the sole recoverer (the deterministic
        // timing pattern of section 4.6).
        return WarpTestServer.StartAsync(
            Fixture,
            configure: cfg =>
            {
                cfg.WorkerCount = 0;
                cfg.StaleJobRecoveryInterval = null;
                cfg.AddWorkerGroup(g =>
                {
                    g.WorkerCount = 1;
                    g.Queues = ["default"];
                });
            });
    }

    // Resolved from the server's REAL DI as IServerTask — not hand-constructed — so these tests also prove
    // the production registration wires the scoped server context into the task's constructor, and that it
    // is a genuinely separate DbContext from the user's TContext (a hand-built task can share one, which is
    // exactly what the same-sweep blocking test must not do).
    private static StaleJobRecovery<TestContext> CreateRecovery(IServiceScope scope)
        => scope.ServiceProvider.GetServices<IServerTask>()
            .OfType<StaleJobRecovery<TestContext>>()
            .Single();

    private static async Task<Guid> SeedDeliveryAsync(
        WarpTestServer server,
        DateTime nextAttemptAt,
        WebhookDeliveryStatus status = WebhookDeliveryStatus.Pending)
    {
        var delivery = new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            EventType = "order.created",
            EventId = Guid.NewGuid().ToString(),
            Url = "https://example.test/hook",
            PayloadJson = "{}",
            SigningMode = WebhookSigning.None,
            RetrySchedule = [],
            Status = status,
            AttemptCount = 1,
            NextAttemptAt = nextAttemptAt,
            CreatedAt = DateTime.UtcNow.AddHours(-2),
        };

        var ctx = server.CreateContext();
        ctx.Set<WebhookDelivery>().Add(delivery);
        await ctx.SaveChangesAsync(Ct);

        return delivery.Id;
    }

    // The delivery's executor job as a crashed worker left it: Processing, with a keep-alive well past the
    // invisibility timeout so the stale-job sweep claims and requeues it.
    private static async Task<Guid> SeedStaleExecutorJobAsync(WarpTestServer server, Guid deliveryId)
    {
        var job = new Job
        {
            Id = Guid.NewGuid(),
            Type = typeof(ExecuteWebhookDelivery).AssemblyQualifiedName!,
            Message = JsonSerializer.Serialize(new ExecuteWebhookDelivery { DeliveryId = deliveryId }),
            Queue = "warp:webhooks",
            CurrentState = State.Processing,
            CreateTime = DateTime.UtcNow.AddHours(-1),
            ScheduleTime = DateTime.UtcNow.AddHours(-1),
            LastKeepAlive = DateTime.UtcNow.AddHours(-1),
        };

        var ctx = server.CreateContext();
        ctx.Set<Job>().Add(job);
        await ctx.SaveChangesAsync(Ct);

        return job.Id;
    }

    private static async Task<WebhookDelivery> GetDeliveryAsync(WarpTestServer server, Guid id)
    {
        return await server.CreateContext().Set<WebhookDelivery>()
            .AsNoTracking()
            .Where(x => x.Id == id)
            .FirstAsync(Ct);
    }

    private static async Task<int> WebhookJobCountAsync(WarpTestServer server)
    {
        return await server.CreateContext().Set<Job>()
            .CountAsync(x => x.Queue == "warp:webhooks", Ct);
    }
}
