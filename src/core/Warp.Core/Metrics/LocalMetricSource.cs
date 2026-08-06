using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Warp.Core.Adapters;
using Warp.Core.ClientObservability;
using Warp.Core.Data.Entities;
using Warp.Core.Endpoints;
using Warp.Core.ErrorGrouping;
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
        if (query.Metric.Name is WarpMetricCatalog.Names.AdapterCalls or WarpMetricCatalog.Names.AdapterDuration)
        {
            return await HttpHistorySeriesAsync(query, WarpMetricCatalog.Tags.Adapter, ct);
        }

        if (query.Metric.Name is WarpMetricCatalog.Names.EndpointCalls or WarpMetricCatalog.Names.EndpointDuration)
        {
            return await HttpHistorySeriesAsync(query, WarpMetricCatalog.Tags.Route, ct);
        }

        if (query.Metric.Name is WarpMetricCatalog.Names.ClientEvents)
        {
            return await ClientEventsSeriesAsync(query, ct);
        }

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

        // A percentile landing in the overflow bucket reports the family's largest FINITE ladder bound (the fixed
        // display floor) — NOT the largest bound that happens to be present. When every sample is over the ladder
        // cap the only populated bucket is int.MaxValue, and "largest present finite" would be 0 → the caller reads
        // NoData and a genuinely-breaching latency SLO renders grey. Matches WalkPercentile / SloMath.Percentile.
        var overflowBound = OverflowBound(metric.Name);
        var threshold = (long)Math.Ceiling(Math.Clamp(percentile, 0, 100) / 100.0 * total);
        long cumulative = 0;
        foreach (var (bound, count) in buckets)
        {
            cumulative += count;
            if (cumulative >= threshold)
            {
                return bound == int.MaxValue ? overflowBound : bound;
            }
        }

        return overflowBound;

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
        var rows = await ParsedRowsAsync(metric, groupBy, ct);

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
        // Local reads the LIFETIME pct histogram; it has no windowed grouped-percentile path (the tiered pcth
        // histogram isn't materialized per breakdown dimension). Fail fast rather than silently return lifetime
        // data for a windowed request — the Prometheus backend DOES honor the window, so a caller passing one
        // would get divergent results across backends. No current caller passes a non-null window.
        if (window is not null)
        {
            throw new NotSupportedException(
                "LocalMetricSource.GetPercentileBreakdownAsync does not support a windowed percentile breakdown (it reads the lifetime histogram); pass null for the window.");
        }

        // Lifetime latency-histogram buckets, grouped. Adapter's pct is Total-only (per adapter), so groupBy is
        // [] or [adapter]; a request for a finer group returns no rows (no such histogram was written).
        var buckets = await ParsedHistogramAsync(metric, ct);
        var wanted = WantedKeys(metric, groupBy);
        var overflowBound = OverflowBound(metric.Name);

        return
        [
            .. buckets
                .Where(b => b.Tags.Count == wanted.Count && b.Tags.Keys.All(wanted.Contains))
                .GroupBy(b => GroupKey(b.Tags, groupBy), StringComparer.Ordinal)
                .Select(g => new PercentileRow(SubTags(g.First().Tags, groupBy), WalkPercentile(g.Select(b => (b.UpperMs, b.Count)), percentile, overflowBound))),
        ];
    }

    // The value a percentile landing in the overflow bucket displays — the family's largest FINITE ladder bound
    // (adapter caps at 10 s), matching each per-surface reader's convention.
    private static int OverflowBound(string metricName) => metricName switch
    {
        WarpMetricCatalog.Names.AdapterDuration => AdapterCounterKeys.Buckets[^2],
        WarpMetricCatalog.Names.EndpointDuration => EndpointCounterKeys.Buckets[^2],
        WarpMetricCatalog.Names.ClientVitalsValue => ClientEventKeys.Buckets[^2],
        WarpMetricCatalog.Names.JobExecutionDuration => JobStatsKeys.Buckets[^2],
        WarpMetricCatalog.Names.QueueWait => QueueWaitKeys.Buckets[^2],
        _ => throw new NotSupportedException($"No overflow bound for '{metricName}'."),
    };

    public async Task<IReadOnlyList<string>> GetTagValuesAsync(MetricRef metric, string tag, MetricWindow? window, CancellationToken ct)
    {
        var rows = await ParsedRowsAsync(metric, [tag], ct);

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
    // parses each key into its logical tags, and applies the ref's exact-match tag filter. <paramref name="involved"/>
    // is the query's grouping/enumeration dimensions — when it (or a fixed tag) mentions application, the scan
    // targets the disjoint per-app key family (§8.23) instead of the app-agnostic one.
    private async Task<List<TaggedRow>> ParsedRowsAsync(MetricRef metric, IReadOnlyList<string> involved, CancellationToken ct)
    {
        var prefix = ScanPrefix(metric, involved);
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
        // Latency histograms are app-agnostic only (the per-app slice omits them, §8.19), so never app-scoped.
        var prefix = ScanPrefix(metric, []);
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
    // so a detail page never materializes every entity's rows. When the query concerns application, the disjoint
    // per-app family (adapter-app) is scanned instead of the app-agnostic one.
    private static string ScanPrefix(MetricRef metric, IReadOnlyList<string> involved)
    {
        if (IsClientFamily(metric.Name))
        {
            // Only the per-type event counts have a per-app slice; name / vital families are global.
            var clientApp = TagOrNull(metric, WarpMetricCatalog.Tags.Application);
            if (metric.Name is WarpMetricCatalog.Names.ClientEvents
                && (clientApp is not null || involved.Contains(WarpMetricCatalog.Tags.Application)))
            {
                return clientApp is not null
                    ? $"{ClientEventKeys.AppPrefix}:{ClientEventKeys.Sanitize(clientApp)}:"
                    : $"{ClientEventKeys.AppPrefix}:";
            }

            return $"{ClientEventKeys.Prefix}:";
        }

        if (metric.Name is WarpMetricCatalog.Names.JobExecution or WarpMetricCatalog.Names.JobExecutionDuration)
        {
            return AppScopedPrefix(metric, involved, JobStatsKeys.Prefix, JobStatsKeys.AppPrefix, JobStatsKeys.Sanitize);
        }

        if (metric.Name is WarpMetricCatalog.Names.QueueWait or WarpMetricCatalog.Names.QueueWaitCount)
        {
            return AppScopedPrefix(metric, involved, QueueWaitKeys.Prefix, QueueWaitKeys.AppPrefix, QueueWaitKeys.Sanitize);
        }

        if (metric.Name is WarpMetricCatalog.Names.QueueDepth or WarpMetricCatalog.Names.QueueOldestAge)
        {
            return $"{QueueBacklogKeys.Prefix}:"; // backlog is queue-global (never app-sliced, §8.23)
        }

        var (prefix, appPrefix, idTag) = HttpFamily(metric.Name);
        var app = TagOrNull(metric, WarpMetricCatalog.Tags.Application);

        if (app is not null || involved.Contains(WarpMetricCatalog.Tags.Application))
        {
            return app is not null ? $"{appPrefix}:{SanitizeSegment(app)}:" : $"{appPrefix}:";
        }

        return TagOrNull(metric, idTag) is { } id ? $"{prefix}:{id}:" : $"{prefix}:";
    }

    // The colon prefixes + identity-tag for an HTTP-shaped family (adapter / endpoint). Both share the layout
    // {prefix}:{id}[:op|grp:{v}]:{outcome} plus a disjoint per-app family, so their reads are parameterized here.
    private static (string Prefix, string AppPrefix, string IdTag) HttpFamily(string metricName) => metricName switch
    {
        WarpMetricCatalog.Names.AdapterCalls or WarpMetricCatalog.Names.AdapterDuration
            => (AdapterCounterKeys.Prefix, AdapterCounterKeys.AppPrefix, WarpMetricCatalog.Tags.Adapter),
        WarpMetricCatalog.Names.EndpointCalls or WarpMetricCatalog.Names.EndpointDuration
            => (EndpointCounterKeys.Prefix, EndpointCounterKeys.AppPrefix, WarpMetricCatalog.Tags.Route),
        _ => throw new NotSupportedException($"'{metricName}' is not an HTTP-shaped metric family."),
    };

    private static string SanitizeSegment(string value) => value.Replace(':', '-');

    // Scans a family's app-agnostic prefix, or its disjoint per-app prefix when the query concerns application.
    private static string AppScopedPrefix(MetricRef metric, IReadOnlyList<string> involved, string prefix, string appPrefix, Func<string, string> sanitize)
    {
        var app = TagOrNull(metric, WarpMetricCatalog.Tags.Application);
        if (app is not null || involved.Contains(WarpMetricCatalog.Tags.Application))
        {
            return app is not null ? $"{appPrefix}:{sanitize(app)}:" : $"{appPrefix}:";
        }

        return $"{prefix}:";
    }

    // Parses one colon count/sum key into its logical tags for the given metric, or null when the key is not a
    // countable row of that metric (e.g. a pct histogram bucket, or the dur-sum row when reading calls).
    private static Dictionary<string, string>? ParseRow(string metricName, string key) => metricName switch
    {
        WarpMetricCatalog.Names.AdapterCalls => ParseAdapterRow(key, wantDuration: false),
        WarpMetricCatalog.Names.AdapterDuration => ParseAdapterRow(key, wantDuration: true),
        WarpMetricCatalog.Names.EndpointCalls => ParseEndpointRow(key, wantDuration: false),
        WarpMetricCatalog.Names.EndpointDuration => ParseEndpointRow(key, wantDuration: true),
        WarpMetricCatalog.Names.ClientEvents => ParseClientEventsRow(key),
        WarpMetricCatalog.Names.ClientEventsNamed => ParseClientNamedRow(key),
        WarpMetricCatalog.Names.ClientVitals => ParseClientVitalRow(key, ClientEventKeys.CountToken),
        WarpMetricCatalog.Names.ClientVitalsValue => ParseClientVitalRow(key, ClientEventKeys.DurationToken),
        WarpMetricCatalog.Names.JobExecution => ParseJobstatRow(key, wantDuration: false),
        WarpMetricCatalog.Names.JobExecutionDuration => ParseJobstatRow(key, wantDuration: true),
        WarpMetricCatalog.Names.QueueWaitCount => ParseQwaitRow(key, QueueWaitKeys.CountToken),
        WarpMetricCatalog.Names.QueueWait => ParseQwaitRow(key, QueueWaitKeys.DurationToken),
        WarpMetricCatalog.Names.QueueDepth => ParseBacklogRow(key, QueueBacklogKeys.DepthToken),
        WarpMetricCatalog.Names.QueueOldestAge => ParseBacklogRow(key, QueueBacklogKeys.OldestAgeToken),
        _ => throw new NotSupportedException($"No reverse parse for '{metricName}'."),
    };

    // jobstat:{dim}:{id}:{token} or jobstat-app:{app}:{dim}:{id}:{token}, where dim is the type/handler marker
    // and token is an outcome (succeeded/failed) or the dur sum. The dim marker maps to the type or handler tag.
    private static Dictionary<string, string>? ParseJobstatRow(string key, bool wantDuration)
    {
        Dictionary<string, string> tags;
        string dimension;
        string id;
        string token;

        if (JobStatsKeys.TryParseTotal(key, out var dim, out var jid, out var jtoken))
        {
            tags = new Dictionary<string, string>(StringComparer.Ordinal);
            (dimension, id, token) = (dim, jid, jtoken);
        }
        else if (JobStatsKeys.TryParseApp(key, out var app, out var adim, out var aid, out var atoken))
        {
            tags = new Dictionary<string, string>(StringComparer.Ordinal) { [WarpMetricCatalog.Tags.Application] = app };
            (dimension, id, token) = (adim, aid, atoken);
        }
        else
        {
            return null;
        }

        if (string.Equals(token, JobStatsKeys.DurationToken, StringComparison.Ordinal) != wantDuration)
        {
            return null;
        }

        if (!TrySetJobstatId(tags, dimension, id))
        {
            return null;
        }

        if (!wantDuration)
        {
            tags[WarpMetricCatalog.Tags.Outcome] = token;
        }

        return tags;
    }

    // Maps the jobstat dimension marker (type | handler) to the corresponding logical tag; returns false for any
    // other marker so an unknown dimension is dropped rather than mis-attributed.
    private static bool TrySetJobstatId(Dictionary<string, string> tags, string dimension, string id)
    {
        if (string.Equals(dimension, JobStatsKeys.TypeMarker, StringComparison.Ordinal))
        {
            tags[WarpMetricCatalog.Tags.Type] = id;
            return true;
        }

        if (string.Equals(dimension, JobStatsKeys.HandlerMarker, StringComparison.Ordinal))
        {
            tags[WarpMetricCatalog.Tags.Handler] = id;
            return true;
        }

        return false;
    }

    // qwait:{queue}:{token} or qwait-app:{app}:{queue}:{token}; keeps only the requested count/dur token.
    private static Dictionary<string, string>? ParseQwaitRow(string key, string wantToken)
    {
        if (QueueWaitKeys.TryParseTotal(key, out var queue, out var token) && string.Equals(token, wantToken, StringComparison.Ordinal))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal) { [WarpMetricCatalog.Tags.Queue] = queue };
        }

        if (QueueWaitKeys.TryParseApp(key, out var app, out var appQueue, out var appToken) && string.Equals(appToken, wantToken, StringComparison.Ordinal))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WarpMetricCatalog.Tags.Application] = app,
                [WarpMetricCatalog.Tags.Queue] = appQueue,
            };
        }

        return null;
    }

    // qbacklog:{queue}:{token}; keeps only the requested depth/oldest-age gauge (one UPSERT row per key).
    private static Dictionary<string, string>? ParseBacklogRow(string key, string wantToken)
        => QueueBacklogKeys.TryParseTotal(key, out var queue, out var token) && string.Equals(token, wantToken, StringComparison.Ordinal)
            ? new Dictionary<string, string>(StringComparer.Ordinal) { [WarpMetricCatalog.Tags.Queue] = queue }
            : null;

    private static bool IsClientFamily(string metricName) => metricName is
        WarpMetricCatalog.Names.ClientEvents or WarpMetricCatalog.Names.ClientEventsNamed
        or WarpMetricCatalog.Names.ClientVitals or WarpMetricCatalog.Names.ClientVitalsValue;

    // client.events: per-type count (clientevent:total:{type}:count) or the per-app slice
    // (clientevent-app:{app}:total:{type}:count).
    private static Dictionary<string, string>? ParseClientEventsRow(string key)
    {
        if (ClientEventKeys.TryParseTypeTotal(key, out var type))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal) { [WarpMetricCatalog.Tags.Type] = type };
        }

        if (ClientEventKeys.TryParseAppTypeTotal(key, out var application, out var appType))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WarpMetricCatalog.Tags.Application] = application,
                [WarpMetricCatalog.Tags.Type] = appType,
            };
        }

        return null;
    }

    // client.events.named: per-(type, name) count (clientevent:name:{type}:{name}:count).
    private static Dictionary<string, string>? ParseClientNamedRow(string key)
        => ClientEventKeys.TryParseNameTotal(key, out var type, out var name)
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WarpMetricCatalog.Tags.Type] = type,
                [WarpMetricCatalog.Tags.Name] = name,
            }
            : null;

    // client.vitals(.value): the per-vital count or duration-sum token (clientevent:vital:{vital}:{token}).
    private static Dictionary<string, string>? ParseClientVitalRow(string key, string wantToken)
        => ClientEventKeys.TryParseVital(key, out var vital, out var token) && string.Equals(token, wantToken, StringComparison.Ordinal)
            ? new Dictionary<string, string>(StringComparer.Ordinal) { [WarpMetricCatalog.Tags.Vital] = vital }
            : null;

    private static (Dictionary<string, string> Tags, int UpperMs)? ParseHistogramRow(string metricName, string key) => metricName switch
    {
        WarpMetricCatalog.Names.AdapterDuration when AdapterCounterKeys.TryParsePct(key, out var adapter, out var upperMs)
            => (new Dictionary<string, string>(StringComparer.Ordinal) { [WarpMetricCatalog.Tags.Adapter] = adapter }, upperMs),
        WarpMetricCatalog.Names.AdapterDuration => null,
        WarpMetricCatalog.Names.EndpointDuration when EndpointCounterKeys.TryParsePct(key, out var route, out var upperMs)
            => (new Dictionary<string, string>(StringComparer.Ordinal) { [WarpMetricCatalog.Tags.Route] = route }, upperMs),
        WarpMetricCatalog.Names.EndpointDuration => null,
        WarpMetricCatalog.Names.ClientVitalsValue when ClientEventKeys.TryParseVitalPct(key, out var vital, out var upperMs)
            => (new Dictionary<string, string>(StringComparer.Ordinal) { [WarpMetricCatalog.Tags.Vital] = vital }, upperMs),
        WarpMetricCatalog.Names.ClientVitalsValue => null,
        WarpMetricCatalog.Names.JobExecutionDuration when JobStatsKeys.TryParsePct(key, out var dim, out var id, out var upperMs) && TrySetJobstatId(new Dictionary<string, string>(StringComparer.Ordinal), dim, id)
            => (JobstatIdTags(dim, id), upperMs),
        WarpMetricCatalog.Names.JobExecutionDuration => null,
        WarpMetricCatalog.Names.QueueWait when QueueWaitKeys.TryParsePct(key, out var queue, out var upperMs)
            => (new Dictionary<string, string>(StringComparer.Ordinal) { [WarpMetricCatalog.Tags.Queue] = queue }, upperMs),
        WarpMetricCatalog.Names.QueueWait => null,
        _ => throw new NotSupportedException($"No histogram parse for '{metricName}'."),
    };

    // The type/handler identity tag for a jobstat pct histogram bucket.
    private static Dictionary<string, string> JobstatIdTags(string dimension, string id)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        TrySetJobstatId(tags, dimension, id);

        return tags;
    }

    // App-agnostic adapter:{a}:{outcome} / :op:{op}:{outcome} / :grp:{grp}:{outcome}, or the per-app
    // adapter-app:{app}:{a}:{outcome} (no op/grp materialization there). The key's own first segment disambiguates
    // the two families. The dur-sum token rides the same keys as the counts; it belongs to adapter.duration (tags
    // without outcome), the outcome counts to adapter.calls.
    private static Dictionary<string, string>? ParseAdapterRow(string key, bool wantDuration)
    {
        Dictionary<string, string> tags;
        string outcome;

        if (AdapterCounterKeys.TryParse(key, out var parsed))
        {
            tags = new Dictionary<string, string>(StringComparer.Ordinal) { [WarpMetricCatalog.Tags.Adapter] = parsed.Adapter };
            switch (parsed.Dimension)
            {
                case AdapterStatDimension.Operation:
                    tags[WarpMetricCatalog.Tags.Operation] = parsed.Value;
                    break;
                case AdapterStatDimension.Group:
                    tags[WarpMetricCatalog.Tags.Group] = parsed.Value;
                    break;
            }

            outcome = parsed.Outcome;
        }
        else if (AdapterCounterKeys.TryParseApp(key, out var application, out var appAdapter, out var appOutcome))
        {
            tags = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WarpMetricCatalog.Tags.Application] = application,
                [WarpMetricCatalog.Tags.Adapter] = appAdapter,
            };
            outcome = appOutcome;
        }
        else
        {
            return null;
        }

        var isDuration = string.Equals(outcome, AdapterCounterKeys.DurationToken, StringComparison.Ordinal);
        if (isDuration != wantDuration)
        {
            return null;
        }

        if (!wantDuration)
        {
            tags[WarpMetricCatalog.Tags.Outcome] = outcome;
        }

        return tags;
    }

    // endpoint:{route}:{outcome} / :grp:{group}:{outcome}, or the per-app endpoint-app:{app}:{route}:{outcome}
    // (no group materialization there). Mirrors ParseAdapterRow; endpoints have no per-operation dimension.
    private static Dictionary<string, string>? ParseEndpointRow(string key, bool wantDuration)
    {
        Dictionary<string, string> tags;
        string outcome;

        if (EndpointCounterKeys.TryParse(key, out var parsed))
        {
            tags = new Dictionary<string, string>(StringComparer.Ordinal) { [WarpMetricCatalog.Tags.Route] = parsed.Route };
            if (parsed.Dimension == EndpointStatDimension.Group)
            {
                tags[WarpMetricCatalog.Tags.Group] = parsed.Group;
            }

            outcome = parsed.Outcome;
        }
        else if (EndpointCounterKeys.TryParseApp(key, out var application, out var appRoute, out var appOutcome))
        {
            tags = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WarpMetricCatalog.Tags.Application] = application,
                [WarpMetricCatalog.Tags.Route] = appRoute,
            };
            outcome = appOutcome;
        }
        else
        {
            return null;
        }

        var isDuration = string.Equals(outcome, EndpointCounterKeys.DurationToken, StringComparison.Ordinal);
        if (isDuration != wantDuration)
        {
            return null;
        }

        if (!wantDuration)
        {
            tags[WarpMetricCatalog.Tags.Outcome] = outcome;
        }

        return tags;
    }

    // HTTP-family hourly history ({prefix}:{id}:hist:{outcome}:{bucket}, legacy-hourly or rolled tiers). Reads the
    // count-outcome rows for the calls metric (optionally split by outcome) or the dur-sum rows for the duration
    // metric, down-binned to the query resolution over its window. History is app-agnostic. <paramref name="idTag"/>
    // is the family's identity tag (adapter | route).
    private async Task<IReadOnlyList<SeriesBucket>> HttpHistorySeriesAsync(SeriesQuery query, string idTag, CancellationToken ct)
    {
        var metric = query.Metric;
        var wantDuration = metric.Name is WarpMetricCatalog.Names.AdapterDuration or WarpMetricCatalog.Names.EndpointDuration;
        var idFilter = TagOrNull(metric, idTag);
        var merged = await MergedAsync(ScanPrefix(metric, []), ct);

        var acc = new Dictionary<(DateTime Bucket, string? Tag), long>();
        foreach (var (key, value) in merged)
        {
            if (!MetricTiers.TryClassifyKey(key, out var baseKey, out _, out var bucketStart)
                || bucketStart < query.Window.FromUtc || bucketStart >= query.Window.ToUtc)
            {
                continue;
            }

            // baseKey is {prefix}:{id}:hist:{outcome} (tier/date suffix stripped by TryClassifyKey). Both HTTP
            // families share the "hist" marker and "dur" duration token.
            var parts = baseKey.Split(':');
            if (parts.Length != 4 || !string.Equals(parts[2], AdapterCounterKeys.HistoryMarker, StringComparison.Ordinal))
            {
                continue;
            }

            var id = parts[1];
            var outcome = parts[3];
            if (string.Equals(outcome, AdapterCounterKeys.DurationToken, StringComparison.Ordinal) != wantDuration)
            {
                continue;
            }

            if (idFilter is not null && !string.Equals(id, idFilter, StringComparison.Ordinal))
            {
                continue;
            }

            var tagValue = query.BreakdownBy switch
            {
                WarpMetricCatalog.Tags.Outcome => outcome,
                _ when string.Equals(query.BreakdownBy, idTag, StringComparison.Ordinal) => id,
                _ => (string?)null,
            };

            var bucket = Truncate(query.Resolution, bucketStart);
            var accKey = (bucket, tagValue);
            acc[accKey] = query.Aggregation == MetricAggregation.Last ? value : acc.GetValueOrDefault(accKey) + value;
        }

        return
        [
            .. acc
                .OrderBy(kv => kv.Key.Bucket)
                .ThenBy(kv => kv.Key.Tag, StringComparer.Ordinal)
                .Select(kv => new SeriesBucket(kv.Key.Bucket, kv.Key.Tag, kv.Value)),
        ];
    }

    // client.events hourly history (clientevent:total:{type}:hist:{bucket}), optionally split by type. Global only
    // (the per-app slice carries type totals, not history), down-binned to the query resolution over its window.
    private async Task<IReadOnlyList<SeriesBucket>> ClientEventsSeriesAsync(SeriesQuery query, CancellationToken ct)
    {
        var merged = await MergedAsync($"{ClientEventKeys.Prefix}:", ct);

        var acc = new Dictionary<(DateTime Bucket, string? Tag), long>();
        foreach (var (key, value) in merged)
        {
            if (!MetricTiers.TryClassifyKey(key, out var baseKey, out _, out var bucketStart)
                || bucketStart < query.Window.FromUtc || bucketStart >= query.Window.ToUtc)
            {
                continue;
            }

            // baseKey is clientevent:total:{type}:hist (tier/date suffix stripped by TryClassifyKey).
            var parts = baseKey.Split(':');
            if (parts.Length != 4
                || !string.Equals(parts[1], ClientEventKeys.TotalMarker, StringComparison.Ordinal)
                || !string.Equals(parts[3], ClientEventKeys.HistoryMarker, StringComparison.Ordinal))
            {
                continue;
            }

            var type = parts[2];
            var tagValue = string.Equals(query.BreakdownBy, WarpMetricCatalog.Tags.Type, StringComparison.Ordinal) ? type : null;
            var bucket = Truncate(query.Resolution, bucketStart);
            var accKey = (bucket, tagValue);
            acc[accKey] = query.Aggregation == MetricAggregation.Last ? value : acc.GetValueOrDefault(accKey) + value;
        }

        return
        [
            .. acc
                .OrderBy(kv => kv.Key.Bucket)
                .ThenBy(kv => kv.Key.Tag, StringComparer.Ordinal)
                .Select(kv => new SeriesBucket(kv.Key.Bucket, kv.Key.Tag, kv.Value)),
        ];
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

    // Cumulative percentile walk over ascending latency buckets. A percentile landing in the overflow bucket
    // (int.MaxValue) reports <paramref name="overflowBound"/> — the family's largest finite ladder bound — as the
    // displayable floor, matching the per-surface readers (e.g. AdapterQueryService.Quantile).
    private static double WalkPercentile(IEnumerable<(int UpperMs, long Count)> buckets, int percentile, int overflowBound)
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
        long cumulative = 0;
        foreach (var (bound, count) in sorted)
        {
            cumulative += count;
            if (cumulative >= threshold)
            {
                return bound == int.MaxValue ? overflowBound : bound;
            }
        }

        return overflowBound;
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
            WarpMetricCatalog.Names.ErrorGroupOccurrences => $"{ErrorGroupKeys.Prefix}:{Tag(metric, WarpMetricCatalog.Tags.Fingerprint)}",
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
