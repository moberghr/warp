using System.Globalization;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;

namespace Warp.Core.Services;

/// <summary>
/// Builds and parses the free-form <see cref="Counter"/> keys for per-job-TYPE and per-HANDLER execution
/// statistics (§8.19 / §4-decision-5 multi-app observability). These extend — but never overload — the
/// existing app-agnostic <c>stats:{outcome}</c> job-stats family. They live under their OWN top-level
/// prefixes (<see cref="Prefix"/> <c>"jobstat"</c> / <see cref="AppPrefix"/> <c>"jobstat-app"</c>) which are
/// DISJOINT from <c>stats:</c>:
/// <list type="bullet">
///   <item>The existing readers gate on <c>StartsWith("stats:succeeded:")</c> / <c>StartsWith("stats:failed:")</c>
///     (<c>DashboardStatsService.GetStatsHistory</c>) or exact key equality (<c>GetCombinedStatValue</c>).
///     <c>"jobstat"</c> starts with <c>'j'</c>, so it satisfies neither — an OLD-version deployment reading
///     the shared <see cref="Statistic"/> table can never mis-attribute these to succeeded/failed totals.</item>
///   <item>Conversely the parsers here gate on <c>parts[0] == "jobstat" / "jobstat-app"</c>, so they reject
///     every <c>stats:</c> key — the new reader never picks up the old family.</item>
/// </list>
/// Keys are colon-delimited. The dimension identifier (a job <see cref="Job.Type"/> / <see cref="Job.HandlerType"/>,
/// i.e. an assembly-qualified name — never contains <c>':'</c>) and the application (a low-cardinality config
/// identity) are passed through <see cref="Sanitize"/> so any stray <c>':'</c> is replaced with <c>'-'</c>,
/// GUARANTEEING the key parses unambiguously. Hourly keys end in the <c>yyyy-MM-dd-HH</c> bucket so the generic
/// hourly-stat prune in <c>ExpirationCleanup</c> (<c>HourlyStatisticsRetention</c>, 7 d) sweeps them for free;
/// lifetime totals + latency buckets carry no date suffix and persist. All keys ride the standard
/// <c>Counter → CounterAggregator → Statistic</c> fold — no new aggregation or cleanup machinery.
/// </summary>
internal static class JobStatsKeys
{
    public const string Prefix = "jobstat";

    public const string AppPrefix = "jobstat-app";

    // Dimension markers. The job's Type and (routed-message) HandlerType are the two axes.
    public const string TypeMarker = "type";
    public const string HandlerMarker = "handler";

    // Trailing outcome tokens. Only terminal EXECUTIONS are counted here (succeeded / failed) — a job that
    // fails-then-requeues emits nothing until it settles, so ExecutedCount = succeeded + failed is a clean
    // per-type/handler execution count with a real error-rate denominator. (The lifetime succeeded/failed/
    // deleted/requeued totals stay on the untouched "stats:" family.)
    public const string SucceededToken = "succeeded";
    public const string FailedToken = "failed";

    // Reserved trailing token for the per-dimension duration SUM (ms). Rides the same key layout + fold as
    // the outcome COUNT tokens so average latency (sum ÷ executed count) survives Job-row cleanup. Never an
    // outcome token, so the reader folds it into DurationSum, not the execution Total.
    public const string DurationToken = "dur";

    // Marker for the latency-histogram buckets (jobstat:{dim}:{id}:pct:{upperMs}) — length 5, Total-only.
    public const string PctMarker = "pct";

    // Marker for the hourly time-series buckets (…:hist:{token}:{yyyy-MM-dd-HH}) — the trailing date is what
    // the generic hourly-stat sweep keys on.
    public const string HistoryMarker = "hist";

    // Ascending latency-bucket upper bounds (ms); trailing int.MaxValue is the "> 10000 ms" catch-all. Mirrors
    // AdapterCounterKeys.Buckets so the percentile walk is identical across surfaces. One call increments the
    // ONE bucket whose bound is the smallest >= its rounded ms (BucketFor).
    public static readonly int[] Buckets = [5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000, int.MaxValue];

    public static string Total(string dimension, string id, string token) => $"{Prefix}:{dimension}:{id}:{token}";

    public static string Pct(string dimension, string id, int upperMs) => $"{Prefix}:{dimension}:{id}:{PctMarker}:{upperMs.ToString(CultureInfo.InvariantCulture)}";

    public static string History(string dimension, string id, string token, string hour) => $"{Prefix}:{dimension}:{id}:{HistoryMarker}:{token}:{hour}";

    public static string AppTotal(string application, string dimension, string id, string token) => $"{AppPrefix}:{application}:{dimension}:{id}:{token}";

    public static string AppHistory(string application, string dimension, string id, string token, string hour) => $"{AppPrefix}:{application}:{dimension}:{id}:{HistoryMarker}:{token}:{hour}";

    // The hourly bucket label (UTC) a timestamp falls in — the trailing segment of a history key. Same
    // "yyyy-MM-dd-HH" format the existing job-stats history and the generic hourly-stat cleanup use.
    public static string HourBucket(DateTime timestampUtc) => timestampUtc.ToString("yyyy-MM-dd-HH", CultureInfo.InvariantCulture);

    // Smallest bucket upper bound that is >= the rounded duration. Buckets is ascending and ends in
    // int.MaxValue, so First always matches.
    public static int BucketFor(int durationMs) => Buckets.First(bound => durationMs <= bound);

    // Replaces any stray ':' with '-' so the dimension id / application are GUARANTEED colon-free and the
    // colon-delimited key parses unambiguously. A no-op for real assembly-qualified type names and config
    // identities (which never contain ':'), but a correctness guarantee rather than an assumption.
    public static string Sanitize(string value) => value.Replace(':', '-');

    /// <summary>
    /// Produces every per-type + per-handler execution counter for one finalized job. Counter construction
    /// only — no reads, no orchestration — so it is safe to call on the worker fetch/execute hot path
    /// (§0.2/§6.1). Emits, for the TYPE dimension (and the HANDLER dimension when <see cref="Job.HandlerType"/>
    /// is set): the per-outcome count, a duration-sum + latency-histogram bucket (when a duration is known),
    /// and hourly-bucketed count + duration; plus the same under the per-application slice when
    /// <paramref name="application"/> is set (histogram omitted there to bound volume — application is a
    /// low-cardinality identity, so avg via dur-sum is the floor for that slice).
    /// </summary>
    public static List<Counter> Build(Job job, string outcomeToken, double? durationMs, string? application, string hourBucket)
    {
        var counters = new List<Counter>();

        AppendDimension(counters, TypeMarker, job.Type, outcomeToken, durationMs, application, hourBucket);
        AppendDimension(counters, HandlerMarker, job.HandlerType, outcomeToken, durationMs, application, hourBucket);

        return counters;
    }

    // Parses a lifetime total key (jobstat:{dim}:{id}:{token}). Returns false for pct / history keys (which
    // are longer) and every non-"jobstat" key. token is an outcome (succeeded/failed) or DurationToken.
    public static bool TryParseTotal(string key, out string dimension, out string id, out string token)
    {
        dimension = string.Empty;
        id = string.Empty;
        token = string.Empty;

        var parts = key.Split(':');
        if (parts.Length != 4)
        {
            return false;
        }

        if (!string.Equals(parts[0], Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        dimension = parts[1];
        id = parts[2];
        token = parts[3];

        return true;
    }

    // Parses a latency-histogram bucket key (jobstat:{dim}:{id}:pct:{upperMs}). Disjoint from TryParseTotal
    // (length 5 vs 4) and TryParseHistory (marker "pct" vs "hist").
    public static bool TryParsePct(string key, out string dimension, out string id, out int upperMs)
    {
        dimension = string.Empty;
        id = string.Empty;
        upperMs = 0;

        var parts = key.Split(':');
        if (parts.Length != 5)
        {
            return false;
        }

        if (!string.Equals(parts[0], Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(parts[3], PctMarker, StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out upperMs))
        {
            return false;
        }

        dimension = parts[1];
        id = parts[2];

        return true;
    }

    // Parses an hourly history key (jobstat:{dim}:{id}:hist:{token}:{yyyy-MM-dd-HH}). Disjoint from the
    // lifetime parsers, which reject the "hist" marker / extra segment.
    public static bool TryParseHistory(string key, out string dimension, out string id, out string token, out DateTime hour)
    {
        dimension = string.Empty;
        id = string.Empty;
        token = string.Empty;
        hour = default;

        var parts = key.Split(':');
        if (parts.Length != 6)
        {
            return false;
        }

        if (!string.Equals(parts[0], Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(parts[3], HistoryMarker, StringComparison.Ordinal))
        {
            return false;
        }

        if (!DateTime.TryParseExact(parts[5], "yyyy-MM-dd-HH", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out hour))
        {
            return false;
        }

        dimension = parts[1];
        id = parts[2];
        token = parts[4];

        return true;
    }

    // Parses a per-app total key (jobstat-app:{app}:{dim}:{id}:{token}). Returns false for the per-app
    // history keys (length 7) and every "jobstat:" / "stats:" key.
    public static bool TryParseApp(string key, out string application, out string dimension, out string id, out string token)
    {
        application = string.Empty;
        dimension = string.Empty;
        id = string.Empty;
        token = string.Empty;

        var parts = key.Split(':');
        if (parts.Length != 5)
        {
            return false;
        }

        if (!string.Equals(parts[0], AppPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        application = parts[1];
        dimension = parts[2];
        id = parts[3];
        token = parts[4];

        return true;
    }

    // Parses a per-app hourly history key (jobstat-app:{app}:{dim}:{id}:hist:{token}:{yyyy-MM-dd-HH}).
    // Disjoint counterpart to TryParseApp.
    public static bool TryParseAppHistory(string key, out string application, out string dimension, out string id, out string token, out DateTime hour)
    {
        application = string.Empty;
        dimension = string.Empty;
        id = string.Empty;
        token = string.Empty;
        hour = default;

        var parts = key.Split(':');
        if (parts.Length != 7)
        {
            return false;
        }

        if (!string.Equals(parts[0], AppPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(parts[4], HistoryMarker, StringComparison.Ordinal))
        {
            return false;
        }

        if (!DateTime.TryParseExact(parts[6], "yyyy-MM-dd-HH", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out hour))
        {
            return false;
        }

        application = parts[1];
        dimension = parts[2];
        id = parts[3];
        token = parts[5];

        return true;
    }

    private static void AppendDimension(List<Counter> counters, string dimension, string? rawId, string outcomeToken, double? durationMs, string? application, string hourBucket)
    {
        if (rawId is null)
        {
            return;
        }

        var id = Sanitize(rawId);

        counters.Add(new Counter { Key = Total(dimension, id, outcomeToken), Value = 1 });
        counters.Add(new Counter { Key = History(dimension, id, outcomeToken, hourBucket), Value = 1 });

        if (durationMs.HasValue)
        {
            var durMs = (int)Math.Round(durationMs.Value, MidpointRounding.AwayFromZero);

            counters.Add(new Counter { Key = Total(dimension, id, DurationToken), Value = durMs });
            counters.Add(new Counter { Key = History(dimension, id, DurationToken, hourBucket), Value = durMs });
            counters.Add(new Counter { Key = Pct(dimension, id, BucketFor(durMs)), Value = 1 });
        }

        if (application is null)
        {
            return;
        }

        var app = Sanitize(application);

        counters.Add(new Counter { Key = AppTotal(app, dimension, id, outcomeToken), Value = 1 });
        counters.Add(new Counter { Key = AppHistory(app, dimension, id, outcomeToken, hourBucket), Value = 1 });

        if (durationMs.HasValue)
        {
            var durMs = (int)Math.Round(durationMs.Value, MidpointRounding.AwayFromZero);

            counters.Add(new Counter { Key = AppTotal(app, dimension, id, DurationToken), Value = durMs });
            counters.Add(new Counter { Key = AppHistory(app, dimension, id, DurationToken, hourBucket), Value = durMs });
        }
    }
}
