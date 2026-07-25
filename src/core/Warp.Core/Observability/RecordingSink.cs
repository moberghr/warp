namespace Warp.Core.Observability;

/// <summary>
/// Where per-call observability records (adapter calls, endpoint calls) are sent once captured. Selected
/// at registration time (<c>AddAdapters</c> / <c>AddEndpointObservability</c>); the capture pipeline
/// (redaction, truncation, sampling gates) is identical regardless of sink.
/// </summary>
public enum RecordingSink
{
    /// <summary>
    /// Persist to database rows + write-optimised <c>Counter</c> aggregates for the Warp dashboard
    /// (list/detail pages, error rates, latency percentiles). The default — unchanged behavior.
    /// </summary>
    Database = 1,

    /// <summary>
    /// Emit each completed record as a single structured OTLP log (one <c>LogRecord</c> per call, every
    /// captured field carried as a log attribute) for an external collector. No database rows and no
    /// flusher are wired for the surface, so the dashboard's DB-backed aggregates are not populated —
    /// aggregate views come from OTel meters instead.
    /// </summary>
    Otel = 2,

    /// <summary>Fan each record to both sinks: DB rows/aggregates <b>and</b> the structured OTLP log.</summary>
    Both = 3,
}
