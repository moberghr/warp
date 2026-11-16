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
