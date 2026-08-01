namespace Warp.Core.Enums;

/// <summary>
/// Which surface an error signal came from (§8.29). Part of an <see cref="Warp.Core.Data.Entities.ErrorGroup"/>'s
/// fingerprint identity — the same exception type from a browser and from a job are deliberately different issues.
/// Values from 1 (§8.11).
/// </summary>
public enum ErrorSource
{
    /// <summary>A failed job execution (any attempt, retry or terminal) — exception from <c>JobLog</c>.</summary>
    Job = 1,

    /// <summary>An inbound Warp HTTP endpoint call — a 5xx/unhandled exception, or a 4xx status-code group.</summary>
    Endpoint = 2,

    /// <summary>An outbound adapter call that failed with an exception.</summary>
    Adapter = 3,

    /// <summary>A browser error reported through the client ingest endpoint.</summary>
    Client = 4,
}
