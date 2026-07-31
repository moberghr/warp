using Warp.Core.Enums;

namespace Warp.Core.Data.Entities;

/// <summary>
/// The transient, write-optimized inbox for error signals (§8.29) — the <c>Counter</c> pattern applied to errors.
/// Each source appends one row carrying what it already has (no fingerprint computed on the hot path): jobs in the
/// existing finalization <c>SaveChanges</c>, the endpoint middleware for 4xx/5xx, the adapter/client flushers.
/// <c>ErrorGroupAggregator</c> DRAINS AND DELETES these each tick (exactly-once by construction — no cursor),
/// computes the fingerprint off the hot path, and folds them into <see cref="ErrorGroup"/> + the trend Counter.
/// A defensive sweep removes any orphaned rows. Always-in-schema (§2.11), mirrored by <c>WarpServerContext</c>.
/// </summary>
public class ErrorOccurrence
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public ErrorSource Source { get; set; }

    public ErrorKind Kind { get; set; }

    /// <summary>Exception type, or the status label for a 4xx group.</summary>
    public string ExceptionType { get; set; } = string.Empty;

    /// <summary>Raw exception message (normalized to the Title by the aggregator).</summary>
    public string? Message { get; set; }

    /// <summary>Raw stack (.NET <c>Exception.ToString()</c> tail or a browser stack), truncated — the top in-app frame is extracted off the hot path.</summary>
    public string? Stack { get; set; }

    /// <summary>Handler / <c>method+route</c> / <c>adapter.operation</c> / url — the fallback locus when no stack frame is parseable.</summary>
    public string Culprit { get; set; } = string.Empty;

    public int? StatusCode { get; set; }

    public Guid? TraceId { get; set; }

    public string? Application { get; set; }

    public DateTime Timestamp { get; set; }
}
