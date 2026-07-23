using Warp.Core.Enums;

namespace Warp.Core.Data.Entities;

/// <summary>
/// One diagnostic row per completed adapter call (successes included under the default
/// <c>RecordCalls = All</c>; <c>FailuresOnly</c> is the volume knob). Not an audit trail — same
/// lossy, retention-bounded stance as <c>JobLog</c>/<c>ServerLog</c>. All captured payload fields
/// are stored post-redaction and post-truncation (§1.2). <c>CorrelationId</c> is a generic,
/// feature-agnostic link to a caller-owned domain record (e.g. a webhook delivery id) so the call
/// log is reusable as the attempt record for higher-level features.
/// </summary>
public class AdapterCallLog
{
    /// <summary>Opt-in provenance: the application that MADE this call (<c>WarpConfiguration.ApplicationName</c>). Null ⇒ feature off / legacy row.</summary>
    public string? Application { get; set; }

    public Guid Id { get; set; } = Guid.NewGuid();

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

    public DateTime? ExpireAt { get; set; }
}
