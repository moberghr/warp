using Warp.Core.Enums;

namespace Warp.Core.Services;

/// <summary>
/// Pure error-budget arithmetic for SLO evaluation (§8.31) — no I/O, so it is unit-tested directly. Two
/// families: RATE objectives (success-rate, deadline-attainment) use the standard error-budget model
/// (budget = 1 − burn, burn = observed error rate ÷ allowed error rate), while THRESHOLD objectives
/// (latency percentile, backlog depth — lower is better) use a headroom fraction against the target. Both
/// surface a <c>BudgetRemaining</c> where <c>&lt; 0</c> means the objective is being missed, so a single
/// <see cref="Classify"/> drives state for every kind.
/// </summary>
internal static class SloMath
{
    /// <summary>Budget-remaining fraction below which a still-positive objective is flagged <see cref="SloState.Warning"/>.</summary>
    public const double WarningBudgetThreshold = 0.25;

    /// <summary>
    /// The result the metric evaluators (<see cref="EvaluateCount"/>, <see cref="EvaluateVolumeFloor"/>,
    /// <see cref="EvaluateRatioDrop"/>) return when an input or the computed value is not finite (NaN or ±∞, e.g. a
    /// gauge-like sum that overflowed). Every field is NaN. The evaluator must map it to <see cref="SloState.NoData"/>
    /// via <see cref="IsNoData"/> BEFORE calling <see cref="Classify"/> — NoData is the safe, non-alerting choice,
    /// where a forced −1 budget would page on garbage input. <see cref="Classify"/> is deliberately unchanged.
    /// </summary>
    public static readonly (double Attainment, double BudgetRemaining, double BurnRate) NoDataResult = (double.NaN, double.NaN, double.NaN);

    /// <summary>True when an evaluation produced <see cref="NoDataResult"/> (any non-finite field).</summary>
    public static bool IsNoData((double Attainment, double BudgetRemaining, double BurnRate) result)
        => !double.IsFinite(result.Attainment) || !double.IsFinite(result.BudgetRemaining) || !double.IsFinite(result.BurnRate);

    /// <summary>
    /// Error budget for a rate objective. <paramref name="target"/> is the desired good-ratio (e.g. 0.995).
    /// No observations ⇒ treated as fully healthy (nothing has burned). BurnRate &gt; 1 ⇒ budget negative.
    /// </summary>
    public static (double Attainment, double BudgetRemaining, double BurnRate) EvaluateRate(long good, long total, double target)
    {
        if (total <= 0)
        {
            return (1.0, 1.0, 0.0);
        }

        var attainment = (double)good / total;
        var errorRate = 1.0 - attainment;
        var allowedError = Math.Max(1e-9, 1.0 - target);
        var burn = errorRate / allowedError;

        return (attainment, 1.0 - burn, burn);
    }

    /// <summary>
    /// Compliance for a lower-is-better threshold objective (latency ms / backlog depth). Attainment is the
    /// observed value itself; budget is the headroom fraction <c>(target − observed) / target</c> (negative
    /// once over); burn is <c>observed / target</c> (&gt; 1 when over).
    /// </summary>
    public static (double Attainment, double BudgetRemaining, double BurnRate) EvaluateThreshold(double observed, double target)
    {
        var t = Math.Max(1e-9, target);

        return (observed, (t - observed) / t, observed / t);
    }

    /// <summary>
    /// Ratio objective whose target is a maximum DROP against a baseline ratio (metric kinds, §E). Attainment is
    /// the observed drop in ratio points (<c>baseline − current</c>), budget is <c>(target − drop) / target</c>
    /// (negative once <c>drop &gt; target</c>), and burn is <c>drop / target</c> (so budget = 1 − burn). The caller
    /// reports NoData when either window has no good + bad events.
    /// </summary>
    public static (double Attainment, double BudgetRemaining, double BurnRate) EvaluateRatioDrop(double baselineRatio, double currentRatio, double maxDrop)
    {
        if (!AllFinite(baselineRatio, currentRatio, maxDrop))
        {
            return NoDataResult;
        }

        var t = Math.Max(1e-9, maxDrop);
        var drop = baselineRatio - currentRatio;

        return Finite((drop, (t - drop) / t, drop / t));
    }

    /// <summary>
    /// Count objective for <c>MetricThreshold</c> and <c>MetricGap</c> (lower is better, N = <paramref name="limit"/>
    /// ≥ 0, 0 allowed). Breaching when <c>observed &gt; N</c>. Budget: N = 0 ⇒ <c>observed == 0 ? 1 : −observed</c>;
    /// otherwise <c>(N − observed) / N</c>. Burn is <c>observed / max(N, 1)</c> — for N ≥ 1 the fraction of the
    /// allowance consumed, for N = 0 the raw event count. The same function is applied to the short window for the
    /// short burn rate (for a gap the observed value is primary − secondary and may be negative).
    /// </summary>
    public static (double Attainment, double BudgetRemaining, double BurnRate) EvaluateCount(double observed, double limit)
    {
        if (!AllFinite(observed, limit))
        {
            return NoDataResult;
        }

        double budget;
        if (limit > 0)
        {
            budget = (limit - observed) / limit;
        }
        else
        {
            budget = Math.Abs(observed) < 1e-9 ? 1.0 : -observed;
        }

        return Finite((observed, budget, observed / Math.Max(limit, 1.0)));
    }

    /// <summary>
    /// Volume floor (<c>MetricVolumeFloor</c>): <c>ratio = observed / baseline</c>, breaching when
    /// <c>ratio &lt; target</c>, budget <c>(ratio − target) / (1 − target)</c>. Attainment is the ratio; burn is the
    /// shortfall against the allowed shortfall, <c>(1 − ratio) / (1 − target)</c> (budget = 1 − burn). A baseline of
    /// zero is NoData for the caller; here it reads as fully healthy (nothing to compare against).
    /// </summary>
    public static (double Attainment, double BudgetRemaining, double BurnRate) EvaluateVolumeFloor(double observed, double baseline, double target)
    {
        if (!AllFinite(observed, baseline, target))
        {
            return NoDataResult;
        }

        if (baseline <= 0)
        {
            return (1.0, 1.0, 0.0);
        }

        var ratio = observed / baseline;
        var allowed = Math.Max(1e-9, 1.0 - target);

        return Finite((ratio, (ratio - target) / allowed, (1.0 - ratio) / allowed));
    }

    /// <summary>
    /// Volume ceiling, the mirror of <see cref="EvaluateVolumeFloor"/> for a rise: <c>ratio = observed / baseline</c>,
    /// breaching when <c>ratio &gt; maxRatio</c> (&gt; 1), budget <c>(maxRatio − ratio) / (maxRatio − 1)</c>, burn
    /// <c>(ratio − 1) / (maxRatio − 1)</c>. A baseline of zero reads as fully healthy here; the caller reports NoData.
    /// </summary>
    public static (double Attainment, double BudgetRemaining, double BurnRate) EvaluateVolumeCeiling(double observed, double baseline, double maxRatio)
    {
        if (!AllFinite(observed, baseline, maxRatio))
        {
            return NoDataResult;
        }

        if (baseline <= 0)
        {
            return (1.0, 1.0, 0.0);
        }

        var ratio = observed / baseline;
        var allowed = Math.Max(1e-9, maxRatio - 1.0);

        return Finite((ratio, (maxRatio - ratio) / allowed, (ratio - 1.0) / allowed));
    }

    /// <summary>
    /// A level held to a bound (metric health-board threshold tiles, #326): at most / at least <paramref name="target"/>,
    /// equal never a violation (as <see cref="TryGaugeVerdict"/>). At most a positive target is exactly
    /// <see cref="EvaluateCount"/> (the <c>MetricThreshold</c> maths, so "5xx ≤ 10" reads the same on a tile and an SLO);
    /// otherwise the budget is the headroom over <c>|target|</c>, and a zero target is all-or-nothing (1, or minus the
    /// overshoot). Attainment is the observed value; burn is <c>1 − budget</c>.
    /// </summary>
    public static (double Attainment, double BudgetRemaining, double BurnRate) EvaluateBound(double observed, double target, SloComparison comparison)
    {
        if (!AllFinite(observed, target))
        {
            return NoDataResult;
        }

        if (comparison != SloComparison.AtLeast && target > 0)
        {
            return EvaluateCount(observed, target);
        }

        var headroom = comparison == SloComparison.AtLeast ? observed - target : target - observed;
        var scale = Math.Abs(target);
        var allOrNothing = headroom >= 0 ? 1.0 : headroom;
        var budget = scale < 1e-9 ? allOrNothing : headroom / scale;

        return Finite((observed, budget, 1.0 - budget));
    }

    /// <summary>
    /// How far <paramref name="current"/> moved from <paramref name="baseline"/> against a <paramref name="maxDeviation"/>
    /// in the <paramref name="direction"/> (metric health-board deviation tiles, #326). <paramref name="ratioPoints"/>
    /// reads both as ratios and the deviation as ratio points — a drop is <see cref="EvaluateRatioDrop"/> (the
    /// <c>MetricRatio</c>-with-baseline maths) and a rise the same with the two swapped; otherwise the deviation is a
    /// fraction of the baseline — a drop is <see cref="EvaluateVolumeFloor"/> at <c>1 − maxDeviation</c> (the
    /// <c>MetricVolumeFloor</c> maths) and a rise <see cref="EvaluateVolumeCeiling"/> at <c>1 + maxDeviation</c>.
    /// <see cref="MetricDeviationDirection.Either"/> takes whichever side has less budget. The caller reports NoData for
    /// a missing (or, for a relative deviation, non-positive) baseline.
    /// </summary>
    public static (double Attainment, double BudgetRemaining, double BurnRate) EvaluateDeviation(
        double current,
        double baseline,
        double maxDeviation,
        MetricDeviationDirection direction,
        bool ratioPoints)
    {
        var drop = ratioPoints
            ? EvaluateRatioDrop(baseline, current, maxDeviation)
            : EvaluateVolumeFloor(current, baseline, 1.0 - maxDeviation);
        var rise = ratioPoints
            ? EvaluateRatioDrop(current, baseline, maxDeviation)
            : EvaluateVolumeCeiling(current, baseline, 1.0 + maxDeviation);

        return direction switch
        {
            MetricDeviationDirection.Drop => drop,
            MetricDeviationDirection.Rise => rise,
            _ when IsNoData(drop) || IsNoData(rise) => NoDataResult,
            _ => drop.BudgetRemaining <= rise.BudgetRemaining ? drop : rise,
        };
    }

    /// <summary>
    /// Fraction (0..1) of the bucket <c>[bucketStart, bucketStart + bucketWidth)</c> that overlaps the window
    /// <c>(windowStart, windowEnd]</c>. A coarse bucket that only partly overlaps a window or baseline contributes
    /// its events scaled by this fraction (events assumed uniform within the bucket).
    /// </summary>
    public static double OverlapFraction(DateTime bucketStart, TimeSpan bucketWidth, DateTime windowStart, DateTime windowEnd)
    {
        if (bucketWidth <= TimeSpan.Zero || windowEnd <= windowStart)
        {
            return 0.0;
        }

        var bucketEnd = bucketStart + bucketWidth;
        var from = bucketStart > windowStart ? bucketStart : windowStart;
        var to = bucketEnd < windowEnd ? bucketEnd : windowEnd;
        if (to <= from)
        {
            return 0.0;
        }

        return Math.Clamp((to - from) / bucketWidth, 0.0, 1.0);
    }

    /// <summary>
    /// Maps a computed budget to a state. Below zero is a miss — <see cref="SloState.Acknowledged"/> when an
    /// operator ack is active (suppresses alerts), else <see cref="SloState.Breaching"/>. A thin positive
    /// margin is <see cref="SloState.Warning"/>; otherwise <see cref="SloState.Healthy"/>.
    /// </summary>
    public static SloState Classify(double budgetRemaining, bool acknowledgedActive)
    {
        if (budgetRemaining < 0)
        {
            return acknowledgedActive ? SloState.Acknowledged : SloState.Breaching;
        }

        return budgetRemaining < WarningBudgetThreshold ? SloState.Warning : SloState.Healthy;
    }

    /// <summary>
    /// The value at <paramref name="percentile"/> (e.g. 95) over a latency-bucket histogram (upper-bound → count).
    /// Mirrors the dashboard's percentile walk: the upper bound of the smallest bucket whose cumulative count
    /// reaches <c>ceil(q·N)</c>; the overflow bucket reports the last real bound as a displayable floor.
    /// </summary>
    public static double Percentile(IReadOnlyDictionary<int, long> buckets, int percentile)
    {
        var total = buckets.Values.Sum();
        if (total == 0)
        {
            return 0;
        }

        var q = Math.Clamp(percentile / 100.0, 0.0, 1.0);
        var threshold = (long)Math.Ceiling(q * total);
        long cumulative = 0;

        foreach (var bound in QueueWaitKeys.Buckets)
        {
            cumulative += buckets.GetValueOrDefault(bound);
            if (cumulative >= threshold)
            {
                return bound == int.MaxValue ? QueueWaitKeys.Buckets[^2] : bound;
            }
        }

        return QueueWaitKeys.Buckets[^2];
    }

    /// <summary>
    /// Splits one histogram row's bucket counts at a latency <paramref name="threshold"/> (<c>MetricLatency</c>): a bucket
    /// is within the threshold when its UPPER bound is at most the threshold, so a bucket that straddles it — and the
    /// overflow bucket — counts as over. Percentile resolution is therefore the bucket ladder: a threshold between two
    /// bounds behaves like the lower one. <paramref name="counts"/> has one entry per bound plus the overflow bucket;
    /// any other length is a misaligned row and returns false.
    /// </summary>
    public static bool TrySplitAtThreshold(IReadOnlyList<double> bounds, IReadOnlyList<long> counts, double threshold, out long within, out long over)
    {
        within = 0;
        over = 0;
        if (counts.Count != bounds.Count + 1)
        {
            return false;
        }

        for (var i = 0; i < counts.Count; i++)
        {
            if (i < bounds.Count && bounds[i] <= threshold)
            {
                within += counts[i];
            }
            else
            {
                over += counts[i];
            }
        }

        return true;
    }

    /// <summary>
    /// One gauge bucket's verdict for <c>MetricGaugeThreshold</c>: the bucket's <paramref name="aggregate"/> (average =
    /// <paramref name="sum"/> ÷ <paramref name="count"/>, or the min / max sample) compared with <paramref name="target"/>
    /// in the <paramref name="comparison"/> direction; equal to the target is never a violation. False when the bucket has
    /// no usable value (no samples, a missing extreme, or a non-finite aggregate), so it does not count as observed.
    /// </summary>
    public static bool TryGaugeVerdict(SloGaugeAggregate aggregate, SloComparison comparison, double target, long count, double sum, double? min, double? max, out bool violates)
    {
        violates = false;
        if (count <= 0)
        {
            return false;
        }

        var value = aggregate switch
        {
            SloGaugeAggregate.Min => min,
            SloGaugeAggregate.Max => max,
            _ => (double?)(sum / count),
        };

        if (value is not { } v || !double.IsFinite(v))
        {
            return false;
        }

        violates = comparison == SloComparison.AtLeast ? v < target : v > target;

        return true;
    }

    private static bool AllFinite(params ReadOnlySpan<double> values)
    {
        foreach (var value in values)
        {
            if (!double.IsFinite(value))
            {
                return false;
            }
        }

        return true;
    }

    // Finite inputs can still overflow (a huge observed over a tiny limit); that is NoData too, never ±∞ into Classify.
    private static (double Attainment, double BudgetRemaining, double BurnRate) Finite((double Attainment, double BudgetRemaining, double BurnRate) result)
        => IsNoData(result) ? NoDataResult : result;
}
