using Warp.Core.Enums;

namespace Warp.Core.Data.Entities;

/// <summary>
/// One captured OTel span in Warp's local, DB-backed trace store (§8.28) — the persisted form of a
/// <see cref="System.Diagnostics.Activity"/>. Rows for one <see cref="TraceId"/> form the trace waterfall
/// (parent/child via <see cref="ParentSpanId"/>). Captured by <c>WarpSpanCollector</c> from Warp's own
/// ActivitySource (job/receive/producer/adapter/webhook spans), the inbound ASP.NET request span (which adopts
/// the browser's propagated <c>traceparent</c>), and — opt-in — any additional sources. Highest-volume signal,
/// so it is sampled + aggressively retention-bounded; flip the sink to an external OTLP collector once the DB
/// is the bottleneck (§8.24). Diagnostics, not an audit trail.
/// </summary>
public class Span
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>W3C trace id (Guid "N" form) — joins <c>Job.TraceId</c> / <c>EndpointCallLog.TraceId</c>.</summary>
    public Guid TraceId { get; set; }

    /// <summary>W3C span id (16 hex chars).</summary>
    public string SpanId { get; set; } = string.Empty;

    /// <summary>Parent span id within the trace; null for the root span.</summary>
    public string? ParentSpanId { get; set; }

    public string Name { get; set; } = string.Empty;

    public SpanKind Kind { get; set; }

    public SpanStatus Status { get; set; }

    public DateTime StartTime { get; set; }

    public double DurationMs { get; set; }

    /// <summary>The application that emitted the span (<c>WarpConfiguration.ApplicationName</c>), when set.</summary>
    public string? Application { get; set; }

    /// <summary>Span tags/attributes as a JSON string→string map (truncated).</summary>
    public string? Attributes { get; set; }

    public DateTime? ExpireAt { get; set; }
}
