using Warp.Core.Enums;

namespace Warp.Core.Data.Entities;

/// <summary>
/// One row per outbound webhook delivery — the only new table for the webhooks feature (attempts are
/// <see cref="AdapterCallLog"/> rows linked by <c>CorrelationId</c>, not a second table). The row is
/// self-contained: everything needed to execute the delivery to completion (URL, headers, payload, retry
/// schedule, success codes, signing mode + secret, group, reference) is stamped at <c>SendAsync</c> time,
/// so a config deploy never reshapes an in-flight delivery. Execution never mutates the schedule column —
/// <c>(RetrySchedule, AttemptCount)</c> fully determines the remaining plan. Secrets and
/// <c>Authorization</c>-class headers are stored at rest (self-containment) and redacted on every read
/// surface (§1.2). Delivery rows are operational history, not an audit trail — same lossy, retention-bounded
/// stance as <see cref="AdapterCallLog"/>.
/// </summary>
public class WebhookDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Opt-in provenance: the application that SENT this delivery (<c>WarpConfiguration.ApplicationName</c>). Null ⇒ feature off / legacy row.</summary>
    public string? Application { get; set; }

    /// <summary>What happened (e.g. <c>order.created</c>) — forwarded as the adapter operation.</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>Stable idempotency key across retries; the consumer's <c>webhook-id</c> header.</summary>
    public string EventId { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    /// <summary>Per-delivery request headers as a JSON object; redacted on all read surfaces.</summary>
    public string? HeadersJson { get; set; }

    /// <summary>Endpoint/tenant dimension — forwarded as the adapter group for the attempt rows.</summary>
    public string? GroupName { get; set; }

    /// <summary>The host's opaque link to its own subscription/definition (indexed).</summary>
    public string? Reference { get; set; }

    /// <summary>Exact bytes to send, host-serialized; stored once.</summary>
    public string PayloadJson { get; set; } = string.Empty;

    public WebhookSigning SigningMode { get; set; }

    /// <summary>Signing secret carried on the row (self-containment); redacted on every read surface.</summary>
    public string? Secret { get; set; }

    /// <summary>
    /// Ordered per-send retry delays (N entries = N retries; empty = single attempt). Persisted as a JSON
    /// seconds array text column via <c>RetryScheduleConverter</c>. Attempt N's failure schedules delay
    /// <c>RetrySchedule[N-1]</c>; the delivery is exhausted once <c>AttemptCount &gt; RetrySchedule.Count</c>.
    /// </summary>
    public IReadOnlyList<TimeSpan> RetrySchedule { get; set; } = [];

    /// <summary>HTTP status codes treated as success as a JSON array; null = any 2xx.</summary>
    public string? SuccessCodesJson { get; set; }

    public WebhookDeliveryStatus Status { get; set; }

    public int AttemptCount { get; set; }

    /// <summary>
    /// True between the <c>Exhausted</c> commit and the exhausted-handler callback completing. Makes the
    /// callback at-least-once across a process crash: the exhaustion transition commits this flag <c>true</c>,
    /// the executor invokes the host callback, then a second small commit clears it. A re-run of the executor
    /// for an already-<c>Exhausted</c> row with the flag still set re-invokes the (idempotent) callback and
    /// clears it, so a crash between the commit and the callback never silently drops the notification.
    /// Internal recovery state — not exposed on any read surface.
    /// </summary>
    public bool ExhaustedCallbackPending { get; set; }

    /// <summary>Display metadata for the next scheduled attempt; never a scan target (§2.8 rides the scheduler).</summary>
    public DateTime? NextAttemptAt { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Retention stamp (<c>CreatedAt + WebhookDeliveryRetention</c>); <c>ExpirationCleanup</c> deletes past it.</summary>
    public DateTime? ExpireAt { get; set; }
}
