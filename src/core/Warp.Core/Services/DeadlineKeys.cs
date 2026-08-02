using Warp.Core.Data.Entities;
using Warp.Core.Entities;

namespace Warp.Core.Services;

/// <summary>
/// Builds and parses the free-form <see cref="Counter"/> keys for per-job-TYPE deadline-attainment statistics
/// (§8.30) — how often a <c>Total</c>-scope timeout deadline (§8.7) was met versus missed. Recorded at the single
/// finalization site (never on the fetch/claim loop): every <c>Total</c>-scope termination bumps
/// <see cref="CountToken"/>, and a deadline breach additionally bumps <see cref="MissToken"/>, so attainment over
/// a window = <c>1 − miss ÷ count</c>. Rides the standard <c>Counter → CounterAggregator → Statistic</c> fold, so
/// the rate survives raw <see cref="Job"/>-row cleanup — no new aggregation or cleanup machinery. Unlike
/// <see cref="QueueWaitKeys"/>/<see cref="JobStatsKeys"/> this is a RATE, not a latency, so there is no duration
/// sum and no <c>pct</c> histogram — just count, miss, and their hourly <c>hist</c> series (for windowed reads).
/// Keys live under their OWN top-level prefixes (<see cref="Prefix"/> <c>"deadline"</c> / <see cref="AppPrefix"/>
/// <c>"deadline-app"</c>), DISJOINT from every existing family (first-segment-equality parsers reject them,
/// §8.6/§8.19). The job type and application are passed through <see cref="Sanitize"/> so any stray <c>':'</c>
/// becomes <c>'-'</c>, guaranteeing the colon-delimited key parses unambiguously.
/// </summary>
internal static class DeadlineKeys
{
    public const string Prefix = "deadline";

    public const string AppPrefix = "deadline-app";

    // Every Total-scope termination — the attainment denominator.
    public const string CountToken = "count";

    // The subset that breached their deadline — the attainment numerator complement.
    public const string MissToken = "miss";

    // Hourly time-series marker (deadline:{type}:hist:{token}:{yyyy-MM-dd-HH}).
    public const string HistoryMarker = "hist";

    public static string Total(string type, string token) => $"{Prefix}:{type}:{token}";

    // tierSuffix is MetricTiers.Suffix(...) — ":{marker}:{stamp}" (§8.30), so deadline attainment gets the same
    // fine→hourly→daily downsampling and 5-minute fast-burn resolution as jobstat/qwait.
    public static string History(string type, string token, string tierSuffix) => $"{Prefix}:{type}:{HistoryMarker}:{token}{tierSuffix}";

    public static string AppTotal(string application, string type, string token) => $"{AppPrefix}:{application}:{type}:{token}";

    public static string AppHistory(string application, string type, string token, string tierSuffix) => $"{AppPrefix}:{application}:{type}:{HistoryMarker}:{token}{tierSuffix}";

    public static string HourBucket(DateTime timestampUtc) => QueueWaitKeys.HourBucket(timestampUtc);

    public static string Sanitize(string value) => value.Replace(':', '-');

    /// <summary>
    /// Produces every deadline-attainment counter for one terminated <c>Total</c>-scope job. Counter construction
    /// only — no reads, no orchestration — so it is safe at the finalization site (§0.2/§6.1): the returned rows
    /// ride the <c>SaveChanges</c> that already commits the terminal state. Always emits the count (denominator);
    /// when <paramref name="missed"/>, additionally emits the miss. Both get an hourly slice, and the same under
    /// the per-application slice when <paramref name="application"/> is set.
    /// </summary>
    public static List<Counter> Build(string type, bool missed, string? application, string tierSuffix)
    {
        var counters = new List<Counter>();
        var t = Sanitize(type);

        counters.Add(new Counter { Key = Total(t, CountToken), Value = 1 });
        counters.Add(new Counter { Key = History(t, CountToken, tierSuffix), Value = 1 });

        if (missed)
        {
            counters.Add(new Counter { Key = Total(t, MissToken), Value = 1 });
            counters.Add(new Counter { Key = History(t, MissToken, tierSuffix), Value = 1 });
        }

        if (application is null)
        {
            return counters;
        }

        var app = Sanitize(application);

        counters.Add(new Counter { Key = AppTotal(app, t, CountToken), Value = 1 });
        counters.Add(new Counter { Key = AppHistory(app, t, CountToken, tierSuffix), Value = 1 });

        if (missed)
        {
            counters.Add(new Counter { Key = AppTotal(app, t, MissToken), Value = 1 });
            counters.Add(new Counter { Key = AppHistory(app, t, MissToken, tierSuffix), Value = 1 });
        }

        return counters;
    }

    // Parses a lifetime total key (deadline:{type}:{token}); token ∈ {count, miss}. Rejects history (length 5)
    // and every non-"deadline" key.
    public static bool TryParseTotal(string key, out string type, out string token)
    {
        type = string.Empty;
        token = string.Empty;

        var parts = key.Split(':');
        if (parts.Length != 3 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        type = parts[1];
        token = parts[2];

        return true;
    }

    // Parses a per-app total key (deadline-app:{app}:{type}:{token}). Rejects the per-app history keys (length 6).
    public static bool TryParseApp(string key, out string application, out string type, out string token)
    {
        application = string.Empty;
        type = string.Empty;
        token = string.Empty;

        var parts = key.Split(':');
        if (parts.Length != 4 || !string.Equals(parts[0], AppPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        application = parts[1];
        type = parts[2];
        token = parts[3];

        return true;
    }

    // Parses a tiered history key (deadline:{type}:hist:{token}:{tier}:{stamp}, §8.30) — the windowed read the
    // SloEvaluator sums for deadline-attainment. Disjoint from the length-3 lifetime key.
    public static bool TryParseHistory(string key, out string type, out string token, out MetricTier tier, out DateTime bucket)
    {
        type = string.Empty;
        token = string.Empty;
        tier = default;
        bucket = default;

        var parts = key.Split(':');
        if (parts.Length != 6 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal) || !string.Equals(parts[2], HistoryMarker, StringComparison.Ordinal))
        {
            return false;
        }

        if (!MetricTiers.TryParse(parts[4], parts[5], out tier, out bucket))
        {
            return false;
        }

        type = parts[1];
        token = parts[3];

        return true;
    }

    // Parses a per-app tiered history key (deadline-app:{app}:{type}:hist:{token}:{tier}:{stamp}).
    public static bool TryParseAppHistory(string key, out string application, out string type, out string token, out MetricTier tier, out DateTime bucket)
    {
        application = string.Empty;
        type = string.Empty;
        token = string.Empty;
        tier = default;
        bucket = default;

        var parts = key.Split(':');
        if (parts.Length != 7 || !string.Equals(parts[0], AppPrefix, StringComparison.Ordinal) || !string.Equals(parts[3], HistoryMarker, StringComparison.Ordinal))
        {
            return false;
        }

        if (!MetricTiers.TryParse(parts[5], parts[6], out tier, out bucket))
        {
            return false;
        }

        application = parts[1];
        type = parts[2];
        token = parts[4];

        return true;
    }
}
