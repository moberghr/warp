using Warp.Core.Enums;

namespace Warp.Core.Data.Entities;

/// <summary>
/// One durable "issue" per error fingerprint (§8.29) — the grouped, deduplicated form of the raw error rows
/// (<c>JobLog</c>, <c>EndpointCallLog</c>, <c>AdapterCallLog</c>, <c>ClientEventLog</c>). Upserted off the hot
/// path by <c>ErrorGroupAggregator</c> as it drains the <see cref="ErrorOccurrence"/> inbox. The <see cref="Count"/>
/// and the <c>errorgroup:</c> Counter trend are durable and survive raw-row cleanup; <see cref="LastSample"/> is a
/// single representative kept for debugging. Always-in-schema (§2.11), mirrored by <c>WarpServerContext</c> (§2.14).
/// </summary>
public class ErrorGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Stable 32-hex identity: <c>hash(source + type + locus)</c>. Unique per group; the URL id.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    public ErrorSource Source { get; set; }

    /// <summary>Exception group vs status-code group (endpoint 4xx). Drives the default UI filter.</summary>
    public ErrorKind Kind { get; set; }

    /// <summary>Exception type (<c>System.NullReferenceException</c>, <c>TypeError</c>) or the status label (<c>HTTP 422</c>).</summary>
    public string ExceptionType { get; set; } = string.Empty;

    /// <summary>The normalized message (variable parts → placeholders) — PII-safe, shown in the list.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Where it happened: handler / <c>method+route</c> / <c>adapter.operation</c> / top frame.</summary>
    public string Culprit { get; set; } = string.Empty;

    /// <summary>HTTP status for a <see cref="ErrorKind.StatusCode"/> group; null for exception groups.</summary>
    public int? StatusCode { get; set; }

    /// <summary>Owning application — executor app for jobs (§8.23), source app otherwise. Null ⇒ unassigned.</summary>
    public string? Application { get; set; }

    public DateTime FirstSeenAt { get; set; }

    public DateTime LastSeenAt { get; set; }

    public long Count { get; set; }

    /// <summary>A raw, truncated representative (message + top frames). Only captured when <c>CaptureErrorSamples</c> is on (§1.2).</summary>
    public string? LastSample { get; set; }

    /// <summary>Trace id of the most recent occurrence, for the "jump to trace" link. Null when unavailable.</summary>
    public Guid? SampleTraceId { get; set; }

    /// <summary>App version at first sight of this issue (§8.23) — the "introduced in" hint. Null when unreported.</summary>
    public string? FirstSeenVersion { get; set; }

    /// <summary>App version of the most recent occurrence — a version bump here vs <see cref="FirstSeenVersion"/> flags a still-live issue across a deploy.</summary>
    public string? LastSeenVersion { get; set; }

    /// <summary>Deployment environment observed at first sight (§8.23). Null when unreported.</summary>
    public string? Environment { get; set; }

    /// <summary>
    /// JSON array of the most-recent occurrences (newest first, capped 10): trace id, timestamp, raw truncated
    /// message, version. Only maintained when <c>CaptureErrorSamples</c> is on (§1.2). Null otherwise.
    /// </summary>
    public string? RecentSamples { get; set; }

    public ErrorGroupStatus Status { get; set; } = ErrorGroupStatus.Unresolved;

    /// <summary>When <see cref="Status"/> last changed — a regression only counts occurrences after this instant.</summary>
    public DateTime? StatusChangedAt { get; set; }

    public DateTime? ExpireAt { get; set; }
}
