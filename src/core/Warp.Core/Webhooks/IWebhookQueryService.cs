using Warp.Core.Enums;

namespace Warp.Core.Webhooks;

/// <summary>
/// Read-only dashboard queries for the Webhooks feature. Reads on the user's <c>TContext</c> (§2.14
/// stays-on-<c>TContext</c>) so dashboard-only / publisher-only processes that call <c>AddWarp</c>
/// without <c>AddWebhooks()</c> can still serve the webhook endpoints — the <c>WebhookDelivery</c> table
/// is always in the schema (§2.11). All implementations use <c>AsNoTracking()</c> (§5.3) and
/// <b>always redact</b> the per-delivery <c>Secret</c> and <c>Authorization</c>-class headers before any
/// value leaves the service (§1.2) — redaction is not an option a caller can turn off. The per-attempt
/// timeline is not returned here; it is assembled from <c>AdapterCallLog</c> rows by <c>CorrelationId</c>.
/// </summary>
public interface IWebhookQueryService
{
    /// <summary>Filtered, newest-first, paged deliveries for the list page.</summary>
    Task<PagedList<WebhookDeliveryListItem>> GetDeliveries(WebhookDeliveryFilter filter, CancellationToken ct = default);

    /// <summary>
    /// Delivery counts grouped by event type or by endpoint (destination), each with a per-status
    /// breakdown, for the "group webhooks" summary tables. Newest-activity first, then by key.
    /// </summary>
    Task<IReadOnlyList<WebhookGroupModel>> GetGroups(WebhookGroupBy by, CancellationToken ct = default);

    /// <summary>
    /// Hourly delivery-statistics time-series (created-per-hour, split by current status), oldest first,
    /// for the delivery-stats chart. Honours the <see cref="WebhookDeliveryFilter"/>'s event-type / endpoint
    /// / status / date scope (an empty filter = global). Aggregated DB-side off the durable
    /// <c>WebhookDelivery</c> rows (deliveries are not lossy), bounded by the delivery retention.
    /// </summary>
    Task<IReadOnlyList<WebhookDeliveryHistoryPoint>> GetDeliveryHistory(WebhookDeliveryFilter filter, CancellationToken ct = default);

    /// <summary>
    /// One delivery's self-contained contract (URL, redacted headers, payload, schedule, success codes,
    /// signing mode, status) for the detail page. Returns null when no delivery matches the id.
    /// </summary>
    Task<WebhookDeliveryDetail?> GetDeliveryDetail(Guid id, CancellationToken ct = default);

    /// <summary>Per-status delivery counts for the summary tiles.</summary>
    Task<WebhookDeliverySummary> GetSummary(CancellationToken ct = default);
}

/// <summary>Filter for <see cref="IWebhookQueryService.GetDeliveries"/>; null / empty members are ignored.</summary>
public sealed class WebhookDeliveryFilter
{
    public WebhookDeliveryStatus? Status { get; set; }

    public string? EventType { get; set; }

    public string? Reference { get; set; }

    public string? GroupName { get; set; }

    /// <summary>Inclusive lower bound on <c>CreatedAt</c>.</summary>
    public DateTime? Since { get; set; }

    /// <summary>Inclusive upper bound on <c>CreatedAt</c>.</summary>
    public DateTime? Until { get; set; }

    /// <summary>Zero-based page index.</summary>
    public int Page { get; set; }

    /// <summary>Rows per page; clamped to the service's page cap.</summary>
    public int PageSize { get; set; } = 20;
}

/// <summary>The dimension to group webhook deliveries by (§8.11 enums-from-1).</summary>
public enum WebhookGroupBy
{
    /// <summary>Group by the delivery's event type.</summary>
    EventType = 1,

    /// <summary>Group by the delivery's endpoint (destination — <c>GroupName</c>, falling back to <c>Url</c>).</summary>
    Endpoint = 2,
}

/// <summary>One row of the "group webhooks" summary — a key with its per-status delivery counts.</summary>
public sealed class WebhookGroupModel
{
    /// <summary>The event type or endpoint this row aggregates.</summary>
    public string Key { get; set; } = string.Empty;

    public int Total { get; set; }

    public int Pending { get; set; }

    public int Delivered { get; set; }

    public int Exhausted { get; set; }

    /// <summary>Deliveries waiting for their endpoint to recover (8.1).</summary>
    public int Parked { get; set; }

    /// <summary>Deliveries an operator cancelled (8.1).</summary>
    public int Cancelled { get; set; }

    /// <summary>Most recent <c>CreatedAt</c> in the group — drives the newest-activity-first ordering.</summary>
    public DateTime LastActivityAt { get; set; }

    /// <summary>
    /// Endpoint health (8.1), only when grouping by endpoint and the group has a health record (a tracked delivery to
    /// it ran out of retries at least once); null otherwise.
    /// </summary>
    public WebhookEndpointState? Health { get; set; }

    /// <summary>When the endpoint's current failing period began; null while healthy or untracked.</summary>
    public DateTime? FailingSince { get; set; }

    /// <summary>When a probe of the failing endpoint is next due; null while healthy or untracked.</summary>
    public DateTime? NextProbeAt { get; set; }

    /// <summary>
    /// The newest successful delivery in the group (endpoint grouping only): the later of the health record's
    /// <c>LastSuccessAt</c> and the newest <c>Delivered</c> row's <c>LastAttemptAt</c>.
    /// </summary>
    public DateTime? LastSuccessAt { get; set; }

    /// <summary>The newest failed attempt in the group (endpoint grouping only), same sources as <see cref="LastSuccessAt"/>.</summary>
    public DateTime? LastFailureAt { get; set; }

    /// <summary>
    /// Share of the deliveries that settled in the last 24 hours (by <c>LastAttemptAt</c>) that were delivered, out of
    /// delivered + exhausted + parked; null without any (endpoint grouping only).
    /// </summary>
    public double? SuccessRate { get; set; }

    /// <summary>
    /// 95th percentile of send-to-delivered time, in milliseconds, over the deliveries delivered in the last 24 hours;
    /// null without any (endpoint grouping only). Includes retries and redeliveries: it is the delay a subscriber saw.
    /// </summary>
    public double? P95DeliveryMs { get; set; }
}

/// <summary>One hourly point of the delivery-statistics time-series (deliveries created that hour, by status).</summary>
public sealed class WebhookDeliveryHistoryPoint
{
    /// <summary>Start of the UTC hour this point covers.</summary>
    public DateTime Hour { get; set; }

    public int Delivered { get; set; }

    public int Exhausted { get; set; }

    public int Pending { get; set; }

    public int Parked { get; set; }

    public int Cancelled { get; set; }

    public int Total { get; set; }
}

/// <summary>One row on the deliveries list page (no payload, headers, or secret).</summary>
public sealed class WebhookDeliveryListItem
{
    public Guid Id { get; set; }

    public string EventType { get; set; } = string.Empty;

    public string EventId { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string? GroupName { get; set; }

    public string? Reference { get; set; }

    public WebhookDeliveryStatus Status { get; set; }

    public WebhookSigning SigningMode { get; set; }

    public int AttemptCount { get; set; }

    public DateTime? NextAttemptAt { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>HTTP status of the last attempt; null without a response or before the first attempt (#357).</summary>
    public int? LastStatusCode { get; set; }

    /// <summary>Why the last attempt failed; null after a success or before the first attempt (#357).</summary>
    public WebhookFailureReason? LastFailureReason { get; set; }

    /// <summary>When the last attempt finished (#357).</summary>
    public DateTime? LastAttemptAt { get; set; }
}

/// <summary>The delivery detail page payload — the self-contained contract, with secret + headers redacted.</summary>
public sealed class WebhookDeliveryDetail
{
    public Guid Id { get; set; }

    public string EventType { get; set; } = string.Empty;

    public string EventId { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    /// <summary>Per-delivery headers as a JSON object with <c>Authorization</c>-class values redacted to <c>***</c>.</summary>
    public string? HeadersJson { get; set; }

    public string? GroupName { get; set; }

    public string? Reference { get; set; }

    public string PayloadJson { get; set; } = string.Empty;

    public WebhookSigning SigningMode { get; set; }

    /// <summary>Whether a signing secret is stored; the secret value itself never leaves the service.</summary>
    public bool HasSecret { get; set; }

    /// <summary>The retry delays in seconds (empty = single attempt).</summary>
    public IReadOnlyList<double> RetryScheduleSeconds { get; set; } = [];

    public string? SuccessCodesJson { get; set; }

    public WebhookDeliveryStatus Status { get; set; }

    public int AttemptCount { get; set; }

    public DateTime? NextAttemptAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? ExpireAt { get; set; }

    /// <summary>HTTP status of the last attempt; null without a response or before the first attempt (#357).</summary>
    public int? LastStatusCode { get; set; }

    /// <summary>Why the last attempt failed; null after a success or before the first attempt (#357).</summary>
    public WebhookFailureReason? LastFailureReason { get; set; }

    /// <summary>When the last attempt finished (#357).</summary>
    public DateTime? LastAttemptAt { get; set; }

    /// <summary>Whether the delivery was sent with endpoint health tracking (8.1).</summary>
    public bool TrackEndpointHealth { get; set; }

    /// <summary>
    /// The per-attempt timeline, oldest first. Assembled from the <c>AdapterCallLog</c> rows whose
    /// <c>CorrelationId</c> is this delivery's id (attempts are adapter calls, not a second table); empty
    /// until the executor has made at least one attempt.
    /// </summary>
    public IReadOnlyList<WebhookAttemptItem> Attempts { get; set; } = [];
}

/// <summary>One attempt in a delivery's timeline — projected from the delivery's <c>AdapterCallLog</c> rows.</summary>
public sealed class WebhookAttemptItem
{
    /// <summary>The backing <c>AdapterCallLog</c> row id (for a drill-through to the full captured call).</summary>
    public Guid CallId { get; set; }

    public DateTime Timestamp { get; set; }

    public double DurationMs { get; set; }

    public AdapterCallOutcome Outcome { get; set; }

    public int? StatusCode { get; set; }

    public string? ExceptionType { get; set; }
}

/// <summary>Summary tile counts for the webhooks dashboard.</summary>
public sealed class WebhookDeliverySummary
{
    public int Total { get; set; }

    public int Pending { get; set; }

    public int Delivered { get; set; }

    public int Exhausted { get; set; }

    public int Parked { get; set; }

    public int Cancelled { get; set; }
}
