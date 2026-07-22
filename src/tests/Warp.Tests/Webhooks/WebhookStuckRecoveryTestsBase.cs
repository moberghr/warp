using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
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
/// <c>WebhookStuckDeliveryGrace</c> past, re-enqueues an executor job through the addon-registered
/// <see cref="IWebhookRedeliveryEnqueuer"/> seam, and defers the row's next sweep by bumping
/// <c>NextAttemptAt</c> with a guarded update (the claim pattern — no duplicate enqueue).
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
    public async Task Recover_NoEnqueuerRegistered_LeavesRowUntouched()
    {
        // Core-only server (no AddWebhooks in this process): the sweep must not bump NextAttemptAt — a bump
        // without an enqueued job would just push the stuck row another grace into the future, repeatedly.
        await using var server = await StartIdleServerAsync();
        var past = DateTime.UtcNow.AddHours(-1);
        var deliveryId = await SeedDeliveryAsync(server, nextAttemptAt: past);

        var recovery = new StaleJobRecovery<TestContext>(
            new TestServerContext(server.CreateContext()),
            server.CreateContext(),
            TimeProvider.System,
            TestTasks.QueriesFor(server.CreateContext()),
            Options.Create(new WarpServerConfiguration()),
            webhookEnqueuers: []);

        var recovered = await recovery.RecoverStuckWebhookDeliveriesAsync(Ct);

        recovered.ShouldBe(0);
        (await WebhookJobCountAsync(server)).ShouldBe(0);

        var delivery = await GetDeliveryAsync(server, deliveryId);
        delivery.NextAttemptAt.ShouldNotBeNull();
        delivery.NextAttemptAt!.Value.ShouldBe(past, TimeSpan.FromSeconds(1));
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
        // Worker polls only "default": a recovered executor job lands on "warp:webhooks" and stays Enqueued
        // for a deterministic count assertion instead of racing a worker pickup. The Queues assignment comes
        // deliberately AFTER AddWebhooks — the addon auto-appends its queue (CRITICAL-2), and a worker that
        // consumed the recovered job would re-stamp NextAttemptAt mid-assert.
        // StaleJobRecoveryInterval = null disables the server's OWN background sweep so the test's manual
        // RecoverStuckWebhookDeliveriesAsync is the sole recoverer — otherwise the background tick (holding
        // the task lock) could recover the seeded row first and the guarded bump would make the manual call
        // observe zero (§4.6 deterministic-timing pattern).
        return WarpTestServer.StartAsync(
            Fixture,
            configure: cfg =>
            {
                cfg.AddWebhooks();
                cfg.Queues = ["default"];
                cfg.StaleJobRecoveryInterval = null;
            });
    }

    // Resolved from the server's REAL DI as IServerTask — not hand-constructed — so these tests also prove
    // the production registration wires the scoped TContext and the addon-registered enqueuers into the
    // task's constructor (tests that construct internal seams verify the seam, not the wiring).
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
