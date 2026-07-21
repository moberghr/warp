using Warp.Core.Enums;

namespace Warp.Core.Adapters;

/// <summary>
/// Internal recording seam for completed adapter calls. Kept <c>internal</c> deliberately: DB-only
/// storage matches the <c>JobLog</c>/<c>ServerLog</c> precedent, and OTel is the high-volume escape
/// valve — a public pluggable-storage contract would balloon the read side (paging, retention). The
/// seam stays internal so promoting it later is cheap without shipping a public surface today.
/// <para>
/// Implementations must be non-blocking and lossy-by-design: a completed scope hands a record over
/// and the caller never waits on persistence. <see cref="Record"/> returns <c>false</c> when the
/// record could not be accepted (e.g. a bounded channel is full); the caller increments
/// <c>warp.adapter.records_dropped</c> and continues. User calls are never blocked or failed by recording.
/// </para>
/// </summary>
internal interface IAdapterCallRecorder
{
    /// <summary>
    /// Hands a completed call record to the recorder. Returns <c>true</c> if accepted, <c>false</c>
    /// if dropped (channel full). Must not block or throw.
    /// </summary>
    bool Record(AdapterCallRecord record);
}

/// <summary>
/// Immutable snapshot of a completed adapter call, produced by <see cref="AdapterCallScope"/> and
/// consumed by an <see cref="IAdapterCallRecorder"/>. Capture-tier payload fields (headers/bodies/
/// status) are populated by the transport binding; the protocol-agnostic core populates the rest.
/// </summary>
internal sealed record AdapterCallRecord
{
    public required string AdapterName { get; init; }

    public required string Operation { get; init; }

    public string? GroupName { get; init; }

    public DateTime Timestamp { get; init; }

    public double DurationMs { get; init; }

    public int Attempts { get; init; }

    public AdapterCallOutcome Outcome { get; init; }

    public int? StatusCode { get; init; }

    public string? ExceptionType { get; init; }

    public string? ExceptionMessage { get; init; }

    public string? RequestSummary { get; init; }

    public string? RequestHeaders { get; init; }

    public string? ResponseHeaders { get; init; }

    public string? RequestBody { get; init; }

    public string? ResponseBody { get; init; }

    public required string MachineName { get; init; }

    public string? TraceId { get; init; }

    public IReadOnlyList<KeyValuePair<string, string>>? Tags { get; init; }

    public string? CorrelationId { get; init; }

    /// <summary>
    /// When <c>true</c>, the flusher skips the <c>AdapterCallLog</c> row for this record but still
    /// writes its <c>Counter</c> rows and the <c>LastSeenAt</c>/definition upsert. Set by
    /// <see cref="AdapterCallScope"/> for successful calls under <c>RecordCalls = FailuresOnly</c> — the
    /// mode gates call-log rows only, never counters/telemetry.
    /// </summary>
    public bool SuppressLog { get; init; }
}
