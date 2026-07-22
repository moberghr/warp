using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Warp.Core.Enums;
using Warp.Core.Webhooks;

namespace Warp.Test.Shared.Shop;

/// <summary>
/// Builds the durable webhooks the shop sends to external subscribers (order.paid / order.shipped),
/// signed with Standard Webhooks. The subscriber endpoint (reliable / flaky / down) becomes the adapter
/// group so the Webhooks dashboard shows per-subscriber delivery health.
/// </summary>
public static class ShopWebhooks
{
    // The published Standard Webhooks test-vector secret — a well-known public value, not a real secret.
    // The subscriber verifies against the same value; override both via Webhooks:Secret.
#pragma warning disable S6418
    private const string DefaultSecret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";
#pragma warning restore S6418

    public static string SubscriberBaseUrl(IConfiguration configuration)
        => configuration["Subscriber:BaseUrl"]?.TrimEnd('/')
        ?? configuration["PartnerApi:BaseUrl"]?.TrimEnd('/')
        ?? "http://localhost:5230";

    public static WebhookSend Build(IConfiguration configuration, string eventType, string subscriber, string orderId)
    {
        var path = subscriber switch
        {
            ShopProviders.FlakySubscriber => "/subscriber/webhooks/flaky",
            ShopProviders.DownSubscriber => "/subscriber/webhooks/down",
            _ => "/subscriber/webhooks",
        };

        var payload = JsonSerializer.Serialize(new { eventType, orderId, occurredAt = DateTimeOffset.UtcNow });
        var reliable = string.Equals(subscriber, ShopProviders.ReliableSubscriber, StringComparison.Ordinal);

        return new WebhookSend
        {
            Url = $"{SubscriberBaseUrl(configuration)}{path}",
            EventType = eventType,
            Payload = payload,
            Group = subscriber,
            Reference = orderId,
            Signing = WebhookSigning.StandardWebhooks,
            Secret = configuration["Webhooks:Secret"] ?? DefaultSecret,
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["webhook-event-type"] = eventType,
            },

            // A tight schedule so retries + exhaustion are watchable within a short window, instead of the
            // built-in [1m, 10m, 1h, 6h]. Reliable delivers first try, so its schedule is irrelevant.
            RetrySchedule = reliable ? [] : [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)],
        };
    }
}

/// <summary>
/// The shop's exhausted-delivery callback (§8.20: Warp signals, the host decides). Logs the dead-lettered
/// notification — a real shop would flag the subscriber or alert. Registered via
/// <c>AddWebhooks(w =&gt; w.OnDeliveryExhausted&lt;OrderWebhookExhaustedHandler&gt;())</c>; idempotent
/// (at-least-once), so keying any side effect on the delivery id is safe.
/// </summary>
public sealed class OrderWebhookExhaustedHandler : IWebhookDeliveryExhaustedHandler
{
    private readonly ILogger<OrderWebhookExhaustedHandler> _logger;

    public OrderWebhookExhaustedHandler(ILogger<OrderWebhookExhaustedHandler> logger)
    {
        _logger = logger;
    }

    public Task OnDeliveryExhaustedAsync(WebhookDeliveryExhausted delivery, CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "Order webhook {DeliveryId} ({EventType}) to subscriber '{Subscriber}' exhausted after {Attempts} attempts (order {OrderRef}).",
            delivery.DeliveryId,
            delivery.EventType,
            delivery.GroupName,
            delivery.AttemptCount,
            delivery.Reference);

        return Task.CompletedTask;
    }
}
