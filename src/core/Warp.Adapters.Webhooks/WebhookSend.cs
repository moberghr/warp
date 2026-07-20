using Warp.Core.Enums;

namespace Warp.Adapters.Webhooks;

/// <summary>
/// A single outbound webhook to deliver — the self-contained description handed to
/// <see cref="IWebhookDispatcher.SendAsync"/>. Everything needed to execute the delivery to
/// completion rides this request and is stamped onto the persisted delivery row, so a later config
/// deploy never reshapes an in-flight delivery. The host owns subscriptions, fan-out, and payload
/// serialization; Warp owns everything after <c>SendAsync</c>.
/// </summary>
public sealed class WebhookSend
{
    /// <summary>Absolute destination URL for the POST.</summary>
    public required string Url { get; init; }

    /// <summary>What happened (e.g. <c>order.created</c>) — forwarded as the adapter operation.</summary>
    public required string EventType { get; init; }

    /// <summary>
    /// Stable idempotency key for the consumer, constant across retries (the <c>webhook-id</c> header
    /// once signing lands). Defaults to a fresh GUID string when the host does not supply one.
    /// </summary>
    public string EventId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>The exact bytes to send, host-serialized. Opaque to Warp; stored once on the row.</summary>
    public string Payload { get; init; } = string.Empty;

    /// <summary>Per-delivery request headers. Redacted on every read surface (§1.2).</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Endpoint/tenant dimension — forwarded as the adapter group for the attempt rows.</summary>
    public string? Group { get; init; }

    /// <summary>The host's opaque link to its own subscription/definition (indexed for lookup).</summary>
    public string? Reference { get; init; }

    /// <summary>
    /// Ordered per-send retry delays. <c>null</c> uses the library default
    /// (<c>[1m, 10m, 1h, 6h]</c>); an empty list means a single attempt then exhaustion; N entries
    /// means N retries. Cadence is a property of what is being delivered — there is deliberately no
    /// app-level schedule setting to disagree with it.
    /// </summary>
    public IReadOnlyList<TimeSpan>? RetrySchedule { get; init; }

    /// <summary>HTTP status codes treated as success. <c>null</c> treats any 2xx as delivered.</summary>
    public IReadOnlyList<int>? SuccessCodes { get; init; }

    /// <summary>How the attempt is signed. Defaults to <see cref="WebhookSigning.None"/>.</summary>
    public WebhookSigning Signing { get; init; } = WebhookSigning.None;

    /// <summary>Signing secret carried on the row (self-containment); redacted on every read surface.</summary>
    public string? Secret { get; init; }
}
