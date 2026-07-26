using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Webhooks;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Webhooks;

/// <summary>
/// Redelivery coverage for <see cref="IWebhookCommandService"/> (WSC7 + review CRITICAL-1 / RACE / W-6).
/// Runs the real <c>AddWebhooks</c> DI wiring via <see cref="WarpTestServer"/> so the executor-job enqueue
/// goes through the addon-registered <see cref="IWebhookRedeliveryEnqueuer"/> seam (not a hand-built
/// internal — adapters lesson). The server's worker polls only <c>default</c>, so a redelivered executor job
/// lands on <c>warp:webhooks</c> and stays <c>Enqueued</c> for a deterministic assertion instead of racing a
/// worker pickup. Each test drives exactly one <c>Redeliver</c> call (§4.8), except the RACE test which
/// deliberately drives two concurrently.
/// </summary>
[GenerateDatabaseTests]
public abstract class WebhookCommandServiceTestsBase : IntegrationTestBase
{
    protected WebhookCommandServiceTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact]
    public async Task Redeliver_DeliveredDelivery_ResetsToPendingAndEnqueuesExecutorJob()
    {
        await using var server = await StartServerAsync();
        var deliveryId = await SeedDeliveryAsync(server, WebhookDeliveryStatus.Delivered, attemptCount: 3);

        var result = await RedeliverAsync(server, deliveryId);

        result.ShouldBe(WebhookRedeliveryResult.Enqueued);

        var delivery = await GetDeliveryAsync(server, deliveryId);
        delivery.Status.ShouldBe(WebhookDeliveryStatus.Pending);
        delivery.AttemptCount.ShouldBe(0);

        (await WebhookJobCountAsync(server)).ShouldBe(1);
    }

    [TimedFact]
    public async Task Redeliver_ExhaustedDelivery_ResetsToPendingAndEnqueuesExecutorJob()
    {
        await using var server = await StartServerAsync();
        var deliveryId = await SeedDeliveryAsync(server, WebhookDeliveryStatus.Exhausted, attemptCount: 5);

        var result = await RedeliverAsync(server, deliveryId);

        result.ShouldBe(WebhookRedeliveryResult.Enqueued);

        var delivery = await GetDeliveryAsync(server, deliveryId);
        delivery.Status.ShouldBe(WebhookDeliveryStatus.Pending);
        delivery.AttemptCount.ShouldBe(0);

        (await WebhookJobCountAsync(server)).ShouldBe(1);
    }

    [TimedFact]
    public async Task Redeliver_PendingDelivery_RejectedWithoutSideEffects()
    {
        await using var server = await StartServerAsync();
        var deliveryId = await SeedDeliveryAsync(server, WebhookDeliveryStatus.Pending, attemptCount: 2);

        var result = await RedeliverAsync(server, deliveryId);

        result.ShouldBe(WebhookRedeliveryResult.Rejected);

        var delivery = await GetDeliveryAsync(server, deliveryId);
        delivery.Status.ShouldBe(WebhookDeliveryStatus.Pending);
        delivery.AttemptCount.ShouldBe(2);

        // No executor job was enqueued — a Pending delivery already owns its live job.
        (await WebhookJobCountAsync(server)).ShouldBe(0);
    }

    [TimedFact]
    public async Task Redeliver_UnknownId_ReturnsNotFound()
    {
        await using var server = await StartServerAsync();

        var result = await RedeliverAsync(server, Guid.NewGuid());

        result.ShouldBe(WebhookRedeliveryResult.NotFound);
        (await WebhookJobCountAsync(server)).ShouldBe(0);
    }

    [TimedFact]
    public async Task Redeliver_NoEnqueuerRegistered_ReturnsUnavailableAndLeavesRowUntouched()
    {
        // Dashboard-only / publisher-only shape: no IWebhookRedeliveryEnqueuer. Mutating here would strand
        // the delivery Pending with no worker to run it and nothing scanning NextAttemptAt (CRITICAL-1).
        var deliveryId = await SeedDeliveryDirectAsync(WebhookDeliveryStatus.Exhausted, attemptCount: 4);

        var command = new WebhookCommandService<TestContext>(
            Fixture.CreateContext(),
            TimeProvider.System,
            Options.Create(new WarpConfiguration()),
            []);

        var result = await command.Redeliver(deliveryId, Ct);

        result.ShouldBe(WebhookRedeliveryResult.Unavailable);

        var delivery = await Fixture.CreateContext().Set<WebhookDelivery>()
            .AsNoTracking()
            .Where(x => x.Id == deliveryId)
            .FirstAsync(Ct);
        delivery.Status.ShouldBe(WebhookDeliveryStatus.Exhausted);
        delivery.AttemptCount.ShouldBe(4);
    }

    [TimedFact]
    public async Task Redeliver_SettledDelivery_RefreshesExpireAt()
    {
        await using var server = await StartServerAsync();

        // An in-flight (soon-to-be-Pending) delivery must not be swept mid-schedule: redeliver refreshes
        // ExpireAt to now + retention (W-6). Seed a row already past its old ExpireAt.
        var deliveryId = await SeedDeliveryAsync(
            server,
            WebhookDeliveryStatus.Exhausted,
            attemptCount: 2,
            expireAt: DateTime.UtcNow.AddDays(-1));

        var before = DateTime.UtcNow;

        var result = await RedeliverAsync(server, deliveryId);

        result.ShouldBe(WebhookRedeliveryResult.Enqueued);

        var delivery = await GetDeliveryAsync(server, deliveryId);
        delivery.ExpireAt.ShouldNotBeNull();
        delivery.ExpireAt!.Value.ShouldBeGreaterThan(before);
    }

    [TimedFact]
    public async Task Redeliver_SettledDelivery_RefreshesCorrelatedAttemptRowExpireAt()
    {
        // SMALL-6: redeliver refreshes the delivery's ExpireAt, but the old AdapterCallLog attempt rows (keyed
        // by CorrelationId = delivery id) kept their original ExpireAt and were swept early, truncating the
        // timeline. Redeliver now refreshes the correlated attempt rows in the same transaction. Driven with a
        // direct command-service call (no running server), so the assertion is deterministic (§4.8).
        var deliveryId = await SeedDeliveryDirectAsync(WebhookDeliveryStatus.Exhausted, attemptCount: 2);

        // An existing attempt row for this delivery, already past its old ExpireAt.
        var ctx = Fixture.CreateContext();
        ctx.Set<AdapterCallLog>().Add(new AdapterCallLog
        {
            Id = Guid.NewGuid(),
            AdapterName = WebhookConstants.AdapterName,
            Operation = "order.created",
            CorrelationId = deliveryId.ToString(),
            Timestamp = DateTime.UtcNow.AddDays(-2),
            DurationMs = 5,
            Attempts = 1,
            Outcome = AdapterCallOutcome.Failed,
            MachineName = "test-host",
            ExpireAt = DateTime.UtcNow.AddDays(-1),
        });
        await ctx.SaveChangesAsync(Ct);

        var before = DateTime.UtcNow;

        var command = new WebhookCommandService<TestContext>(
            Fixture.CreateContext(),
            TimeProvider.System,
            Options.Create(new WarpConfiguration()),
            [new NoOpRedeliveryEnqueuer()]);

        var result = await command.Redeliver(deliveryId, Ct);
        result.ShouldBe(WebhookRedeliveryResult.Enqueued);

        var attempt = await Fixture.CreateContext().Set<AdapterCallLog>()
            .AsNoTracking()
            .Where(x => x.CorrelationId == deliveryId.ToString())
            .FirstAsync(Ct);

        attempt.ExpireAt.ShouldNotBeNull();
        attempt.ExpireAt!.Value.ShouldBeGreaterThan(before);
    }

    [TimedFact]
    public async Task Redeliver_ExhaustedWithCallbackPending_PreservesCallbackObligation()
    {
        // BUG-B pin: the settled→Pending flip must not erase an outstanding exhausted-callback obligation
        // (a crash between the Exhausted commit and the callback). The flag rides through Redeliver so the
        // redelivered executor run fires the prior exhaustion's callback before attempting.
        await using var server = await StartServerAsync();
        var deliveryId = await SeedDeliveryAsync(server, WebhookDeliveryStatus.Exhausted, attemptCount: 5);

        var ctx = server.CreateContext();
        await ctx.Set<WebhookDelivery>()
            .Where(x => x.Id == deliveryId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExhaustedCallbackPending, true), Ct);

        var result = await RedeliverAsync(server, deliveryId);

        result.ShouldBe(WebhookRedeliveryResult.Enqueued);
        (await GetDeliveryAsync(server, deliveryId)).ExhaustedCallbackPending.ShouldBeTrue();
    }

    [TimedFact]
    public async Task Redeliver_TwoConcurrentOnOneSettledDelivery_EnqueuesExactlyOneJob()
    {
        await using var server = await StartServerAsync();
        var deliveryId = await SeedDeliveryAsync(server, WebhookDeliveryStatus.Exhausted, attemptCount: 3);

        // Two concurrent redelivers on one settled delivery: the guarded settled→Pending ExecuteUpdate lets
        // exactly one win (rowcount 1) and the other match zero rows → Rejected. Exactly one executor job.
        var scopeFactory = server.GetService<IServiceScopeFactory>();

        var results = await Task.WhenAll(
            RedeliverInNewScopeAsync(scopeFactory, deliveryId),
            RedeliverInNewScopeAsync(scopeFactory, deliveryId));

        results.Count(x => x == WebhookRedeliveryResult.Enqueued).ShouldBe(1);
        results.Count(x => x == WebhookRedeliveryResult.Rejected).ShouldBe(1);

        (await WebhookJobCountAsync(server)).ShouldBe(1);
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private Task<WarpTestServer> StartServerAsync()
    {
        return WarpTestServer.StartAsync(
            Fixture,
            configure: cfg =>
            {
                cfg.AddWebhooks();

                // These tests assert a redelivered executor job is ENQUEUED on warp:webhooks (and the delivery
                // row's reset state) — they never execute it. The default worker group force-appends
                // warp:webhooks to its queue list unconditionally (WarpServerConfiguration, §8.20), so
                // cfg.Queues cannot exclude it — a running worker WILL drain warp:webhooks and execute the
                // redelivered job under load (flipping AttemptCount off 0 / the queue count off 1). Disable the
                // worker entirely: the redelivery enqueuer + command service still stage the job (they're
                // registered regardless of the worker), and with no worker the job stays Enqueued for a
                // deterministic assertion. (Before the webhook executor's IHttpClientFactory was registered by
                // AddWarp, this raced only when the executor happened to fail to construct — now it constructs
                // and would deterministically drain, so worker isolation must be explicit.)
                cfg.DisableWorker();

                // These tests hand-seed delivery rows (Redeliver_SettledDelivery_RefreshesExpireAt seeds one
                // already past ExpireAt) and assert exact row/job state. Disable the maintenance tasks that
                // would otherwise delete or advance them: ExpirationCleanup (60s default — fires during a
                // full-suite CI run and swept the past-expiry seed row, so Redeliver returned NotFound) and
                // StaleJobRecovery (the webhook stuck-delivery sweep).
                cfg.ExpirationCleanupInterval = null;
                cfg.StaleJobRecoveryInterval = null;
            });
    }

    private static async Task<Guid> SeedDeliveryAsync(
        WarpTestServer server,
        WebhookDeliveryStatus status,
        int attemptCount,
        DateTime? expireAt = null)
    {
        var ctx = server.CreateContext();
        var delivery = NewDelivery(status, attemptCount, expireAt);

        ctx.Set<WebhookDelivery>().Add(delivery);
        await ctx.SaveChangesAsync(Ct);

        return delivery.Id;
    }

    private async Task<Guid> SeedDeliveryDirectAsync(WebhookDeliveryStatus status, int attemptCount)
    {
        var ctx = Fixture.CreateContext();
        var delivery = NewDelivery(status, attemptCount, expireAt: null);

        ctx.Set<WebhookDelivery>().Add(delivery);
        await ctx.SaveChangesAsync(Ct);

        return delivery.Id;
    }

    private static WebhookDelivery NewDelivery(WebhookDeliveryStatus status, int attemptCount, DateTime? expireAt)
        => new()
        {
            Id = Guid.NewGuid(),
            EventType = "order.created",
            EventId = Guid.NewGuid().ToString(),
            Url = "https://example.test/hook",
            PayloadJson = "{\"order\":42}",
            SigningMode = WebhookSigning.None,
            RetrySchedule = [TimeSpan.FromMinutes(1)],
            Status = status,
            AttemptCount = attemptCount,
            CreatedAt = DateTime.UtcNow,
            ExpireAt = expireAt,
        };

    private static async Task<WebhookRedeliveryResult> RedeliverAsync(WarpTestServer server, Guid deliveryId)
    {
        using var scope = server.GetService<IServiceScopeFactory>().CreateScope();
        var command = scope.ServiceProvider.GetRequiredService<IWebhookCommandService>();

        return await command.Redeliver(deliveryId, Ct);
    }

    private static async Task<WebhookRedeliveryResult> RedeliverInNewScopeAsync(IServiceScopeFactory scopeFactory, Guid deliveryId)
    {
        using var scope = scopeFactory.CreateScope();
        var command = scope.ServiceProvider.GetRequiredService<IWebhookCommandService>();

        return await command.Redeliver(deliveryId, Ct);
    }

    private static async Task<WebhookDelivery> GetDeliveryAsync(WarpTestServer server, Guid deliveryId)
    {
        return await server.CreateContext().Set<WebhookDelivery>()
            .AsNoTracking()
            .Where(x => x.Id == deliveryId)
            .FirstAsync(Ct);
    }

    private static async Task<int> WebhookJobCountAsync(WarpTestServer server)
    {
        return await server.CreateContext().Set<Job>()
            .CountAsync(x => x.Queue == "warp:webhooks", Ct);
    }
}

/// <summary>
/// No-op redelivery enqueuer: lets a direct <see cref="WebhookCommandService{TContext}"/> call exercise the
/// full Redeliver transaction (the settled→Pending flip plus the correlated attempt-row ExpireAt refresh)
/// without a running server/worker to stage a real executor job.
/// </summary>
internal sealed class NoOpRedeliveryEnqueuer : IWebhookRedeliveryEnqueuer
{
    public Task EnqueueAsync(Guid deliveryId, CancellationToken ct = default) => Task.CompletedTask;
}
