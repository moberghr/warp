using Warp.Core.Enums;
using Warp.Core.Services;

namespace Warp.Core.Models;

/// <summary>One stored health-board tile (#326): the definition the dashboard edits.</summary>
public sealed class MetricBoardTileModel
{
    public int Id { get; init; }

    /// <summary>The board holding the tile; null is the Default board.</summary>
    public int? BoardId { get; init; }

    public string Name { get; init; } = string.Empty;

    public int Position { get; init; }

    public string Instrument { get; init; } = string.Empty;

    public string? Application { get; init; }

    public string? TagFilter { get; init; }

    public MetricTileKind Kind { get; init; }

    /// <summary>A chart tile's stored metric-page controls (a URL query string without the range); null for other kinds.</summary>
    public string? ChartQuery { get; init; }

    /// <summary>An SLO tile's objective id (a soft reference); null for other kinds.</summary>
    public int? SloDefinitionId { get; init; }

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
    /// and an acknowledged-noise annotation covering the tile is active at the window's end (#333); null when the
    /// tile has no rule.
    /// </summary>
    public SloState? Status { get; init; }

    /// <summary>The rule's own verdict, before acknowledged noise: equal to <see cref="Status"/> unless that is Acknowledged.</summary>
    public SloState? UnacknowledgedStatus { get; init; }

    /// <summary>Instrument annotations (#333) that apply to the tile and overlap the window, newest start first.</summary>
    public IReadOnlyList<MetricAnnotationModel> Annotations { get; init; } = [];

    public IReadOnlyList<MetricBoardSparkPoint> Sparkline { get; init; } = [];

    /// <summary>
    /// An SLO tile's objective with its current evaluation (from the one objective read of the board); null for other kinds
    /// and when the objective no longer exists (<see cref="Missing"/>).
    /// </summary>
    public SloObjectiveModel? Slo { get; init; }

    /// <summary>
    /// True for an SLO tile whose objective no longer exists (deleted by an older binary mid rolling deploy, outside Warp,
    /// or in a race in a host without a provider, where no board lock is taken).
    /// </summary>
    public bool Missing { get; init; }
}

/// <summary>One named board as the picker lists it.</summary>
public sealed class MetricBoardSummaryModel
{
    public int Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public int Position { get; init; }

    /// <summary>The application the picker scopes the board to by default; it does not filter the tiles.</summary>
    public string? Application { get; init; }

    /// <summary>True for the Default board, which owns the tiles with no board id and cannot be deleted.</summary>
    public bool IsDefault { get; init; }

    public int TileCount { get; init; }

    public string? CreatedBy { get; init; }
}

/// <summary>The health board over one window: every tile, in board order.</summary>
public sealed class MetricBoardModel
{
    /// <summary>
    /// The board read; null for a read across every board (the deploy verdict without a <c>board</c>). Its
    /// <see cref="MetricBoardSummaryModel.TileCount"/> is the number of tiles evaluated.
    /// </summary>
    public MetricBoardSummaryModel? Board { get; init; }

    public DateTime From { get; init; }

    public DateTime To { get; init; }

    /// <summary>The sparkline resolution (the series read's tier by window length).</summary>
    public MetricTier Resolution { get; init; }

    public IReadOnlyList<MetricBoardTileResult> Tiles { get; init; } = [];

    /// <summary>
    /// Where the tile values and their 1d / 7d baselines stop (#328): the start of the fine bucket that holds
    /// <c>now − MetricFlushInterval</c>, clamped to the range. Data after it is still being written by the processes, so
    /// the current window and the shifted baselines are all cut here and compare complete data with complete data. Equal
    /// to <see cref="To"/> for a range that ended earlier; equal to <see cref="From"/> when nothing in the range is complete
    /// yet (the values are then empty). The sparklines still run to <see cref="To"/>.
    /// </summary>
    public DateTime CompleteUntil { get; init; }

    /// <summary>
    /// True when more than <c>MetricAnnotationValidation.MaxPerRead</c> instrument notes overlap the range: the board then
    /// keeps the ones active at its end (ongoing first) and acknowledged noise before the rest, newest first.
    /// </summary>
    public bool AnnotationsTruncated { get; init; }
}
