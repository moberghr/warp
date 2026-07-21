using Warp.Core.Enums;

namespace Warp.Core.Services;

/// <summary>
/// Read-only dashboard queries for the Adapters feature (outbound service-call observability).
/// Reads on the user's <c>TContext</c> (§2.14 stays-on-<c>TContext</c> rule) so dashboard-only /
/// publisher-only processes that call <c>AddWarp</c> without <c>AddAdapters()</c> can still serve the
/// adapter endpoints. All implementations use <c>AsNoTracking()</c> + <c>.Select()</c> projections
/// (§5.3, §6.4) with no <c>_context.Set&lt;&gt;()</c> subqueries inside projections (§5.2). Call
/// counts / error rates come from the write-optimised <c>Counter</c> → <c>Statistic</c> rows (§6.2)
/// so successes are always counted (real denominators even under <c>RecordCalls = FailuresOnly</c>);
/// average latency and per-group last-failure timestamps come from the retained <c>AdapterCallLog</c>
/// rows.
/// </summary>
public interface IAdapterQueryService
{
    /// <summary>Registered adapters with per-adapter call/error/latency stats for the list page.</summary>
    Task<IReadOnlyList<AdapterListItemModel>> GetAdapters(CancellationToken ct = default);

    /// <summary>
    /// One adapter's detail: per-operation and per-group stat tables, the recent-calls list, and the
    /// shared-policy conflict flag. Returns null when no <c>AdapterDefinition</c> exists for the name.
    /// </summary>
    Task<AdapterDetailModel?> GetAdapterDetail(string name, CancellationToken ct = default);

    /// <summary>
    /// A single call-log row including any captured (redacted, truncated) request/response payloads.
    /// Returns null when no row matches the adapter/id pair.
    /// </summary>
    Task<AdapterCallDetailModel?> GetCallDetail(string name, Guid callId, CancellationToken ct = default);
}

/// <summary>One row on the adapters list page.</summary>
public sealed class AdapterListItemModel
{
    public string Name { get; set; } = string.Empty;

    public string? ConfigSummary { get; set; }

    public DateTime FirstSeenAt { get; set; }

    public DateTime LastSeenAt { get; set; }

    public long TotalCalls { get; set; }

    public long ErrorCount { get; set; }

    /// <summary>Errors ÷ total calls, in the range 0–1; 0 when no calls have been recorded.</summary>
    public double ErrorRate { get; set; }

    /// <summary>Average call latency over the last 24 hours (rolling), not all-time; 0 when no recent calls.</summary>
    public double AvgDurationMs { get; set; }

    public bool HasPolicyConflict { get; set; }
}

/// <summary>The adapter detail page payload.</summary>
public sealed class AdapterDetailModel
{
    public string Name { get; set; } = string.Empty;

    public string? ConfigSummary { get; set; }

    public DateTime FirstSeenAt { get; set; }

    public DateTime LastSeenAt { get; set; }

    public bool HasPolicyConflict { get; set; }

    /// <summary>Display label for the group dimension (e.g. "Endpoint", "Shop"); "Group" by default.</summary>
    public string GroupLabel { get; set; } = "Group";

    public long TotalCalls { get; set; }

    public long ErrorCount { get; set; }

    public double ErrorRate { get; set; }

    public double AvgDurationMs { get; set; }

    /// <summary>90th-percentile call latency (ms), derived from the durable latency histogram; 0 when no data.</summary>
    public double P90DurationMs { get; set; }

    /// <summary>95th-percentile call latency (ms), derived from the durable latency histogram; 0 when no data.</summary>
    public double P95DurationMs { get; set; }

    /// <summary>99th-percentile call latency (ms), derived from the durable latency histogram; 0 when no data.</summary>
    public double P99DurationMs { get; set; }

    public List<AdapterOperationStatModel> Operations { get; set; } = [];

    public List<AdapterGroupStatModel> Groups { get; set; } = [];

    public List<AdapterCallSummaryModel> RecentCalls { get; set; } = [];

    /// <summary>
    /// Hourly performance time-series (call volume, error rate, average latency), oldest first, from the
    /// durable hourly <c>Counter</c>/<c>Statistic</c> buckets — exact-over-all-calls, unaffected by
    /// FailuresOnly/sampling, and surviving <c>AdapterCallLog</c> deletion. Bounded by the 7-day retention.
    /// </summary>
    public List<AdapterHistoryPointModel> History { get; set; } = [];
}

/// <summary>One hourly point of an adapter's performance time-series.</summary>
public sealed class AdapterHistoryPointModel
{
    /// <summary>Start of the UTC hour this point covers.</summary>
    public DateTime Hour { get; set; }

    public long Calls { get; set; }

    public long Errors { get; set; }

    public double ErrorRate { get; set; }

    public double AvgDurationMs { get; set; }
}

/// <summary>Per-operation row of the detail page operations table.</summary>
public sealed class AdapterOperationStatModel
{
    public string Operation { get; set; } = string.Empty;

    public long Calls { get; set; }

    public long Errors { get; set; }

    public double ErrorRate { get; set; }

    public double AvgDurationMs { get; set; }
}

/// <summary>Per-group row of the detail page groups table (shown only when the adapter carries groups).</summary>
public sealed class AdapterGroupStatModel
{
    public string Group { get; set; } = string.Empty;

    public long Calls { get; set; }

    public long Errors { get; set; }

    public double ErrorRate { get; set; }

    public double AvgDurationMs { get; set; }

    public DateTime? LastFailureAt { get; set; }
}

/// <summary>One entry in the detail page recent-calls list (no captured payload bodies/headers).</summary>
public sealed class AdapterCallSummaryModel
{
    public Guid Id { get; set; }

    public string Operation { get; set; } = string.Empty;

    public string? GroupName { get; set; }

    public DateTime Timestamp { get; set; }

    public double DurationMs { get; set; }

    public int Attempts { get; set; }

    public AdapterCallOutcome Outcome { get; set; }

    public int? StatusCode { get; set; }

    public string? CorrelationId { get; set; }

    public string? TagsJson { get; set; }
}

/// <summary>Full call-log row with the captured (already redacted + truncated) payloads.</summary>
public sealed class AdapterCallDetailModel
{
    public Guid Id { get; set; }

    public string AdapterName { get; set; } = string.Empty;

    public string Operation { get; set; } = string.Empty;

    public string? GroupName { get; set; }

    public DateTime Timestamp { get; set; }

    public double DurationMs { get; set; }

    public int Attempts { get; set; }

    public AdapterCallOutcome Outcome { get; set; }

    public int? StatusCode { get; set; }

    public string? ExceptionType { get; set; }

    public string? ExceptionMessage { get; set; }

    public string? RequestSummary { get; set; }

    public string? RequestHeaders { get; set; }

    public string? ResponseHeaders { get; set; }

    public string? RequestBody { get; set; }

    public string? ResponseBody { get; set; }

    public string MachineName { get; set; } = string.Empty;

    public string? TraceId { get; set; }

    public string? TagsJson { get; set; }

    public string? CorrelationId { get; set; }
}
