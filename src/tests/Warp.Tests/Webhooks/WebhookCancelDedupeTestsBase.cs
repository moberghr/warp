using System.Net;
using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Webhooks;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Webhooks;

/// <summary>
/// Cancel (single delivery and per group), send-time dedupe on event id + URL, and the last-attempt fields (#357)
/// — all 8.1. Cancel is checked against a live executor job and against an attempt already in flight.
/// </summary>
[GenerateDatabaseTests]
public abstract class WebhookCancelDedupeTestsBase : IntegrationTestBase
{
    private const string Url = "https://example.test/hook";

    protected WebhookCancelDedupeTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task Cancel_PendingDeliveryWithALiveJob_CancelsAndTheJobSendsNothing()
    {
        var receiver = new SwitchableReceiver(HttpStatusCode.OK);
        await using var server = await StartServerAsync(receiver, worker: false);
        var id = await SendAsync(server, "evt-1");

        var result = await CommandAsync(server, x => x.Cancel(id, Ct));

        result.ShouldBe(WebhookCancelResult.Cancelled);

        // The executor job staged by SendAsync runs and finds the delivery no longer Pending.
        using (var scope = server.GetService<IServiceScopeFactory>().CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IJobHandler<ExecuteWebhookDelivery>>()
                .HandleAsync(new ExecuteWebhookDelivery { DeliveryId = id }, Ct);
        }

        receiver.CountFor("{}").ShouldBe(0);
        var row = await GetDeliveryAsync(id);
        row.Status.ShouldBe(WebhookDeliveryStatus.Cancelled);
        row.NextAttemptAt.ShouldBeNull();
    }

    [TimedFact]
    public async Task Cancel_AttemptInFlight_OutcomeIsNotRecordedAndNoRetryIsScheduled()
    {
        var signal = new BarrierSignal();
        await using var server = await StartServerAsync(new PinnedReceiver(signal, HttpStatusCode.InternalServerError), worker: true);
        var id = await SendAsync(server, "evt-1", retrySchedule: [TimeSpan.FromHours(1)]);

        (await signal.Running.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue();
        (await CommandAsync(server, x => x.Cancel(id, Ct))).ShouldBe(WebhookCancelResult.Cancelled);
        signal.CanFinish.Release();

        await WarpTestServer.WaitUntil(
            async () => await server.CreateContext().Set<Job>()
                .Where(x => x.Queue == "default")
                .Where(x => x.CurrentState == State.Completed)
                .AnyAsync(Ct),
            timeout: TimeSpan.FromSeconds(8),
            ct: Ct);

        var row = await GetDeliveryAsync(id);
        row.Status.ShouldBe(WebhookDeliveryStatus.Cancelled);
        row.LastAttemptAt.ShouldBeNull();
        (await server.CreateContext().Set<Job>()
            .Where(x => x.Queue == "default")
            .Where(x => x.CurrentState == State.Scheduled)
            .AnyAsync(Ct)).ShouldBeFalse();
    }

    [TimedTheory]
    [InlineData(WebhookDeliveryStatus.Parked, WebhookCancelResult.Cancelled)]
    [InlineData(WebhookDeliveryStatus.Delivered, WebhookCancelResult.Rejected)]
    [InlineData(WebhookDeliveryStatus.Exhausted, WebhookCancelResult.Rejected)]
    [InlineData(WebhookDeliveryStatus.Cancelled, WebhookCancelResult.Rejected)]
    public async Task Cancel_ByStatus_CancelsOnlyWaitingDeliveries(WebhookDeliveryStatus status, WebhookCancelResult expected)
    {
        var id = await SeedDeliveryAsync(status, group: "acme");

        var result = await CreateCommandService().Cancel(id, Ct);

        result.ShouldBe(expected);
        (await GetDeliveryAsync(id)).Status.ShouldBe(expected == WebhookCancelResult.Cancelled ? WebhookDeliveryStatus.Cancelled : status);
    }

    [TimedFact]
    public async Task Cancel_UnknownId_ReturnsNotFound()
    {
        var result = await CreateCommandService().Cancel(Guid.NewGuid(), Ct);

        result.ShouldBe(WebhookCancelResult.NotFound);
    }

    [TimedFact]
    public async Task CancelGroup_CancelsThatGroupsPendingAndParkedOnly()
    {
        var pending = await SeedDeliveryAsync(WebhookDeliveryStatus.Pending, group: "acme");
        var parked = await SeedDeliveryAsync(WebhookDeliveryStatus.Parked, group: "acme");
        var delivered = await SeedDeliveryAsync(WebhookDeliveryStatus.Delivered, group: "acme");
        var otherGroup = await SeedDeliveryAsync(WebhookDeliveryStatus.Parked, group: "globex");

        var cancelled = await CreateCommandService().CancelGroup("acme", Ct);

        cancelled.ShouldBe(2);
        (await GetDeliveryAsync(pending)).Status.ShouldBe(WebhookDeliveryStatus.Cancelled);
        (await GetDeliveryAsync(parked)).Status.ShouldBe(WebhookDeliveryStatus.Cancelled);
        (await GetDeliveryAsync(delivered)).Status.ShouldBe(WebhookDeliveryStatus.Delivered);
        (await GetDeliveryAsync(otherGroup)).Status.ShouldBe(WebhookDeliveryStatus.Parked);
    }

    [TimedFact]
    public async Task CancelGroup_UngroupedDeliveriesByTheirUrlKey_AreCancelled()
    {
        // The endpoint groups view keys a delivery without a group by its URL; "Cancel all" on that row must work.
        var ungrouped = await SeedDeliveryAsync(WebhookDeliveryStatus.Pending, group: null);

        var cancelled = await CreateCommandService().CancelGroup(Url, Ct);

        cancelled.ShouldBe(1);
        (await GetDeliveryAsync(ungrouped)).Status.ShouldBe(WebhookDeliveryStatus.Cancelled);
    }

    [TimedFact]
    public async Task Send_DedupeOnAndEventIdsDifferingPastTheColumnCap_AreTwoDeliveries()
    {
        await using var server = await StartServerAsync(new SwitchableReceiver(HttpStatusCode.OK), worker: false, dedupe: true);
        var prefix = new string('e', 200);

        var first = await SendAsync(server, prefix + "A");
        var second = await SendAsync(server, prefix + "B");

        second.ShouldNotBe(first);
    }

    [TimedFact]
    public async Task Send_DedupeOnAndSameEventIdAndUrl_ReturnsTheFirstDelivery()
    {
        await using var server = await StartServerAsync(new SwitchableReceiver(HttpStatusCode.OK), worker: false, dedupe: true);

        var first = await SendAsync(server, "evt-1");
        var second = await SendAsync(server, "evt-1");

        second.ShouldBe(first);
        (await Fixture.CreateContext().Set<WebhookDelivery>().CountAsync(Ct)).ShouldBe(1);
        (await Fixture.CreateContext().Set<Job>().CountAsync(x => x.Queue == "default", Ct)).ShouldBe(1);
        (await GetDeliveryAsync(first)).DedupeKey.ShouldNotBeNull().Length.ShouldBe(64);
    }

    [TimedFact]
    public async Task Send_DedupeOnAndAnySettledStatus_StillReturnsTheFirstDelivery()
    {
        await using var server = await StartServerAsync(new SwitchableReceiver(HttpStatusCode.OK), worker: false, dedupe: true);
        var first = await SendAsync(server, "evt-1");
        await Fixture.CreateContext().Set<WebhookDelivery>()
            .Where(x => x.Id == first)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, WebhookDeliveryStatus.Delivered), Ct);

        var second = await SendAsync(server, "evt-1");

        second.ShouldBe(first);
    }

    [TimedFact]
    public async Task Send_DedupeOnAndAnotherUrl_StoresASecondDelivery()
    {
        await using var server = await StartServerAsync(new SwitchableReceiver(HttpStatusCode.OK), worker: false, dedupe: true);

        var first = await SendAsync(server, "evt-1");
        var second = await SendAsync(server, "evt-1", url: "https://example.test/other");

        second.ShouldNotBe(first);
    }

    [TimedFact]
    public async Task Send_DedupeOff_StoresEverySendWithoutAKey()
    {
        await using var server = await StartServerAsync(new SwitchableReceiver(HttpStatusCode.OK), worker: false, dedupe: false);

        var first = await SendAsync(server, "evt-1");
        var second = await SendAsync(server, "evt-1");

        second.ShouldNotBe(first);
        (await GetDeliveryAsync(first)).DedupeKey.ShouldBeNull();
        (await GetDeliveryAsync(second)).DedupeKey.ShouldBeNull();
    }

    [TimedFact]
    public async Task Send_SuccessfulAttempt_StampsLastAttemptFieldsReadByTheQueryService()
    {
        await using var server = await StartServerAsync(new SwitchableReceiver(HttpStatusCode.Accepted), worker: true);
        var id = await SendAsync(server, "evt-1");
        await WaitForStatusAsync(server, id, WebhookDeliveryStatus.Delivered);

        var query = new WebhookQueryService<TestContext>(Fixture.CreateContext());
        var detail = (await query.GetDeliveryDetail(id, Ct)).ShouldNotBeNull();
        var item = (await query.GetDeliveries(new WebhookDeliveryFilter(), Ct)).Items.ShouldHaveSingleItem();

        detail.LastStatusCode.ShouldBe(202);
        detail.LastFailureReason.ShouldBeNull();
        detail.LastAttemptAt.ShouldNotBeNull();
        item.LastStatusCode.ShouldBe(202);
        item.LastAttemptAt.ShouldBe(detail.LastAttemptAt);
    }

    [TimedFact]
    public async Task Send_FailedAttempt_StampsStatusAndReason()
    {
        await using var server = await StartServerAsync(new SwitchableReceiver(HttpStatusCode.ServiceUnavailable), worker: true);
        var id = await SendAsync(server, "evt-1");
        await WaitForStatusAsync(server, id, WebhookDeliveryStatus.Exhausted);

        var row = await GetDeliveryAsync(id);

        row.LastStatusCode.ShouldBe(503);
        row.LastFailureReason.ShouldBe(WebhookFailureReason.UnsuccessfulStatus);
        row.LastAttemptAt.ShouldNotBeNull();
    }

    [TimedFact]
    public async Task Redeliver_LeavesTheLastAttemptFieldsUntilTheNextAttempt()
    {
        var attemptedAt = DateTime.UtcNow.AddMinutes(-5);
        var id = await SeedDeliveryAsync(WebhookDeliveryStatus.Exhausted, group: null);
        await Fixture.CreateContext().Set<WebhookDelivery>()
            .Where(x => x.Id == id)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.LastStatusCode, 500)
                    .SetProperty(x => x.LastFailureReason, WebhookFailureReason.UnsuccessfulStatus)
                    .SetProperty(x => x.LastAttemptAt, attemptedAt),
                Ct);

        (await CreateCommandService().Redeliver(id, Ct)).ShouldBe(WebhookRedeliveryResult.Enqueued);

        var row = await GetDeliveryAsync(id);
        row.Status.ShouldBe(WebhookDeliveryStatus.Pending);
        row.LastStatusCode.ShouldBe(500);
        row.LastFailureReason.ShouldBe(WebhookFailureReason.UnsuccessfulStatus);
        row.LastAttemptAt.ShouldNotBeNull().ShouldBe(attemptedAt, TimeSpan.FromMilliseconds(1));
    }

    private WebhookCommandService<TestContext> CreateCommandService()
        => new(Fixture.CreateContext(), TimeProvider.System, Options.Create(new WarpConfiguration()), [new NoOpRedeliveryEnqueuer()]);

    private Task<WarpTestServer> StartServerAsync(HttpMessageHandler receiver, bool worker, bool dedupe = false)
    {
        return WarpTestServer.StartAsync(
            Fixture,
            configure: cfg =>
            {
                cfg.Queues = ["default"];
                cfg.AddWebhooks(w => w.DeduplicateByEventId = dedupe);
                cfg.StaleJobRecoveryInterval = null;
                cfg.ExpirationCleanupInterval = null;
                if (!worker)
                {
                    cfg.DisableWorker();
                }
            },
            configureServices: services =>
                services.AddHttpClient(WebhookConstants.AdapterName)
                    .ConfigurePrimaryHttpMessageHandler(() => receiver));
    }

    private static async Task<T> CommandAsync<T>(WarpTestServer server, Func<IWebhookCommandService, Task<T>> action)
    {
        using var scope = server.GetService<IServiceScopeFactory>().CreateScope();

        return await action(scope.ServiceProvider.GetRequiredService<IWebhookCommandService>());
    }

    private static async Task<Guid> SendAsync(WarpTestServer server, string eventId, IReadOnlyList<TimeSpan>? retrySchedule = null, string url = Url)
    {
        using var scope = server.GetService<IServiceScopeFactory>().CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IWebhookDispatcher>().SendAsync(
            new WebhookSend
            {
                Url = url,
                EventType = "order.created",
                EventId = eventId,
                RetrySchedule = retrySchedule ?? [],
            },
            Ct);
    }

    private async Task<Guid> SeedDeliveryAsync(WebhookDeliveryStatus status, string? group)
    {
        var delivery = new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            EventType = "order.created",
            EventId = Guid.NewGuid().ToString(),
            Url = Url,
            GroupName = group,
            PayloadJson = "{}",
            SigningMode = WebhookSigning.None,
            RetrySchedule = [],
            Status = status,
            AttemptCount = 1,
            CreatedAt = DateTime.UtcNow,
        };

        var ctx = Fixture.CreateContext();
        ctx.Set<WebhookDelivery>().Add(delivery);
        await ctx.SaveChangesAsync(Ct);

        return delivery.Id;
    }

    private async Task<WebhookDelivery> GetDeliveryAsync(Guid id)
        => await Fixture.CreateContext().Set<WebhookDelivery>()
            .AsNoTracking()
            .Where(x => x.Id == id)
            .FirstAsync(Ct);

    private static Task WaitForStatusAsync(WarpTestServer server, Guid id, WebhookDeliveryStatus status)
        => WarpTestServer.WaitUntil(
            async () => await server.CreateContext().Set<WebhookDelivery>()
                .Where(x => x.Id == id)
                .Select(x => (WebhookDeliveryStatus?)x.Status)
                .FirstOrDefaultAsync(Ct) == status,
            timeout: TimeSpan.FromSeconds(8),
            ct: Ct);
}

/// <summary>Holds the request inside the receiver until the test releases it, so an attempt can be caught in flight.</summary>
internal sealed class PinnedReceiver : HttpMessageHandler
{
    private readonly BarrierSignal _signal;
    private readonly HttpStatusCode _status;

    public PinnedReceiver(BarrierSignal signal, HttpStatusCode status)
    {
        _signal = signal;
        _status = status;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _signal.Running.Release();
        await _signal.CanFinish.WaitAsync(cancellationToken);

        return new HttpResponseMessage(_status) { Content = new StringContent("ok") };
    }
}
