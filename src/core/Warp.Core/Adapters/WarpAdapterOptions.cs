using Warp.Core.Enums;
using Warp.Core.Observability;

namespace Warp.Core.Adapters;

/// <summary>
/// Per-adapter observability configuration. Protocol-agnostic: shared by manual scopes and the
/// HTTP/Refit bindings (which layer transport-specific options on top). All capture is opt-in and
/// user-owned — the defaults are metadata-only with a redaction denylist, honouring the §1.2
/// PII-responsibility model.
/// </summary>
public sealed class WarpAdapterOptions
{
    /// <summary>
    /// Whether a call-log <b>row</b> is written per call. Default <see cref="CallRecording.All"/>
    /// (successes included). Decoupled from capture, which controls payload richness only.
    /// </summary>
    public CallRecording RecordCalls { get; set; } = CallRecording.All;

    /// <summary>
    /// Where completed call records are sent. Default <see cref="RecordingSink.Database"/> (DB rows +
    /// <c>Counter</c> aggregates for the dashboard — unchanged behavior); <see cref="RecordingSink.Otel"/>
    /// emits each record as a structured OTLP log (no DB rows/flusher/aggregates); <see cref="RecordingSink.Both"/>
    /// fans to both. Read at <c>AddAdapters()</c> registration time to select the process-wide recorder — the
    /// recording channel is a single per-process singleton, so this is a process-level knob, not per-adapter.
    /// </summary>
    public RecordingSink Sink { get; set; } = RecordingSink.Database;

    /// <summary>
    /// Fraction of <b>successful</b> call-log <b>rows</b> to keep (0.0–1.0). Applies to the raw row only:
    /// failures are always kept, and the <c>Counter</c>/<c>Statistic</c> aggregates (counts, error rate,
    /// latency percentiles) always record every call regardless of this value. <c>1.0</c> (default) keeps
    /// all rows; <c>0.0</c> keeps none (equivalent to <see cref="CallRecording.FailuresOnly"/> for
    /// successes); <c>0.1</c> keeps ~10% of successful rows. A per-call force-capture override
    /// (request option or ambient scope) writes the row regardless of the sample.
    /// </summary>
    public double SampleRate { get; set; } = 1.0;

    /// <summary>Capture tier for request bodies. Independent of <see cref="CaptureResponseBodies"/>.</summary>
    public CaptureMode CaptureRequestBodies { get; set; } = CaptureMode.None;

    /// <summary>Capture tier for response bodies. Independent of <see cref="CaptureRequestBodies"/>.</summary>
    public CaptureMode CaptureResponseBodies { get; set; } = CaptureMode.None;

    /// <summary>Capture tier for request and response headers (redacted per <see cref="RedactedHeaders"/>).</summary>
    public CaptureMode CaptureHeaders { get; set; } = CaptureMode.None;

    /// <summary>Truncation cap for a captured body, in bytes. Default 8 KB.</summary>
    public int MaxCapturedBodySize { get; set; } = 8 * 1024;

    /// <summary>Truncation cap for captured headers, in bytes. Default 4 KB.</summary>
    public int MaxCapturedHeaderSize { get; set; } = 4 * 1024;

    /// <summary>Per-adapter override of the global <c>WarpConfiguration.AdapterCallLogRetention</c>; null uses the global value.</summary>
    public TimeSpan? CallLogRetention { get; set; }

    /// <summary>
    /// Per-adapter override of the global <c>WarpConfiguration.AdapterCallLogRetentionCount</c> (keep at most
    /// this many call-log rows for this adapter, deleting the oldest beyond the cap); null uses the global
    /// value. Applied on top of the age cap — a row is removed once it exceeds either limit.
    /// </summary>
    public int? CallLogRetentionCount { get; set; }

    /// <summary>
    /// Case-insensitive header denylist whose values are stored as <c>***</c> when headers are
    /// captured. Fully user-owned (§1.2): prepopulated with the common credential-bearing headers,
    /// but callers may <c>Add</c>/<c>Remove</c>/<c>Clear</c> freely.
    /// </summary>
    public ISet<string> RedactedHeaders { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Proxy-Authorization",
        "Cookie",
        "Set-Cookie",
        "X-Api-Key",
    };

    /// <summary>Optional per-call enrichment hook; runs at call completion so it can add tags via <see cref="AdapterCallScope.SetTag"/>.</summary>
    public Action<AdapterCallScope>? EnrichCall { get; set; }

    /// <summary>
    /// Cardinality guard for <b>heuristic-derived</b> operation names. Once this many distinct
    /// heuristic names have been recorded for the adapter, further heuristic names collapse to the
    /// literal <c>{other}</c> with a one-time warning. Explicitly-supplied names are never collapsed.
    /// </summary>
    public int MaxDistinctOperations { get; set; } = 50;

    /// <summary>
    /// Cardinality guard for group values (runtime data, unbounded by nature). Beyond this many
    /// distinct groups, further new values collapse to the literal <c>{other}</c> with a one-time warning.
    /// </summary>
    public int MaxDistinctGroups { get; set; } = 500;

    /// <summary>Dashboard display name for the group dimension (e.g. "Endpoint", "Shop"). Default "Group".</summary>
    public string GroupLabel { get; set; } = "Group";

    /// <summary>
    /// Opt-in to include the group value as a meter tag (for bounded group sets). Default false —
    /// groups are unbounded and excluded from metrics to protect metric cardinality. The group is
    /// always recorded on the span attribute and counter key regardless of this flag.
    /// </summary>
    public bool IncludeGroupInMetrics { get; set; }
}
