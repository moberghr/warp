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
}
