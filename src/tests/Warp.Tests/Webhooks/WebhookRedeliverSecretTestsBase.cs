using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Webhooks;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Webhooks;

/// <summary>
/// Redeliver with a rotated secret (8.1). A delivery stamps its secret at send, so a plain Redeliver after
/// a key rotation signs with the old key. <see cref="WebhookRedeliveryOptions.Secret"/> replaces it in the
/// same guarded settled→Pending flip; the redelivered attempt runs on the real worker and the signature that
/// reaches the endpoint is checked against the expected key.
/// </summary>
[GenerateDatabaseTests]
public abstract class WebhookRedeliverSecretTestsBase : IntegrationTestBase
{
    // Two valid Standard Webhooks secrets (base64 after the whsec_ prefix).
    private const string OldSecret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";
    private const string NewSecret = "whsec_bmV3LXJvdGF0ZWQtc2lnbmluZy1rZXktMDAx";

    protected WebhookRedeliverSecretTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact]
    public async Task Redeliver_WithNewSecret_NextAttemptIsSignedWithTheNewSecret()
    {
        var recorder = new WebhookRequestRecorder();
        await using var server = await StartServerAsync(recorder);
        var deliveryId = await SeedSettledDeliveryAsync(server, WebhookSigning.StandardWebhooks, OldSecret);

        var result = await RedeliverAsync(server, deliveryId, new WebhookRedeliveryOptions { Secret = NewSecret });

        result.ShouldBe(WebhookRedeliveryResult.Enqueued);
        await WaitForDeliveredAsync(server, deliveryId);

        var headers = recorder.LastHeaders().ShouldNotBeNull();
        headers["webhook-signature"].ShouldBe(ExpectedSignature(headers, NewSecret));
        headers["webhook-signature"].ShouldNotBe(ExpectedSignature(headers, OldSecret));
    }

    [TimedFact]
    public async Task Redeliver_WithoutNewSecret_NextAttemptKeepsTheStampedSecret()
    {
        var recorder = new WebhookRequestRecorder();
        await using var server = await StartServerAsync(recorder);
        var deliveryId = await SeedSettledDeliveryAsync(server, WebhookSigning.StandardWebhooks, OldSecret);

        var result = await RedeliverAsync(server, deliveryId, new WebhookRedeliveryOptions());

        result.ShouldBe(WebhookRedeliveryResult.Enqueued);
        await WaitForDeliveredAsync(server, deliveryId);

        var headers = recorder.LastHeaders().ShouldNotBeNull();
        headers["webhook-signature"].ShouldBe(ExpectedSignature(headers, OldSecret));
    }

    [TimedFact]
    public async Task Redeliver_PendingDeliveryWithNewSecret_RejectedAndSecretUnchanged()
    {
        // The secret rides the guarded flip: a rejected redeliver must not rotate the key of a delivery whose
        // live executor job is still signing with the old one.
        var recorder = new WebhookRequestRecorder();
        await using var server = await StartServerAsync(recorder);
        var deliveryId = await SeedSettledDeliveryAsync(server, WebhookSigning.StandardWebhooks, OldSecret, WebhookDeliveryStatus.Pending);

        var result = await RedeliverAsync(server, deliveryId, new WebhookRedeliveryOptions { Secret = NewSecret });

        result.ShouldBe(WebhookRedeliveryResult.Rejected);
        (await ReadSecretAsync(server, deliveryId)).ShouldBe(OldSecret);
    }

    [TimedFact]
    public async Task Redeliver_StandardWebhooksWithNonBase64Secret_ThrowsAndLeavesRowSettled()
    {
        var recorder = new WebhookRequestRecorder();
        await using var server = await StartServerAsync(recorder);
        var deliveryId = await SeedSettledDeliveryAsync(server, WebhookSigning.StandardWebhooks, OldSecret);

        await Should.ThrowAsync<ArgumentException>(() =>
            RedeliverAsync(server, deliveryId, new WebhookRedeliveryOptions { Secret = "whsec_not base64!" }));

        var row = await server.CreateContext().Set<WebhookDelivery>()
            .AsNoTracking()
            .Where(x => x.Id == deliveryId)
            .Select(x =>
                new
                {
                    x.Status,
                    x.Secret,
                })
            .FirstAsync(Ct);

        row.Status.ShouldBe(WebhookDeliveryStatus.Exhausted);
        row.Secret.ShouldBe(OldSecret);
    }

    [TimedFact]
    public async Task Redeliver_UnsignedDeliveryWithNewSecret_Throws()
    {
        var recorder = new WebhookRequestRecorder();
        await using var server = await StartServerAsync(recorder);
        var deliveryId = await SeedSettledDeliveryAsync(server, WebhookSigning.None, secret: null);

        await Should.ThrowAsync<ArgumentException>(() =>
            RedeliverAsync(server, deliveryId, new WebhookRedeliveryOptions { Secret = NewSecret }));

        (await ReadSecretAsync(server, deliveryId)).ShouldBeNull();
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static string ExpectedSignature(IReadOnlyDictionary<string, string> headers, string secret)
    {
        var signed = new StandardWebhooksSigner().Sign(new WebhookSignatureRequest
        {
            WebhookId = headers["webhook-id"],
            Timestamp = DateTimeOffset.FromUnixTimeSeconds(long.Parse(headers["webhook-timestamp"], CultureInfo.InvariantCulture)),
            Payload = "{\"order\":42}",
            Secret = secret,
        });

        return signed["webhook-signature"];
    }

    private Task<WarpTestServer> StartServerAsync(WebhookRequestRecorder recorder)
    {
        return WarpTestServer.StartAsync(
            Fixture,
            configure: cfg =>
            {
                cfg.Queues = ["default"];
                cfg.AddWebhooks();

                // Seeded rows are settled with no executor job; keep the sweeps away from them.
                cfg.ExpirationCleanupInterval = null;
                cfg.StaleJobRecoveryInterval = null;
            },
            configureServices: services =>
                services.AddHttpClient(WebhookConstants.AdapterName)
                    .ConfigurePrimaryHttpMessageHandler(() => new CapturingWebhookHandler(HttpStatusCode.OK, recorder)));
    }

    private static async Task<Guid> SeedSettledDeliveryAsync(
        WarpTestServer server,
        WebhookSigning signing,
        string? secret,
        WebhookDeliveryStatus status = WebhookDeliveryStatus.Exhausted)
    {
        var delivery = new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            EventType = "order.created",
            EventId = "evt_" + Guid.NewGuid().ToString("N"),
            Url = "https://example.test/hook",
            PayloadJson = "{\"order\":42}",
            SigningMode = signing,
            Secret = secret,
            RetrySchedule = [],
            Status = status,
            AttemptCount = 1,
            CreatedAt = DateTime.UtcNow,
        };

        var ctx = server.CreateContext();
        ctx.Set<WebhookDelivery>().Add(delivery);
        await ctx.SaveChangesAsync(Ct);

        return delivery.Id;
    }

    private static async Task<WebhookRedeliveryResult> RedeliverAsync(WarpTestServer server, Guid deliveryId, WebhookRedeliveryOptions options)
    {
        using var scope = server.GetService<IServiceScopeFactory>().CreateScope();
        var command = scope.ServiceProvider.GetRequiredService<IWebhookCommandService>();

        return await command.Redeliver(deliveryId, options, Ct);
    }

    private static async Task<string?> ReadSecretAsync(WarpTestServer server, Guid deliveryId)
    {
        return await server.CreateContext().Set<WebhookDelivery>()
            .AsNoTracking()
            .Where(x => x.Id == deliveryId)
            .Select(x => x.Secret)
            .FirstAsync(Ct);
    }

    private static Task WaitForDeliveredAsync(WarpTestServer server, Guid id)
    {
        return WarpTestServer.WaitUntil(
            async () => await server.CreateContext().Set<WebhookDelivery>()
                .Where(x => x.Id == id)
                .Select(x => (WebhookDeliveryStatus?)x.Status)
                .FirstOrDefaultAsync(Ct) == WebhookDeliveryStatus.Delivered,
            timeout: TimeSpan.FromSeconds(8),
            ct: Ct);
    }
}
