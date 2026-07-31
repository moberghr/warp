using Warp.Core.Enums;

namespace Warp.Core.Models;

/// <summary>
/// Read-side shapes for the error grouping / Issues dashboard (§8.29). The summary is the list-row projection of
/// an <c>ErrorGroup</c>; the detail adds the representative sample and the durable hourly trend
/// (<c>errorgroup:</c> Counter fold, survives raw-row cleanup, §8.22).
/// </summary>
public sealed class ErrorGroupSummaryModel
{
    public string Fingerprint { get; init; } = string.Empty;

    public ErrorSource Source { get; init; }

    public ErrorKind Kind { get; init; }

    public string ExceptionType { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Culprit { get; init; } = string.Empty;

    public int? StatusCode { get; init; }

    public string? Application { get; init; }

    public DateTime FirstSeenAt { get; init; }

    public DateTime LastSeenAt { get; init; }

    public long Count { get; init; }

    public ErrorGroupStatus Status { get; init; }

    /// <summary>First seen within the last 24h — flags a freshly-appeared issue.</summary>
    public bool IsNew { get; init; }

    /// <summary>An unresolved group that was previously resolved/ignored (its status changed) — a regression.</summary>
    public bool IsRegressed { get; init; }
}

public sealed class ErrorGroupTrendPoint
{
    public DateTime Hour { get; init; }

    public long Count { get; init; }
}

public sealed class ErrorGroupDetailModel
{
    public string Fingerprint { get; init; } = string.Empty;

    public ErrorSource Source { get; init; }

    public ErrorKind Kind { get; init; }

    public string ExceptionType { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Culprit { get; init; } = string.Empty;

    public int? StatusCode { get; init; }

    public string? Application { get; init; }

    public DateTime FirstSeenAt { get; init; }

    public DateTime LastSeenAt { get; init; }

    public long Count { get; init; }

    public ErrorGroupStatus Status { get; init; }

    public bool IsNew { get; init; }

    public bool IsRegressed { get; init; }

    /// <summary>A raw, truncated representative (message + top frames). Null when <c>CaptureErrorSamples</c> is off.</summary>
    public string? LastSample { get; init; }

    /// <summary>Trace id of the most recent occurrence, for the "jump to trace" link. Null when unavailable.</summary>
    public Guid? SampleTraceId { get; init; }

    /// <summary>The last 24 hourly buckets, ascending — folded from the durable <c>errorgroup:</c> trend keys.</summary>
    public IReadOnlyList<ErrorGroupTrendPoint> Trend { get; init; } = [];
}

public sealed class ErrorGroupListModel
{
    public IReadOnlyList<ErrorGroupSummaryModel> Items { get; init; } = [];

    public int Total { get; init; }
}
