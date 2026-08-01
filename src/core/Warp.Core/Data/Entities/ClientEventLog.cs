using Warp.Core.Enums;

namespace Warp.Core.Data.Entities;

/// <summary>
/// One diagnostic row per client (browser) event ingested through the Warp ingest endpoint (§8.27) — the
/// client-side mirror of <see cref="EndpointCallLog"/> / <see cref="AdapterCallLog"/>. A single primitive
/// discriminated by <see cref="Type"/> (<see cref="ClientEventType"/>): an unhandled error (message + stack +
/// breadcrumbs), a Core Web Vital (numeric <see cref="Value"/>), an explicit log (a <see cref="Level"/>), or a
/// custom named event. Not an audit trail: lossy + retention-bounded (age AND count), same stance as
/// <c>EndpointCallLog</c>/<c>JobLog</c>. Trend data lives in the durable <c>clientevent:</c> Counter fold and
/// survives row cleanup. <see cref="Application"/> comes from the TRUSTED ingest-key mapping, never a
/// client-declared value. Payload fields are stored post-redaction and post-truncation (§1.2).
/// </summary>
public class ClientEventLog
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The application this event belongs to — resolved server-side from the ingest key, not client-declared. Null ⇒ legacy/unkeyed.</summary>
    public string? Application { get; set; }

    public ClientEventType Type { get; set; }

    /// <summary>Error type, vital name (LCP/CLS/…), or custom event name. Null for a bare log line. Collapsed to a bounded set only in the Counter fold — the raw name is kept here.</summary>
    public string? Name { get; set; }

    /// <summary>Log level (warn/error/…) for <see cref="ClientEventType.Log"/>. Null otherwise.</summary>
    public string? Level { get; set; }

    public string? Message { get; set; }

    public string? Stack { get; set; }

    /// <summary>Numeric measurement for <see cref="ClientEventType.Vital"/> (ms; CLS is unitless). Null otherwise.</summary>
    public double? Value { get; set; }

    /// <summary>The page URL/path the event fired on. Kept raw for context; never used as an aggregate dimension (unbounded).</summary>
    public string? Url { get; set; }

    /// <summary>
    /// W3C trace id the browser propagated on the API call this event represents (a <see cref="ClientEventType.Request"/>),
    /// stored as a <see cref="Guid"/> in the SAME form the server uses (<c>new Guid(traceId.ToString("N"))</c>) so it joins
    /// directly to <c>EndpointCallLog.TraceId</c> / <c>Job.TraceId</c> — the client end of the unified session timeline.
    /// Null for events not tied to a request.
    /// </summary>
    public Guid? TraceId { get; set; }

    public string? SessionId { get; set; }

    public string? Release { get; set; }

    public string? UserAgent { get; set; }

    /// <summary>Caller IP — PII (§1.2), captured only when the host opts in.</summary>
    public string? RemoteIp { get; set; }

    /// <summary>Custom event properties as a JSON string→value map, post-redaction + truncation.</summary>
    public string? Properties { get; set; }

    /// <summary>The breadcrumb trail (navigations/clicks/fetch) leading to an error, as a JSON array, truncated.</summary>
    public string? Breadcrumbs { get; set; }

    /// <summary>Client-reported event time (clamped to a sane window on ingest).</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>Server receive time.</summary>
    public DateTime ReceivedAt { get; set; }

    public DateTime? ExpireAt { get; set; }
}
