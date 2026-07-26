namespace Warp.Core.Enums;

/// <summary>
/// Discriminates the one <see cref="Warp.Core.Data.Entities.ClientEventLog"/> primitive into the four kinds a
/// browser reports (§8.27). Values from 1 (§8.11). <see cref="Log"/> and <see cref="Event"/> are the
/// host-driven <c>log()</c> / <c>track()</c> shapes; <see cref="Error"/> and <see cref="Vital"/> are
/// auto-captured by the shipped client script.
/// </summary>
public enum ClientEventType
{
    /// <summary>Unhandled error / rejection: message + stack + breadcrumbs.</summary>
    Error = 1,

    /// <summary>A Core Web Vital sample (LCP/CLS/INP/FCP/TTFB): a numeric <c>Value</c>.</summary>
    Vital = 2,

    /// <summary>An explicit structured log line: a <c>Level</c> + message.</summary>
    Log = 3,

    /// <summary>A custom named event: <c>track(name, props)</c>.</summary>
    Event = 4,
}
