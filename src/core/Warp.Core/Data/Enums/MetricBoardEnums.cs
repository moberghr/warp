namespace Warp.Core.Enums;

/// <summary>What a metric health-board tile (#326) shows for its instrument. Values start at 1 (§8.11).</summary>
public enum MetricTileKind
{
    /// <summary>
    /// The instrument's value over the window: the matching events (sum for a counter or up-down counter, measurement
    /// count for a histogram), or a gauge's <c>GaugeAggregate</c> (average, min or max) over every sample.
    /// </summary>
    Value = 1,

    /// <summary>
    /// Good events ÷ (good + bad): events whose <c>RatioTag</c> matches <c>GoodValues</c> against those matching
    /// <c>BadValues</c>, every other value ignored (the <c>MetricRatio</c> SLO rule). Counters and histograms only.
    /// </summary>
    Ratio = 2,

    /// <summary>
    /// The metric page's chart of the instrument, with the controls stored in <c>ChartQuery</c> (breakdown, filters,
    /// compare, percentile, ratio view, rate) and the board's range. A picture, not a check: it has no status rule, so it
    /// never counts in the deploy verdict, and the board read computes nothing for it (the dashboard reads the series).
    /// </summary>
    Chart = 3,
}

/// <summary>How a health-board tile picks its status colour. Values start at 1 (§8.11).</summary>
public enum MetricTileStatusRule
{
    /// <summary>No status: the tile shows its numbers only.</summary>
    None = 1,

    /// <summary>The current value must stay at most / at least (<c>Comparison</c>) the <c>Threshold</c>.</summary>
    Threshold = 2,

    /// <summary>
    /// The current value must not move more than <c>MaxDeviation</c> from the same window <c>BaselineDays</c> earlier,
    /// in the <c>DeviationDirection</c>: a fraction of the baseline for a value tile, ratio points for a ratio tile.
    /// </summary>
    Deviation = 3,
}

/// <summary>Which move away from the baseline a <see cref="MetricTileStatusRule.Deviation"/> tile treats as bad. Values start at 1 (§8.11).</summary>
public enum MetricDeviationDirection
{
    /// <summary>Only a drop below the baseline counts (payment success rate, inbound volume).</summary>
    Drop = 1,

    /// <summary>Only a rise above the baseline counts (5xx count, latency).</summary>
    Rise = 2,

    /// <summary>A move either way counts.</summary>
    Either = 3,
}
