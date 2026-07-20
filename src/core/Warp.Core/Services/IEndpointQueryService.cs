using Warp.Core.Enums;

namespace Warp.Core.Services;

/// <summary>
/// Read side of inbound endpoint observability, over the user's DbContext. Registered by <c>AddWarp</c>
/// itself (like <see cref="IAdapterQueryService"/>) so dashboard-only / publisher-only processes serve
/// <c>/api/endpoints*</c> without running a server or calling <c>AddEndpointObservability()</c>. Counts,
/// error rates and average latency come from the merged <c>Statistic</c> + pending <c>Counter</c>
/// aggregates (so they survive log deletion); last-failure timestamps and the recent-calls list read the
/// retained <c>EndpointCallLog</c> rows and degrade to null/empty once logs are swept. The endpoint LIST is
/// discovered from the aggregates (endpoints that have received traffic) — there is no definition table.
/// </summary>
public interface IEndpointQueryService
{
    Task<IReadOnlyList<EndpointListItemModel>> GetEndpoints(CancellationToken ct = default);

    Task<EndpointDetailModel?> GetEndpointDetail(string id, CancellationToken ct = default);

    Task<EndpointCallDetailModel?> GetCallDetail(string id, Guid callId, CancellationToken ct = default);
}

/// <summary>One row on the endpoints (list) page. Identity is the HTTP method + normalized route template.</summary>
public sealed class EndpointListItemModel
{
    public string Id { get; set; } = string.Empty;

    public string Method { get; set; } = string.Empty;

    public string RouteTemplate { get; set; } = string.Empty;

    public string Route { get; set; } = string.Empty;

    public long TotalCalls { get; set; }

    public long ErrorCount { get; set; }

    public double ErrorRate { get; set; }

    public double AvgDurationMs { get; set; }
}

/// <summary>Per-caller (group) row of the detail page callers table.</summary>
public sealed class EndpointGroupStatModel
{
    public string Group { get; set; } = string.Empty;

    public long Calls { get; set; }

    public long Errors { get; set; }

    public double ErrorRate { get; set; }

    public double AvgDurationMs { get; set; }

    public DateTime? LastFailureAt { get; set; }
}

/// <summary>One entry in the detail page recent-calls list (no captured payload bodies/headers).</summary>
public sealed class EndpointCallSummaryModel
{
    public Guid Id { get; set; }

    public DateTime Timestamp { get; set; }

    public double DurationMs { get; set; }

    public AdapterCallOutcome Outcome { get; set; }

    public int? StatusCode { get; set; }

    public string? RemoteIp { get; set; }

    public string? UserAgent { get; set; }

    public string? User { get; set; }

    public string? GroupName { get; set; }
}

/// <summary>The endpoint detail page payload.</summary>
public sealed class EndpointDetailModel
{
    public string Id { get; set; } = string.Empty;

    public string Method { get; set; } = string.Empty;

    public string RouteTemplate { get; set; } = string.Empty;

    public string Route { get; set; } = string.Empty;

    public string GroupLabel { get; set; } = "Caller";

    public long TotalCalls { get; set; }

    public long ErrorCount { get; set; }

    public double ErrorRate { get; set; }

    public double AvgDurationMs { get; set; }

    /// <summary>90th-percentile request latency (ms), derived from the durable latency histogram; 0 when no data.</summary>
    public double P90DurationMs { get; set; }

    /// <summary>95th-percentile request latency (ms), derived from the durable latency histogram; 0 when no data.</summary>
    public double P95DurationMs { get; set; }

    /// <summary>99th-percentile request latency (ms), derived from the durable latency histogram; 0 when no data.</summary>
    public double P99DurationMs { get; set; }

    public IReadOnlyList<EndpointGroupStatModel> Groups { get; set; } = [];

    public IReadOnlyList<EndpointCallSummaryModel> RecentCalls { get; set; } = [];
}

/// <summary>Full call-log row with the captured (already redacted + truncated) payloads and caller metadata.</summary>
public sealed class EndpointCallDetailModel
{
    public Guid Id { get; set; }

    public string Method { get; set; } = string.Empty;

    public string RouteTemplate { get; set; } = string.Empty;

    public string Operation { get; set; } = string.Empty;

    public string? GroupName { get; set; }

    public DateTime Timestamp { get; set; }

    public double DurationMs { get; set; }

    public AdapterCallOutcome Outcome { get; set; }

    public int? StatusCode { get; set; }

    public string? RemoteIp { get; set; }

    public string? UserAgent { get; set; }

    public string? User { get; set; }

    public string? ExceptionType { get; set; }

    public string? ExceptionMessage { get; set; }

    public string? RequestHeaders { get; set; }

    public string? ResponseHeaders { get; set; }

    public string? RequestBody { get; set; }

    public string? ResponseBody { get; set; }

    public string MachineName { get; set; } = string.Empty;

    /// <summary>W3C trace id (matches <c>Job.TraceId</c>); links this request to the jobs it spawned.</summary>
    public Guid? TraceId { get; set; }

    /// <summary>Custom enrichment tags (user id, tenant, …) as a JSON string→string map.</summary>
    public string? TagsJson { get; set; }

    /// <summary>Jobs enqueued during this request (same trace id) — the request→jobs drill-down.</summary>
    public IReadOnlyList<EndpointRelatedJobModel> RelatedJobs { get; set; } = [];
}

/// <summary>A job spawned during a request (shares the request's trace id), shown on the call detail.</summary>
public sealed class EndpointRelatedJobModel
{
    public Guid Id { get; set; }

    public string? Type { get; set; }

    public State State { get; set; }

    public string Queue { get; set; } = string.Empty;
}
