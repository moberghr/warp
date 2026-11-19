using Warp.Core.Enums;

namespace Warp.Core.Data.Entities;

/// <summary>
/// One durable Service-Level Objective (§8.31): a promise about a slice of Warp's traffic — a success-rate,
/// a queue-wait/execution latency percentile, a backlog depth, or a deadline-attainment ratio — scoped to a
/// queue / job-type (<see cref="Dimension"/>) and optionally an executor <see cref="Application"/> (§8.23).
/// Objectives are seeded from config (<c>AddSlo</c>) and editable in the dashboard; the DB row is the source of
/// truth (config seeding is insert-if-absent, so a dashboard edit is never clobbered on restart). Always-in-schema
/// (§2.11), mirrored by <c>WarpServerContext</c> (§2.14). The rolling status lives on <see cref="SloEvaluation"/>.
/// </summary>
public class SloDefinition
{
    public int Id { get; set; }

    /// <summary>Human label, shown in the dashboard and carried on the breach event.</summary>
    public string Name { get; set; } = string.Empty;

    public SloKind Kind { get; set; }

    /// <summary>What the objective is scoped to: a queue name, a job-type, or <c>*</c> for all. Sanitized on read.</summary>
    public string Dimension { get; set; } = "*";

    /// <summary>Executor application to scope to (§8.23), reading the <c>-app</c> aggregate slice. Null ⇒ not app-scoped.</summary>
    public string? Application { get; set; }

    /// <summary>
    /// The target: a ratio (0.995) for rate/attainment kinds, milliseconds for latency kinds, a job count for depth,
    /// and the latency threshold in the instrument's own unit for <see cref="SloKind.MetricLatency"/>.
    /// </summary>
    public double TargetValue { get; set; }

    /// <summary>
    /// Percentile (1–99, default 95) for the latency kinds, <see cref="SloKind.MetricLatency"/> included, and the
    /// required compliant share of the window for <see cref="SloKind.MetricGaugeThreshold"/>; null for rate/depth kinds.
    /// </summary>
    public int? Percentile { get; set; }

    /// <summary>Rolling evaluation window in seconds (e.g. 3600). The short burn window is derived as this / 12.</summary>
    public int WindowSeconds { get; set; } = 3600;

    public bool Enabled { get; set; } = true;

    /// <summary>Metric kinds: tag filter (<c>k=v[,k=v]</c>, trailing <c>*</c> prefix match) applied to the primary instrument held in <see cref="Dimension"/>.</summary>
    public string? TagFilter { get; set; }

    /// <summary><see cref="SloKind.MetricGap"/>: the instrument subtracted from the primary one.</summary>
    public string? SecondaryMetric { get; set; }

    /// <summary><see cref="SloKind.MetricGap"/>: tag filter applied to <see cref="SecondaryMetric"/>.</summary>
    public string? SecondaryTagFilter { get; set; }

    /// <summary><see cref="SloKind.MetricRatio"/>: the tag whose value classifies an event as good or bad.</summary>
    public string? RatioTag { get; set; }

    /// <summary><see cref="SloKind.MetricRatio"/>: CSV of <see cref="RatioTag"/> values counted as good; a value may end in <c>*</c>.</summary>
    public string? GoodValues { get; set; }

    /// <summary><see cref="SloKind.MetricRatio"/>: CSV of <see cref="RatioTag"/> values counted as bad; a value may end in <c>*</c>. Other values are ignored.</summary>
    public string? BadValues { get; set; }

    /// <summary>Baseline offset in days (1 or 7): <see cref="SloKind.MetricVolumeFloor"/> (required) and <see cref="SloKind.MetricRatio"/> (optional, turns the target into a maximum drop).</summary>
    public int? BaselineDays { get; set; }

    /// <summary><see cref="SloKind.MetricGaugeThreshold"/>: whether the aggregate must stay at most or at least the target (default at most).</summary>
    public SloComparison? Comparison { get; set; }

    /// <summary><see cref="SloKind.MetricGaugeThreshold"/>: the per-bucket aggregate compared with the target (default average).</summary>
    public SloGaugeAggregate? GaugeAggregate { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
