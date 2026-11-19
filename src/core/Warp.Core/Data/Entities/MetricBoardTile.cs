using Warp.Core.Enums;

namespace Warp.Core.Data.Entities;

/// <summary>
/// One tile of the metrics health board (#326, §8.34): an instrument, an optional tag filter or a good/bad ratio, and an
/// optional status rule (a threshold, or a maximum deviation from the same window 1 or 7 days earlier). Tiles are seeded
/// from config (<c>AddMetricBoard</c>) and editable in the dashboard; the DB row is the source of truth (seeding is
/// insert-if-absent, so a dashboard edit is never clobbered on restart). Always-in-schema (§2.11), mirrored by
/// <c>WarpServerContext</c> (§2.14). The board is computed on read from <c>metric_point</c>; nothing is stored per tile.
/// </summary>
public class MetricBoardTile
{
    public int Id { get; set; }

    /// <summary>
    /// The <see cref="MetricBoard"/> holding the tile: a soft reference (no foreign key, like
    /// <c>MetricAnnotation.SloDefinitionId</c>; deleting a board deletes its tiles in the same <c>SaveChanges</c>). Null is
    /// the Default board, which is how every tile stored before named boards reads; the command service and the seeder
    /// store Default's tiles as null too, so there is one representation.
    /// </summary>
    public int? BoardId { get; set; }

    /// <summary>Label shown on the tile.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Board order, ascending (ties by id).</summary>
    public int Position { get; set; }

    /// <summary>The instrument name, as in <c>metric_instrument</c>.</summary>
    public string Instrument { get; set; } = string.Empty;

    /// <summary>Restricts the tile to one application's rows (§8.23); null reads every application.</summary>
    public string? Application { get; set; }

    /// <summary>Tag filter (<c>k=v[,k=v]</c>, trailing <c>*</c> prefix match): the one <c>MetricFilter</c> grammar.</summary>
    public string? TagFilter { get; set; }

    public MetricTileKind Kind { get; set; } = MetricTileKind.Value;

    /// <summary>
    /// <see cref="MetricTileKind.Chart"/>: the metric page's controls as a URL query string without the range
    /// (<c>breakdown=…&amp;filter=k:v&amp;compare=7d&amp;percentile=95</c>, ≤ 2000 characters, stored canonical). The board's
    /// range drives the chart. Null for every other kind.
    /// </summary>
    public string? ChartQuery { get; set; }

    /// <summary><see cref="MetricTileKind.Ratio"/>: the tag whose value classifies an event as good or bad.</summary>
    public string? RatioTag { get; set; }

    /// <summary><see cref="MetricTileKind.Ratio"/>: CSV of <see cref="RatioTag"/> values counted as good; a value may end in <c>*</c>.</summary>
    public string? GoodValues { get; set; }

    /// <summary><see cref="MetricTileKind.Ratio"/>: CSV of <see cref="RatioTag"/> values counted as bad; a value may end in <c>*</c>.</summary>
    public string? BadValues { get; set; }

    /// <summary>A <see cref="MetricTileKind.Value"/> tile on a gauge: the aggregate of the window's samples (default average).</summary>
    public SloGaugeAggregate? GaugeAggregate { get; set; }

    public MetricTileStatusRule StatusRule { get; set; } = MetricTileStatusRule.None;

    /// <summary><see cref="MetricTileStatusRule.Threshold"/>: whether the value must stay at most or at least <see cref="Threshold"/>.</summary>
    public SloComparison? Comparison { get; set; }

    /// <summary><see cref="MetricTileStatusRule.Threshold"/>: the limit, in the tile's own value (events, the gauge's unit, or a 0–1 ratio).</summary>
    public double? Threshold { get; set; }

    /// <summary><see cref="MetricTileStatusRule.Deviation"/>: the baseline offset in days (1 or 7).</summary>
    public int? BaselineDays { get; set; }

    /// <summary>
    /// <see cref="MetricTileStatusRule.Deviation"/>: the largest allowed move — a fraction of the baseline for a value
    /// tile (0.3 = 30 %), ratio points for a ratio tile (0.02 = 2 points).
    /// </summary>
    public double? MaxDeviation { get; set; }

    /// <summary><see cref="MetricTileStatusRule.Deviation"/>: which move counts (default either way).</summary>
    public MetricDeviationDirection? DeviationDirection { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
