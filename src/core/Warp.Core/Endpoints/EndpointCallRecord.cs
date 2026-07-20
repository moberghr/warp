using Warp.Core.Enums;

namespace Warp.Core.Endpoints;

/// <summary>
/// Recording seam for completed inbound endpoint requests — the inbound mirror of
/// <c>IAdapterCallRecorder</c>. Public so the HTTP middleware binding in <c>Warp.Http</c> can hand a
/// completed record to the bounded-channel recorder (DB-only storage; OTel is the high-volume escape valve).
/// <para>
/// Implementations must be non-blocking and lossy-by-design: a completed request hands a record over
/// and the caller never waits on persistence. <see cref="Record"/> returns <c>false</c> when the
/// record could not be accepted (e.g. a bounded channel is full); the caller counts the drop and
/// continues. Inbound requests are never blocked or failed by recording.
/// </para>
/// </summary>
public interface IEndpointCallRecorder
{
    /// <summary>
    /// Hands a completed endpoint request record to the recorder. Returns <c>true</c> if accepted,
    /// <c>false</c> if dropped (channel full). Must not block or throw.
    /// </summary>
    bool Record(EndpointCallRecord record);
}

/// <summary>
/// Immutable snapshot of a completed inbound endpoint request, produced by the HTTP middleware binding
/// and consumed by an <see cref="IEndpointCallRecorder"/>. Capture-tier payload fields (headers/bodies)
/// are populated post-redaction and post-truncation (§1.2) by the binding; the rest is protocol-agnostic
/// request metadata.
/// </summary>
public sealed record EndpointCallRecord
{
    public required string Method { get; init; }

    public required string RouteTemplate { get; init; }

    public required string Operation { get; init; }

    public string? GroupName { get; init; }

    public DateTime Timestamp { get; init; }

    public double DurationMs { get; init; }

    public AdapterCallOutcome Outcome { get; init; }

    public int? StatusCode { get; init; }

    public string? RemoteIp { get; init; }

    public string? UserAgent { get; init; }

    public string? User { get; init; }

    public string? ExceptionType { get; init; }

    public string? ExceptionMessage { get; init; }

    public string? RequestHeaders { get; init; }

    public string? ResponseHeaders { get; init; }

    public string? RequestBody { get; init; }

    public string? ResponseBody { get; init; }

    public required string MachineName { get; init; }

    public string? TraceId { get; init; }

    /// <summary>
    /// Retention deadline stamped by the caller/middleware; the flusher just persists it onto the
    /// <c>EndpointCallLog</c> row.
    /// </summary>
    public DateTime? ExpireAt { get; init; }

    /// <summary>
    /// When <c>true</c>, the flusher skips the <c>EndpointCallLog</c> row for this record but still
    /// writes its <c>Counter</c> rows. Set by the binding for successful requests under
    /// <c>RecordCalls = FailuresOnly</c> — the mode gates call-log rows only, never counters/telemetry.
    /// </summary>
    public bool SuppressLog { get; init; }
}
