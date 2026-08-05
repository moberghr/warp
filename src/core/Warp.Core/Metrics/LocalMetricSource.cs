using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Warp.Core.Data.Entities;
using Warp.Core.Services;

namespace Warp.Core.Metrics;

/// <summary>
/// The default <see cref="IMetricSource"/> — reads Warp's own metrics from the durable <c>Statistic</c>/<c>Counter</c>
/// fold. It owns the translation from an abstract <see cref="MetricRef"/> to the colon-delimited storage keys
/// (§8.6/§8.19) and reproduces the existing merged Statistic+Counter read + <see cref="MetricTiers"/> down-bin
/// semantics exactly, so routing a reader through the seam changes no numbers. A later Prometheus backend owns its
/// own <see cref="MetricRef"/>→OTel-name translation independently — no shared mapping table.
///
/// Phase 1: <see cref="MetricRef.Name"/> is the colon base key (e.g. <c>stats:succeeded</c>,
/// <c>warpsys:records-dropped:adapter</c>); the per-family logical→colon mapping is filled in as jobstat/qwait/SLO
/// reads are routed. Read-side only, off the worker hot path (§0.2/§6.1).
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
            var aggregated = await _context.Set<Statistic>().AsNoTracking()
                .Where(x => x.Key == metric.Name).Select(x => x.Value).FirstOrDefaultAsync(ct);
            var pending = await _context.Set<Counter>().AsNoTracking()
                .Where(x => x.Key == metric.Name).SumAsync(x => (long)x.Value, ct);

            return aggregated + pending;
        }

        var series = await GetSeriesAsync(
            new SeriesQuery(metric, window.Value, MetricResolution.Hourly, MetricAggregation.Sum), ct);

        return series.Sum(b => b.Value);
    }

    public async Task<IReadOnlyList<SeriesBucket>> GetSeriesAsync(SeriesQuery query, CancellationToken ct)
    {
        var prefix = query.Metric.Name + ":";

        var stats = await _context.Set<Statistic>().AsNoTracking()
            .Where(x => x.Key.StartsWith(prefix))
            .Select(x => new { x.Key, x.Value })
            .ToListAsync(ct);

        var pending = await _context.Set<Counter>().AsNoTracking()
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
        var prefix = metric.Name + PctHistoryMarker;

        var stats = await _context.Set<Statistic>().AsNoTracking()
            .Where(x => x.Key.StartsWith(prefix)).Select(x => new { x.Key, x.Value }).ToListAsync(ct);
        var pending = await _context.Set<Counter>().AsNoTracking()
            .Where(x => x.Key.StartsWith(prefix)).GroupBy(x => x.Key)
            .Select(g => new { Key = g.Key, Value = (long)g.Sum(c => c.Value) }).ToListAsync(ct);

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
        var value = await _context.Set<Statistic>().AsNoTracking()
            .Where(x => x.Key == metric.Name)
            .Select(x => (long?)x.Value)
            .FirstOrDefaultAsync(ct);

        return value;
    }

    private static DateTime Truncate(MetricResolution resolution, DateTime ts) => resolution switch
    {
        MetricResolution.Fine => new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour, ts.Minute / 5 * 5, 0, DateTimeKind.Utc),
        MetricResolution.Hourly => new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour, 0, 0, DateTimeKind.Utc),
        _ => new DateTime(ts.Year, ts.Month, ts.Day, 0, 0, 0, DateTimeKind.Utc),
    };
}
