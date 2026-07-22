namespace Warp.Core.Webhooks;

/// <summary>
/// Computes the signing headers added to a webhook attempt. Warp ships
/// <see cref="StandardWebhooksSigner"/> for <c>WebhookSigning.StandardWebhooks</c>; hosts that use a
/// different scheme register their own implementation via
/// <c>AddWebhooks(w =&gt; w.UseCustomSigner&lt;T&gt;())</c>, selected per send by
/// <c>WebhookSigning.Custom</c>. Implementations are pure — they return the headers to add, never mutate
/// ambient state — and are resolved once per attempt inside the executor's HTTP leg. A missing
/// <see cref="IWebhookSigner"/> for a declared custom-signing host fails at <c>AddWebhooks</c> time, not
/// at send time.
/// </summary>
public interface IWebhookSigner
{
    /// <summary>
    /// Returns the headers to add to the outgoing request for the given attempt. The
    /// <see cref="WebhookSignatureRequest.WebhookId"/> is stable across retries so the header set is a
    /// consumer idempotency key; the <see cref="WebhookSignatureRequest.Timestamp"/> reflects the current
    /// attempt.
    /// </summary>
    IReadOnlyDictionary<string, string> Sign(WebhookSignatureRequest request);
}

/// <summary>
/// The self-contained input to <see cref="IWebhookSigner.Sign"/> for one attempt. Everything the signer
/// needs rides the delivery row; nothing is looked up from ambient config mid-flight.
/// </summary>
public sealed record WebhookSignatureRequest
{
    /// <summary>Stable idempotency key (= the delivery's <c>EventId</c>) — the <c>webhook-id</c> value.</summary>
    public required string WebhookId { get; init; }

    /// <summary>The current attempt's timestamp (<c>webhook-timestamp</c> is its unix-seconds form).</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>The exact payload bytes being signed.</summary>
    public required string Payload { get; init; }

    /// <summary>The per-delivery signing secret carried on the row (<c>whsec_…</c> for Standard Webhooks).</summary>
    public string? Secret { get; init; }
}
