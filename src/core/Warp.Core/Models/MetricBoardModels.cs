using Warp.Core.Enums;
using Warp.Core.Services;

namespace Warp.Core.Models;

/// <summary>One stored health-board tile (#326): the definition the dashboard edits.</summary>
public sealed class MetricBoardTileModel
{
    public int Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public int Position { get; init; }

    public string Instrument { get; init; } = string.Empty;

    public string? Application { get; init; }

    public string? TagFilter { get; init; }

    public MetricTileKind Kind { get; init; }

    public string? RatioTag { get; init; }

    public string? GoodValues { get; init; }

    public string? BadValues { get; init; }

    public SloGaugeAggregate? GaugeAggregate { get; init; }

    public MetricTileStatusRule StatusRule { get; init; }

    public SloComparison? Comparison { get; init; }

    public double? Threshold { get; init; }

    public int? BaselineDays { get; init; }

    public double? MaxDeviation { get; init; }

    public MetricDeviationDirection? DeviationDirection { get; init; }
}

/// <summary>One sparkline point: the tile's value in one display bucket.</summary>
public sealed class MetricBoardSparkPoint
{
    public DateTime Timestamp { get; init; }

    public double? Value { get; init; }
}

/// <summary>One evaluated tile: its value now and over the same window 1 and 7 days earlier, a status and a sparkline.</summary>
public sealed class MetricBoardTileResult
{
    public MetricBoardTileModel Tile { get; init; } = new();

    /// <summary>The instrument's registry kind; null when the instrument is not in the registry (the tile reads NoData).</summary>
    public MetricKind? InstrumentKind { get; init; }

    public string? Unit { get; init; }

    /// <summary>
    /// The value over the window: events (sum for a counter / up-down counter, count for a histogram), a gauge's
    /// aggregate, or a 0–1 ratio. Null when there is nothing to show.
    /// </summary>
    public double? Current { get; init; }

    /// <summary>The same window one day earlier.</summary>
    public double? DayAgo { get; init; }

    /// <summary>The same window seven days earlier.</summary>
    public double? WeekAgo { get; init; }

    /// <summary>
    /// Healthy / Warning / Breaching / NoData from the tile's rule, or Acknowledged when it would be Warning or Breaching
    /// and an acknowledged-noise annotation that applies to the tile is active at the window's end (#333); null when the
    /// tile has no rule.
    /// </summary>
    public SloState? Status { get; init; }

    /// <summary>The rule's own verdict, before acknowledged noise: equal to <see cref="Status"/> unless that is Acknowledged.</summary>
    public SloState? UnacknowledgedStatus { get; init; }

    /// <summary>Instrument annotations (#333) that apply to the tile and overlap the window, newest start first.</summary>
    public IReadOnlyList<MetricAnnotationModel> Annotations { get; init; } = [];

    public IReadOnlyList<MetricBoardSparkPoint> Sparkline { get; init; } = [];
}

/// <summary>The health board over one window: every tile, in board order.</summary>
public sealed class MetricBoardModel
{
    public DateTime From { get; init; }

    public DateTime To { get; init; }

    /// <summary>The sparkline resolution (the series read's tier by window length).</summary>
    public MetricTier Resolution { get; init; }

    public IReadOnlyList<MetricBoardTileResult> Tiles { get; init; } = [];
}
