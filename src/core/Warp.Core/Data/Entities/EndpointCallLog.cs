using Warp.Core.Enums;

namespace Warp.Core.Data.Entities;

/// <summary>
/// One diagnostic row per inbound HTTP request to a Warp-exposed endpoint (the inbound mirror of
/// <see cref="AdapterCallLog"/>). Captures who called (<see cref="RemoteIp"/>, <see cref="UserAgent"/>,
/// <see cref="User"/>), how long it took, the outcome/status, and — per the capture tiers — the request
/// and response headers/bodies (stored post-redaction and post-truncation, §1.2). Not an audit trail:
/// same lossy, retention-bounded stance as <c>AdapterCallLog</c>/<c>JobLog</c>. Endpoint identity is the
/// HTTP <see cref="Method"/> plus the route <see cref="RouteTemplate"/> (already bounded — no runtime path
/// cardinality); <see cref="Operation"/> is the handler/route display name and <see cref="GroupName"/> is
/// an optional low-cardinality caller key (channel / client / tenant).
/// </summary>
public class EndpointCallLog
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Opt-in provenance: the application that OWNS this endpoint / served the request (<c>WarpConfiguration.ApplicationName</c>). Null ⇒ feature off / legacy row.</summary>
    public string? Application { get; set; }

    public string Method { get; set; } = string.Empty;

    public string RouteTemplate { get; set; } = string.Empty;

    public string Operation { get; set; } = string.Empty;

    public string? GroupName { get; set; }

    public DateTime Timestamp { get; set; }

    public double DurationMs { get; set; }

    public AdapterCallOutcome Outcome { get; set; }

    public int? StatusCode { get; set; }

    public string? RemoteIp { get; set; }

    /// <summary>Client session id (OTel <c>session.id</c>) read from the incoming W3C baggage, when the caller propagated one — joins this request to the browser session that made it (§8.27).</summary>
    public string? Session { get; set; }

    public string? UserAgent { get; set; }

    public string? User { get; set; }

    public string? ExceptionType { get; set; }

    public string? ExceptionMessage { get; set; }

    public string? RequestHeaders { get; set; }

    public string? ResponseHeaders { get; set; }

    public string? RequestBody { get; set; }

    public string? ResponseBody { get; set; }

    public string MachineName { get; set; } = string.Empty;

    /// <summary>
    /// W3C trace id of the request, stored as a <see cref="Guid"/> in the SAME form jobs use
    /// (<c>new Guid(Activity.TraceId.ToHexString())</c>) so jobs spawned during the request join directly
    /// on <c>Job.TraceId</c> — the request→jobs drill-down. Null when no <c>Activity</c> was flowing.
    /// </summary>
    public Guid? TraceId { get; set; }

    /// <summary>Custom per-request enrichment (user id, tenant, correlation id, …) as a JSON string→string map, set by the options enricher.</summary>
    public string? TagsJson { get; set; }

    public DateTime? ExpireAt { get; set; }
}
