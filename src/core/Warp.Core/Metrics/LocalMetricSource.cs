using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Warp.Core.Adapters;
using Warp.Core.Data.Entities;
using Warp.Core.Services;

namespace Warp.Core.Metrics;

/// <summary>
/// The default <see cref="IMetricSource"/> — reads Warp's own metrics from the durable <c>Statistic</c>/<c>Counter</c>
/// fold. It owns the translation from a logical <see cref="MetricRef"/> (a <see cref="WarpMetricCatalog"/> name +
/// tags) to the colon-delimited storage keys (§8.6/§8.19) and reproduces the existing merged Statistic+Counter read
/// + <see cref="MetricTiers"/> down-bin semantics exactly, so routing a reader through the seam changes no numbers.
/// A later Prometheus backend owns its own <see cref="MetricRef"/>→OTel-name translation independently — no shared
/// mapping table; <see cref="ResolveBaseKey"/> is the local half only. Read-side only, off the worker hot path
/// (§0.2/§6.1).
/// </summary>
internal sealed class LocalMetricSource<TContext> : IMetricSource
    where TContext : DbContext
{
    private const string PctHistoryMarker = ":pcth:";

    private readonly TContext _context;

    public LocalMetricSource(TContext context) => _context = context;

    public async Task<long> GetTotalAsync(MetricRef metric, MetricWindow? window, CancellationToken ct)
    {
        if (window is null)
        {
            // Lifetime total — the exact key (§ combined Statistic + not-yet-folded Counter, as GetCombinedStatValue).
            var key = ResolveBaseKey(metric);
            var aggregated = await _context.Set<Statistic>()
                .Where(x => x.Key == key)
                .Select(x => x.Value)
                .FirstOrDefaultAsync(ct);
            var pending = await _context.Set<Counter>()
                .Where(x => x.Key == key)
                .SumAsync(x => (long)x.Value, ct);

            return aggregated + pending;
        }

        var series = await GetSeriesAsync(
            new SeriesQuery(metric, window.Value, MetricResolution.Hourly, MetricAggregation.Sum), ct);

        return series.Sum(b => b.Value);
    }

    public async Task<IReadOnlyList<SeriesBucket>> GetSeriesAsync(SeriesQuery query, CancellationToken ct)
    {
        var prefix = ResolveBaseKey(query.Metric) + ":";

        var stats = await _context.Set<Statistic>()
            .Where(x => x.Key.StartsWith(prefix))
            .Select(x => new { x.Key, x.Value })
            .ToListAsync(ct);

        var pending = await _context.Set<Counter>()
            .Where(x => x.Key.StartsWith(prefix))
            .GroupBy(x => x.Key)
            .Select(g => new { Key = g.Key, Value = (long)g.Sum(c => c.Value) })
            .ToListAsync(ct);

        // History keys carry the tiered latency histogram too (…:pcth:…); those belong to GetPercentile, not a
        // counter series — exclude them so a latency metric's series isn't polluted by its histogram buckets.
        var acc = new Dictionary<DateTime, long>();
        Fold(stats.Where(s => !s.Key.Contains(PctHistoryMarker, StringComparison.Ordinal)).Select(s => (s.Key, s.Value)));
        Fold(pending.Where(p => !p.Key.Contains(PctHistoryMarker, StringComparison.Ordinal)).Select(p => (p.Key, p.Value)));

        return [.. acc.OrderBy(kv => kv.Key).Select(kv => new SeriesBucket(kv.Key, null, kv.Value))];

        void Fold(IEnumerable<(string Key, long Value)> rows)
        {
            foreach (var (key, value) in rows)
            {
                if (!MetricTiers.TryClassifyKey(key, out _, out _, out var bucketStart)
                    || bucketStart < query.Window.FromUtc || bucketStart >= query.Window.ToUtc)
                {
                    continue;
                }

                var bucket = Truncate(query.Resolution, bucketStart);
                acc[bucket] = query.Aggregation == MetricAggregation.Last ? value : acc.GetValueOrDefault(bucket) + value;
            }
        }
    }

    public async Task<double> GetPercentileAsync(MetricRef metric, int percentile, MetricWindow window, CancellationToken ct)
    {
        // The tiered latency histogram: keys …:pcth:{upperMs}:{tier}:{stamp}. Sum bucket counts across the window,
        // then walk to the requested percentile (overflow bucket reports the last finite bound — the shared
        // display convention, mirroring SloMath.Percentile / the per-surface readers).
        var prefix = ResolveBaseKey(metric) + PctHistoryMarker;

        var stats = await _context.Set<Statistic>()
            .Where(x => x.Key.StartsWith(prefix))
            .Select(x => new { x.Key, x.Value })
            .ToListAsync(ct);
        var pending = await _context.Set<Counter>()
            .Where(x => x.Key.StartsWith(prefix))
            .GroupBy(x => x.Key)
            .Select(g => new { Key = g.Key, Value = (long)g.Sum(c => c.Value) })
            .ToListAsync(ct);

        var buckets = new SortedDictionary<int, long>();
        Collect(stats.Select(s => (s.Key, s.Value)));
        Collect(pending.Select(p => (p.Key, p.Value)));

        var total = buckets.Values.Sum();
        if (total == 0)
        {
            return 0;
        }

        var threshold = (long)Math.Ceiling(Math.Clamp(percentile, 0, 100) / 100.0 * total);
        long cumulative = 0;
        var lastFinite = buckets.Keys.LastOrDefault(b => b != int.MaxValue);
        foreach (var (bound, count) in buckets)
        {
            cumulative += count;
            if (cumulative >= threshold)
            {
                return bound == int.MaxValue ? lastFinite : bound;
            }
        }

        return lastFinite;

        void Collect(IEnumerable<(string Key, long Value)> rows)
        {
            foreach (var (key, value) in rows)
            {
                if (MetricTiers.TryClassifyKey(key, out var baseKey, out _, out var bucketStart)
                    && bucketStart >= window.FromUtc && bucketStart < window.ToUtc
                    && int.TryParse(baseKey[(baseKey.LastIndexOf(':') + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var upperMs))
                {
                    buckets[upperMs] = buckets.GetValueOrDefault(upperMs) + value;
                }
            }
        }
    }

    public async Task<double?> GetGaugeAsync(MetricRef metric, CancellationToken ct)
    {
        var key = ResolveBaseKey(metric);
        var value = await _context.Set<Statistic>()
            .Where(x => x.Key == key)
            .Select(x => (long?)x.Value)
            .FirstOrDefaultAsync(ct);

        return value;
    }

    // The grouped/enumeration reads back the fan-out dashboard tables (per-adapter, per-route, per-queue, …).
    // Each family's reverse parse (colon key → tag values) is wired as that family's reader is routed through the
    // seam (Batch C of the metric-source plan). Families not yet routed throw loudly rather than silently empty.
    public async Task<IReadOnlyList<BreakdownRow>> GetBreakdownAsync(MetricRef metric, IReadOnlyList<string> groupBy, MetricWindow? window, CancellationToken ct)
    {
        var rows = await ParsedRowsAsync(metric, ct);

        // Dimension exclusivity: keep only rows whose tag set is EXACTLY the fixed filter tags plus the groupBy
        // tags — so an adapter's Total-dimension rows never mix with its per-Operation or per-Group rows (which
        // carry an extra tag), and summing a marginal never double-counts a finer one.
        var wanted = WantedKeys(metric, groupBy);

        return
        [
            .. rows
                .Where(r => r.Tags.Count == wanted.Count && r.Tags.Keys.All(wanted.Contains))
                .GroupBy(r => GroupKey(r.Tags, groupBy), StringComparer.Ordinal)
                .Select(g => new BreakdownRow(SubTags(g.First().Tags, groupBy), g.Sum(r => r.Value))),
        ];
    }

    public async Task<IReadOnlyList<PercentileRow>> GetPercentileBreakdownAsync(MetricRef metric, int percentile, IReadOnlyList<string> groupBy, MetricWindow? window, CancellationToken ct)
    {
        // Lifetime latency-histogram buckets, grouped. Adapter's pct is Total-only (per adapter), so groupBy is
        // [] or [adapter]; a request for a finer group returns no rows (no such histogram was written).
        var buckets = await ParsedHistogramAsync(metric, ct);
        var wanted = WantedKeys(metric, groupBy);

        return
        [
            .. buckets
                .Where(b => b.Tags.Count == wanted.Count && b.Tags.Keys.All(wanted.Contains))
                .GroupBy(b => GroupKey(b.Tags, groupBy), StringComparer.Ordinal)
                .Select(g => new PercentileRow(SubTags(g.First().Tags, groupBy), WalkPercentile(g.Select(b => (b.UpperMs, b.Count)), percentile))),
        ];
    }

    public async Task<IReadOnlyList<string>> GetTagValuesAsync(MetricRef metric, string tag, MetricWindow? window, CancellationToken ct)
    {
        var rows = await ParsedRowsAsync(metric, ct);

        return
        [
            .. rows
                .Where(r => r.Tags.ContainsKey(tag))
                .Select(r => r.Tags[tag])
                .Distinct(StringComparer.Ordinal)
                .OrderBy(v => v, StringComparer.Ordinal),
        ];
    }

    private static NotSupportedException NotRoutedYet(MetricRef metric, string method)
        => new($"LocalMetricSource.{method} has no translation for logical metric '{metric.Name}' yet (routed per family in Batch C).");

    // ---- Per-family reverse parse (colon key → logical tags) --------------------------------------------------
    private readonly record struct TaggedRow(Dictionary<string, string> Tags, long Value);

    private readonly record struct HistogramRow(Dictionary<string, string> Tags, int UpperMs, long Count);

    // Scans a family's lifetime count/sum rows under its colon prefix, merges Statistic + not-yet-folded Counter,
    // parses each key into its logical tags, and applies the ref's exact-match tag filter.
    private async Task<List<TaggedRow>> ParsedRowsAsync(MetricRef metric, CancellationToken ct)
    {
        var prefix = ScanPrefix(metric);
        var merged = await MergedAsync(prefix, ct);

        var rows = new List<TaggedRow>();
        foreach (var (key, value) in merged)
        {
            if (ParseRow(metric.Name, key) is { } tags && MatchesFilter(tags, metric.Tags))
            {
                rows.Add(new TaggedRow(tags, value));
            }
        }

        return rows;
    }

    private async Task<List<HistogramRow>> ParsedHistogramAsync(MetricRef metric, CancellationToken ct)
    {
        var prefix = ScanPrefix(metric);
        var merged = await MergedAsync(prefix, ct);

        var rows = new List<HistogramRow>();
        foreach (var (key, value) in merged)
        {
            if (ParseHistogramRow(metric.Name, key) is { } row && MatchesFilter(row.Tags, metric.Tags))
            {
                rows.Add(new HistogramRow(row.Tags, row.UpperMs, value));
            }
        }

        return rows;
    }

    // The colon prefix to scan for a family's rows, narrowed by any fixed identity tag (e.g. a specific adapter)
    // so a detail page never materializes every entity's rows.
    private static string ScanPrefix(MetricRef metric) => metric.Name switch
    {
        WarpMetricCatalog.Names.AdapterCalls or WarpMetricCatalog.Names.AdapterDuration
            => TagOrNull(metric, WarpMetricCatalog.Tags.Adapter) is { } a
                ? $"{AdapterCounterKeys.Prefix}:{a}:"
                : $"{AdapterCounterKeys.Prefix}:",
        _ => throw NotRoutedYet(metric, nameof(ScanPrefix)),
    };

    // Parses one colon count/sum key into its logical tags for the given metric, or null when the key is not a
    // countable row of that metric (e.g. a pct histogram bucket, or the dur-sum row when reading calls).
    private static Dictionary<string, string>? ParseRow(string metricName, string key) => metricName switch
    {
        WarpMetricCatalog.Names.AdapterCalls => ParseAdapterRow(key, wantDuration: false),
        WarpMetricCatalog.Names.AdapterDuration => ParseAdapterRow(key, wantDuration: true),
        _ => throw new NotSupportedException($"No reverse parse for '{metricName}'."),
    };

    private static (Dictionary<string, string> Tags, int UpperMs)? ParseHistogramRow(string metricName, string key) => metricName switch
    {
        WarpMetricCatalog.Names.AdapterDuration when AdapterCounterKeys.TryParsePct(key, out var adapter, out var upperMs)
            => (new Dictionary<string, string>(StringComparer.Ordinal) { [WarpMetricCatalog.Tags.Adapter] = adapter }, upperMs),
        WarpMetricCatalog.Names.AdapterDuration => null,
        _ => throw new NotSupportedException($"No histogram parse for '{metricName}'."),
    };

    // adapter:{a}:{outcome} / :op:{op}:{outcome} / :grp:{grp}:{outcome}. The dur-sum token rides the same keys
    // as the counts; it belongs to adapter.duration (tags without outcome), the outcome counts to adapter.calls.
    private static Dictionary<string, string>? ParseAdapterRow(string key, bool wantDuration)
    {
        if (!AdapterCounterKeys.TryParse(key, out var parsed))
        {
            return null;
        }

        var isDuration = string.Equals(parsed.Outcome, AdapterCounterKeys.DurationToken, StringComparison.Ordinal);
        if (isDuration != wantDuration)
        {
            return null;
        }

        var tags = new Dictionary<string, string>(StringComparer.Ordinal) { [WarpMetricCatalog.Tags.Adapter] = parsed.Adapter };

        switch (parsed.Dimension)
        {
            case AdapterStatDimension.Operation:
                tags[WarpMetricCatalog.Tags.Operation] = parsed.Value;
                break;
            case AdapterStatDimension.Group:
                tags[WarpMetricCatalog.Tags.Group] = parsed.Value;
                break;
        }

        if (!wantDuration)
        {
            tags[WarpMetricCatalog.Tags.Outcome] = parsed.Outcome;
        }

        return tags;
    }

    private static HashSet<string> WantedKeys(MetricRef metric, IReadOnlyList<string> groupBy)
    {
        var wanted = new HashSet<string>(groupBy, StringComparer.Ordinal);
        if (metric.Tags is { } tags)
        {
            foreach (var key in tags.Keys)
            {
                wanted.Add(key);
            }
        }

        return wanted;
    }

    private static bool MatchesFilter(Dictionary<string, string> tags, IReadOnlyDictionary<string, string>? filter)
        => filter is null || filter.All(f => tags.TryGetValue(f.Key, out var v) && string.Equals(v, f.Value, StringComparison.Ordinal));

    private static string GroupKey(Dictionary<string, string> tags, IReadOnlyList<string> groupBy)
        => string.Join("\u001F", groupBy.Select(g => tags[g]));

    private static Dictionary<string, string> SubTags(Dictionary<string, string> tags, IReadOnlyList<string> groupBy)
        => groupBy.ToDictionary(g => g, g => tags[g], StringComparer.Ordinal);

    // Cumulative percentile walk over ascending latency buckets (overflow bucket → last finite bound), matching
    // SloMath.Percentile and the per-surface readers.
    private static double WalkPercentile(IEnumerable<(int UpperMs, long Count)> buckets, int percentile)
    {
        var sorted = new SortedDictionary<int, long>();
        foreach (var (upperMs, count) in buckets)
        {
            sorted[upperMs] = sorted.GetValueOrDefault(upperMs) + count;
        }

        var total = sorted.Values.Sum();
        if (total == 0)
        {
            return 0;
        }

        var threshold = (long)Math.Ceiling(Math.Clamp(percentile, 0, 100) / 100.0 * total);
        var lastFinite = sorted.Keys.LastOrDefault(b => b != int.MaxValue);
        long cumulative = 0;
        foreach (var (bound, count) in sorted)
        {
            cumulative += count;
            if (cumulative >= threshold)
            {
                return bound == int.MaxValue ? lastFinite : bound;
            }
        }

        return lastFinite;
    }

    private async Task<List<(string Key, long Value)>> MergedAsync(string prefix, CancellationToken ct)
    {
        var stats = await _context.Set<Statistic>()
            .Where(x => x.Key.StartsWith(prefix))
            .Select(x => new { x.Key, x.Value })
            .ToListAsync(ct);

        var pending = await _context.Set<Counter>()
            .Where(x => x.Key.StartsWith(prefix))
            .GroupBy(x => x.Key)
            .Select(g => new { Key = g.Key, Value = (long)g.Sum(c => c.Value) })
            .ToListAsync(ct);

        return
        [
            .. stats
                .Concat(pending)
                .GroupBy(x => x.Key, StringComparer.Ordinal)
                .Select(g => (g.Key, g.Sum(x => x.Value))),
        ];
    }

    // Translates a logical MetricRef (a WarpMetricCatalog name + tags) to its colon storage base key — the exact
    // key for a gauge/lifetime total, or the prefix that the tiered scan / pcth walk extends. Mirrors how the
    // matching *Keys.Build wrote the family, so the seam reads exactly what was written. Application is sanitized
    // (as the writers do); the dimension/queue/type is passed through unchanged to match the current SLO/dashboard
    // readers bit-for-bit.
    private static string ResolveBaseKey(MetricRef metric)
        => metric.Name switch
        {
            WarpMetricCatalog.Names.LifecycleSucceeded => "stats:succeeded",
            WarpMetricCatalog.Names.LifecycleFailed => "stats:failed",
            WarpMetricCatalog.Names.LifecycleDeleted => "stats:deleted",
            WarpMetricCatalog.Names.RecordsDropped => $"{DroppedRecordKeys.Prefix}:{Tag(metric, WarpMetricCatalog.Tags.Pipeline)}",
            WarpMetricCatalog.Names.QueueDepth => QueueBacklogKeys.Total(Tag(metric, WarpMetricCatalog.Tags.Queue), QueueBacklogKeys.DepthToken),
            WarpMetricCatalog.Names.QueueOldestAge => QueueBacklogKeys.Total(Tag(metric, WarpMetricCatalog.Tags.Queue), QueueBacklogKeys.OldestAgeToken),
            WarpMetricCatalog.Names.QueueWait => QueueWaitBase(metric),
            WarpMetricCatalog.Names.JobExecution => JobExecutionHistoryBase(metric),
            WarpMetricCatalog.Names.JobExecutionDuration => JobExecutionDurationBase(metric),
            WarpMetricCatalog.Names.Deadline => DeadlineHistoryBase(metric, DeadlineKeys.CountToken),
            WarpMetricCatalog.Names.DeadlineMiss => DeadlineHistoryBase(metric, DeadlineKeys.MissToken),
            _ => throw NotRoutedYet(metric, nameof(ResolveBaseKey)),
        };

    // qwait:{queue} / qwait-app:{app}:{queue} — the base the pcth percentile walk (and count/dur reads) extend.
    private static string QueueWaitBase(MetricRef metric)
    {
        var queue = Tag(metric, WarpMetricCatalog.Tags.Queue);
        var app = TagOrNull(metric, WarpMetricCatalog.Tags.Application);

        return app is null
            ? $"{QueueWaitKeys.Prefix}:{queue}"
            : $"{QueueWaitKeys.AppPrefix}:{QueueWaitKeys.Sanitize(app)}:{queue}";
    }

    // jobstat[-app]:type:{id}:hist:{outcome} — the history base the windowed series sums (per SloEvaluator).
    private static string JobExecutionHistoryBase(MetricRef metric)
    {
        var type = Tag(metric, WarpMetricCatalog.Tags.Type);
        var outcome = Tag(metric, WarpMetricCatalog.Tags.Outcome);
        var app = TagOrNull(metric, WarpMetricCatalog.Tags.Application);

        return app is null
            ? JobStatsKeys.History(JobStatsKeys.TypeMarker, type, outcome, string.Empty)
            : JobStatsKeys.AppHistory(JobStatsKeys.Sanitize(app), JobStatsKeys.TypeMarker, type, outcome, string.Empty);
    }

    // jobstat:type:{id} — the base the pcth percentile walk extends for execution latency.
    private static string JobExecutionDurationBase(MetricRef metric)
    {
        var type = Tag(metric, WarpMetricCatalog.Tags.Type);
        var app = TagOrNull(metric, WarpMetricCatalog.Tags.Application);

        return app is null
            ? $"{JobStatsKeys.Prefix}:{JobStatsKeys.TypeMarker}:{type}"
            : $"{JobStatsKeys.AppPrefix}:{JobStatsKeys.Sanitize(app)}:{JobStatsKeys.TypeMarker}:{type}";
    }

    // deadline[-app]:{type}:hist:{token} — the history base the windowed attainment sums.
    private static string DeadlineHistoryBase(MetricRef metric, string token)
    {
        var type = Tag(metric, WarpMetricCatalog.Tags.Type);
        var app = TagOrNull(metric, WarpMetricCatalog.Tags.Application);

        return app is null
            ? DeadlineKeys.History(type, token, string.Empty)
            : DeadlineKeys.AppHistory(DeadlineKeys.Sanitize(app), type, token, string.Empty);
    }

    private static string Tag(MetricRef metric, string key)
        => TagOrNull(metric, key) ?? throw new InvalidOperationException($"Metric '{metric.Name}' requires tag '{key}'.");

    private static string? TagOrNull(MetricRef metric, string key)
        => metric.Tags is { } tags && tags.TryGetValue(key, out var value) ? value : null;

    private static DateTime Truncate(MetricResolution resolution, DateTime ts) => resolution switch
    {
        MetricResolution.Fine => new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour, ts.Minute / 5 * 5, 0, DateTimeKind.Utc),
        MetricResolution.Hourly => new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour, 0, 0, DateTimeKind.Utc),
        _ => new DateTime(ts.Year, ts.Month, ts.Day, 0, 0, 0, DateTimeKind.Utc),
    };
}
