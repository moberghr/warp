using Warp.Core.Enums;

namespace Warp.Core.ClientObservability;

/// <summary>
/// A parsed, redaction-safe client (browser) event handed to <see cref="IClientEventRecorder"/> by the ingest
/// endpoint (§8.27). The binding is responsible for redaction + truncation before constructing this; the
/// flusher stamps <c>ReceivedAt</c>/<c>ExpireAt</c>. <see cref="Application"/> is the TRUSTED app resolved
/// from the ingest key (never client-declared).
/// </summary>
public sealed record ClientEventRecord
{
    public required string Application { get; init; }

    public required ClientEventType Type { get; init; }

    /// <summary>Error type / vital name / custom event name / log level (the per-name aggregate dimension). Null for a bare log.</summary>
    public string? Name { get; init; }

    public string? Level { get; init; }

    public string? Message { get; init; }

    public string? Stack { get; init; }

    public double? Value { get; init; }

    public string? Url { get; init; }

    /// <summary>W3C trace id (Guid form) the browser propagated for a <see cref="ClientEventType.Request"/>; joins to <c>EndpointCallLog.TraceId</c>.</summary>
    public Guid? TraceId { get; init; }

    public string? SessionId { get; init; }

    public string? Release { get; init; }

    public string? UserAgent { get; init; }

    public string? RemoteIp { get; init; }

    public string? Properties { get; init; }

    public string? Breadcrumbs { get; init; }

    public DateTime Timestamp { get; init; }
}

/// <summary>
/// Non-blocking, lossy sink for client events (§8.27) — the client-side mirror of
/// <c>IEndpointCallRecorder</c>. <see cref="Record"/> returns false when the bounded buffer is full; the
/// caller (the ingest endpoint) counts the drop and never blocks or fails the browser.
/// </summary>
public interface IClientEventRecorder
{
    bool Record(ClientEventRecord record);
}
