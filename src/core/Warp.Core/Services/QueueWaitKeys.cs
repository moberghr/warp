using System.Collections.Concurrent;
using System.Globalization;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;

namespace Warp.Core.Services;

/// <summary>
/// Builds and parses the free-form <see cref="Counter"/> keys for per-QUEUE queue-wait statistics (§8.26) —
/// the time a job spent eligible-but-unclaimed (<c>claimTime − Job.ScheduleTime</c>), recorded once per claim.
/// Mirrors <see cref="JobStatsKeys"/> exactly (same latency <see cref="Buckets"/>, <c>dur</c> sum, <c>pct</c>
/// histogram, hourly <c>hist</c> series, and disjoint app-agnostic / per-app prefixes) so avg + p95/p99
/// survive raw <see cref="Job"/>-row cleanup via the standard <c>Counter → CounterAggregator → Statistic</c>
/// fold — no new aggregation or cleanup machinery. Keys live under their OWN top-level prefixes
/// (<see cref="Prefix"/> <c>"qwait"</c> / <see cref="AppPrefix"/> <c>"qwait-app"</c>), DISJOINT from every
/// existing family (first-segment-equality parsers reject them, §8.6/§8.19). Unlike jobstat there is no
/// outcome axis — queue-wait applies to every claim — so the count token is a single <see cref="CountToken"/>.
/// The queue name and application are passed through <see cref="Sanitize"/> so any stray <c>':'</c> becomes
/// <c>'-'</c>, guaranteeing the colon-delimited key parses unambiguously.
/// </summary>
internal static class QueueWaitKeys
{
    public const string Prefix = "qwait";

    public const string AppPrefix = "qwait-app";

    // Sample count (one per claim) — the denominator for average wait and the percentile walk.
    public const string CountToken = "count";

    // Per-queue wait-duration SUM (ms). Average = dur ÷ count; survives Job-row cleanup.
    public const string DurationToken = "dur";

    // Latency-histogram bucket marker (qwait:{queue}:pct:{upperMs}) — length 4.
    public const string PctMarker = "pct";

    // Tiered time-series marker (qwait:{queue}:hist:{token}:{tier}:{stamp}, §8.30).
    public const string HistoryMarker = "hist";

    // Tiered latency-histogram marker (qwait:{queue}:pcth:{upperMs}:{tier}:{stamp}) — the windowed counterpart
    // of the lifetime Pct histogram.
    public const string PctHistoryMarker = "pcth";

    // Mirrors JobStatsKeys.Buckets exactly (job-domain scale, up to 5 min) so the percentile walk is identical
    // across the jobstat and qwait surfaces.
    public static readonly int[] Buckets = [5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000, 30000, 60000, 300000, int.MaxValue];

    public static string Total(string queue, string token) => $"{Prefix}:{queue}:{token}";

    public static string Pct(string queue, int upperMs) => $"{Prefix}:{queue}:{PctMarker}:{upperMs.ToString(CultureInfo.InvariantCulture)}";

    // tierSuffix is MetricTiers.Suffix(...) — ":{marker}:{stamp}".
    public static string History(string queue, string token, string tierSuffix) => $"{Prefix}:{queue}:{HistoryMarker}:{token}{tierSuffix}";

    public static string PctHistory(string queue, int upperMs, string tierSuffix) => $"{Prefix}:{queue}:{PctHistoryMarker}:{upperMs.ToString(CultureInfo.InvariantCulture)}{tierSuffix}";

    public static string AppTotal(string application, string queue, string token) => $"{AppPrefix}:{application}:{queue}:{token}";

    public static string AppHistory(string application, string queue, string token, string tierSuffix) => $"{AppPrefix}:{application}:{queue}:{HistoryMarker}:{token}{tierSuffix}";

    public static string HourBucket(DateTime timestampUtc) => timestampUtc.ToString("yyyy-MM-dd-HH", CultureInfo.InvariantCulture);

    public static int BucketFor(int durationMs) => Buckets.First(bound => durationMs <= bound);

    public static string Sanitize(string value) => value.Replace(':', '-');

    /// <summary>
    /// Produces every queue-wait counter for one claimed job. Counter construction only — no reads, no
    /// orchestration — so it is safe on the worker fetch/execute hot path (§0.2/§6.1): the returned rows are
    /// added to the context that already commits the "Processing" JobLog at claim, so they ride that existing
    /// <c>SaveChanges</c> with no extra round-trip. Emits per-queue count + hourly count, a duration sum +
    /// latency-histogram bucket + hourly duration, and the same under the per-application slice (histogram
    /// omitted there to bound volume) when <paramref name="application"/> is set.
    /// </summary>
    public static List<Counter> Build(string queue, double waitMs, string? application, string tierSuffix)
    {
        var counters = new List<Counter>(application is null ? 6 : 10);

        // Clamp before the int cast: a genuinely stuck queue (the case this SLI exists to surface) can exceed
        // int.MaxValue ms (~24.8 days), and an unchecked cast would wrap to a negative value and poison the
        // durable avg (dur ÷ count). The always-on meter is a double and unaffected.
        var durMs = (int)Math.Min(int.MaxValue, Math.Round(Math.Max(0, waitMs), MidpointRounding.AwayFromZero));
        var bucket = BucketFor(durMs);

        counters.Add(new Counter { Key = Key(KeyKind.Total, queue, CountToken, 0, null, tierSuffix), Value = 1 });
        counters.Add(new Counter { Key = Key(KeyKind.History, queue, CountToken, 0, null, tierSuffix), Value = 1 });
        counters.Add(new Counter { Key = Key(KeyKind.Total, queue, DurationToken, 0, null, tierSuffix), Value = durMs });
        counters.Add(new Counter { Key = Key(KeyKind.History, queue, DurationToken, 0, null, tierSuffix), Value = durMs });

        // Lifetime pct (dashboard all-time percentiles) + tiered pcth (windowed percentiles, §8.30).
        counters.Add(new Counter { Key = Key(KeyKind.Pct, queue, string.Empty, bucket, null, tierSuffix), Value = 1 });
        counters.Add(new Counter { Key = Key(KeyKind.PctHistory, queue, string.Empty, bucket, null, tierSuffix), Value = 1 });

        if (application is null)
        {
            return counters;
        }

        counters.Add(new Counter { Key = Key(KeyKind.AppTotal, queue, CountToken, 0, application, tierSuffix), Value = 1 });
        counters.Add(new Counter { Key = Key(KeyKind.AppHistory, queue, CountToken, 0, application, tierSuffix), Value = 1 });
        counters.Add(new Counter { Key = Key(KeyKind.AppTotal, queue, DurationToken, 0, application, tierSuffix), Value = durMs });
        counters.Add(new Counter { Key = Key(KeyKind.AppHistory, queue, DurationToken, 0, application, tierSuffix), Value = durMs });

        return counters;
    }

    // Parses a lifetime total key (qwait:{queue}:{token}); token ∈ {count, dur}. Rejects pct/history (longer)
    // and every non-"qwait" key.
    public static bool TryParseTotal(string key, out string queue, out string token)
    {
        queue = string.Empty;
        token = string.Empty;

        var parts = key.Split(':');
        if (parts.Length != 3 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        queue = parts[1];
        token = parts[2];

        return true;
    }

    // Parses a latency-histogram bucket key (qwait:{queue}:pct:{upperMs}).
    public static bool TryParsePct(string key, out string queue, out int upperMs)
    {
        queue = string.Empty;
        upperMs = 0;

        var parts = key.Split(':');
        if (parts.Length != 4 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal) || !string.Equals(parts[2], PctMarker, StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out upperMs))
        {
            return false;
        }

        queue = parts[1];

        return true;
    }

    // Parses a per-app total key (qwait-app:{app}:{queue}:{token}). Rejects the per-app history keys (length 6).
    public static bool TryParseApp(string key, out string application, out string queue, out string token)
    {
        application = string.Empty;
        queue = string.Empty;
        token = string.Empty;

        var parts = key.Split(':');
        if (parts.Length != 4 || !string.Equals(parts[0], AppPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        application = parts[1];
        queue = parts[2];
        token = parts[3];

        return true;
    }

    // Parses a tiered history key (qwait:{queue}:hist:{token}:{tier}:{stamp}).
    public static bool TryParseHistory(string key, out string queue, out string token, out MetricTier tier, out DateTime bucket)
    {
        queue = string.Empty;
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

        queue = parts[1];
        token = parts[3];

        return true;
    }

    // Parses a tiered latency-histogram key (qwait:{queue}:pcth:{upperMs}:{tier}:{stamp}).
    public static bool TryParsePctHistory(string key, out string queue, out int upperMs, out MetricTier tier, out DateTime bucket)
    {
        queue = string.Empty;
        upperMs = 0;
        tier = default;
        bucket = default;

        var parts = key.Split(':');
        if (parts.Length != 6 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal) || !string.Equals(parts[2], PctHistoryMarker, StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out upperMs))
        {
            return false;
        }

        if (!MetricTiers.TryParse(parts[4], parts[5], out tier, out bucket))
        {
            return false;
        }

        queue = parts[1];

        return true;
    }

    // Distinct keys cached within one tier bucket; past it keys are built per job, as before.
    private const int MaxCachedKeys = 10_000;

    private static volatile KeyCache _keyCache = new(string.Empty);

    // Every claimed job builds these keys, and they are the same for every job of a queue within a tier
    // bucket, so they are cached per bucket exactly like JobStatsKeys' (the cache starts over when the
    // bucket changes, so it does not grow with time).
    private static string Key(KeyKind kind, string queue, string token, int bucket, string? application, string tierSuffix)
    {
        var cache = _keyCache;
        if (!string.Equals(cache.TierSuffix, tierSuffix, StringComparison.Ordinal))
        {
            cache = new KeyCache(tierSuffix);
            _keyCache = cache;
        }

        var lookup = new KeyLookup(kind, queue, token, bucket, application);
        if (cache.Keys.TryGetValue(lookup, out var key))
        {
            return key;
        }

        var q = Sanitize(queue);
        var app = application is null ? null : Sanitize(application);
        key = kind switch
        {
            KeyKind.Total => Total(q, token),
            KeyKind.History => History(q, token, tierSuffix),
            KeyKind.Pct => Pct(q, bucket),
            KeyKind.PctHistory => PctHistory(q, bucket, tierSuffix),
            KeyKind.AppTotal => AppTotal(app!, q, token),
            _ => AppHistory(app!, q, token, tierSuffix),
        };

        if (cache.Keys.Count < MaxCachedKeys)
        {
            cache.Keys.TryAdd(lookup, key);
        }

        return key;
    }

    private enum KeyKind
    {
        Total = 1,
        History = 2,
        Pct = 3,
        PctHistory = 4,
        AppTotal = 5,
        AppHistory = 6,
    }

    private readonly record struct KeyLookup(KeyKind Kind, string Queue, string Token, int Bucket, string? Application);

    private sealed class KeyCache(string tierSuffix)
    {
        public string TierSuffix { get; } = tierSuffix;

        public ConcurrentDictionary<KeyLookup, string> Keys { get; } = new();
    }
}
