using Warp.Core.Metrics;

namespace Warp.Tests.Metrics;

/// <summary>
/// In-memory <see cref="IMetricSource"/> for tests — a controllable, backend-neutral stand-in that pins the
/// contract's semantics (tag filtering, resolution bucketing, breakdown grouping, percentile, gauge). Used both to
/// pin the contract (NoDb) and to drive consumer tests (dashboard / SLO evaluator) against a non-local source,
/// proving the read side is genuinely backend-agnostic.
/// </summary>
internal sealed class FakeMetricSource : IMetricSource
{
    private readonly record struct Sample(IReadOnlyDictionary<string, string> Tags, DateTime Ts, long Value);

    private readonly record struct Latency(IReadOnlyDictionary<string, string> Tags, int Ms);

    private readonly Dictionary<string, List<Sample>> _counters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Latency>> _latencies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _gauges = new(StringComparer.Ordinal);

    public FakeMetricSource Add(string name, DateTime tsUtc, long value, params (string Key, string Value)[] tags)
    {
        Bucket(_counters, name).Add(new Sample(ToTags(tags), tsUtc, value));

        return this;
    }

    public FakeMetricSource AddLatency(string name, int ms, params (string Key, string Value)[] tags)
    {
        Bucket(_latencies, name).Add(new Latency(ToTags(tags), ms));

        return this;
    }

    public FakeMetricSource SetGauge(string name, double value)
    {
        _gauges[name] = value;

        return this;
    }

    public Task<long> GetTotalAsync(MetricRef metric, MetricWindow? window, CancellationToken ct)
    {
        var total = Samples(metric)
            .Where(s => window is not { } w || (s.Ts >= w.FromUtc && s.Ts < w.ToUtc))
            .Sum(s => s.Value);

        return Task.FromResult(total);
    }

    public Task<IReadOnlyList<SeriesBucket>> GetSeriesAsync(SeriesQuery query, CancellationToken ct)
    {
        var inWindow = Samples(query.Metric)
            .Where(s => s.Ts >= query.Window.FromUtc && s.Ts < query.Window.ToUtc);

        var grouped = inWindow
            .GroupBy(s => (Bucket: Truncate(query.Resolution, s.Ts), Tag: query.BreakdownBy is null ? null : Tag(s.Tags, query.BreakdownBy)))
            .Select(g =>
            {
                var value = query.Aggregation == MetricAggregation.Last
                    ? g.MaxBy(s => s.Ts).Value
                    : g.Sum(s => s.Value);

                return new SeriesBucket(g.Key.Bucket, g.Key.Tag, value);
            })
            .OrderBy(b => b.BucketStart)
            .ThenBy(b => b.TagValue, StringComparer.Ordinal)
            .ToList();

        return Task.FromResult<IReadOnlyList<SeriesBucket>>(grouped);
    }

    public Task<double> GetPercentileAsync(MetricRef metric, int percentile, MetricWindow window, CancellationToken ct)
    {
        var samples = (_latencies.TryGetValue(metric.Name, out var list) ? list : [])
            .Where(l => Matches(l.Tags, metric.Tags))
            .Select(l => l.Ms)
            .Order()
            .ToList();

        if (samples.Count == 0)
        {
            return Task.FromResult(0d);
        }

        // Nearest-rank: the smallest value whose rank reaches ceil(p/100 * n).
        var rank = (int)Math.Ceiling(Math.Clamp(percentile, 0, 100) / 100.0 * samples.Count);
        var index = Math.Clamp(rank - 1, 0, samples.Count - 1);

        return Task.FromResult((double)samples[index]);
    }

    public Task<double?> GetGaugeAsync(MetricRef metric, CancellationToken ct)
        => Task.FromResult(_gauges.TryGetValue(metric.Name, out var v) ? v : (double?)null);

    public Task<IReadOnlyList<BreakdownRow>> GetBreakdownAsync(MetricRef metric, IReadOnlyList<string> groupBy, MetricWindow? window, CancellationToken ct)
    {
        var rows = Samples(metric)
            .Where(s => window is not { } w || (s.Ts >= w.FromUtc && s.Ts < w.ToUtc))
            .GroupBy(s => GroupKey(s.Tags, groupBy), StringComparer.Ordinal)
            .Select(g => new BreakdownRow(GroupTags(g.First().Tags, groupBy), g.Sum(s => s.Value)))
            .OrderBy(r => GroupKey(r.Tags, groupBy), StringComparer.Ordinal)
            .ToList();

        return Task.FromResult<IReadOnlyList<BreakdownRow>>(rows);
    }

    public Task<IReadOnlyList<PercentileRow>> GetPercentileBreakdownAsync(MetricRef metric, int percentile, IReadOnlyList<string> groupBy, MetricWindow? window, CancellationToken ct)
    {
        var rows = (_latencies.TryGetValue(metric.Name, out var list) ? list : [])
            .Where(l => Matches(l.Tags, metric.Tags))
            .GroupBy(l => GroupKey(l.Tags, groupBy), StringComparer.Ordinal)
            .Select(g => new PercentileRow(GroupTags(g.First().Tags, groupBy), NearestRank(g.Select(l => l.Ms), percentile)))
            .OrderBy(r => GroupKey(r.Tags, groupBy), StringComparer.Ordinal)
            .ToList();

        return Task.FromResult<IReadOnlyList<PercentileRow>>(rows);
    }

    public Task<IReadOnlyList<string>> GetTagValuesAsync(MetricRef metric, string tag, MetricWindow? window, CancellationToken ct)
    {
        var fromCounters = Samples(metric)
            .Where(s => window is not { } w || (s.Ts >= w.FromUtc && s.Ts < w.ToUtc))
            .Select(s => Tag(s.Tags, tag));

        var fromLatencies = (_latencies.TryGetValue(metric.Name, out var list) ? list : [])
            .Where(l => Matches(l.Tags, metric.Tags))
            .Select(l => Tag(l.Tags, tag));

        var values = fromCounters
            .Concat(fromLatencies)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(values);
    }

    private static double NearestRank(IEnumerable<int> values, int percentile)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count == 0)
        {
            return 0;
        }

        var rank = (int)Math.Ceiling(Math.Clamp(percentile, 0, 100) / 100.0 * sorted.Count);

        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }

    // A stable, value-based grouping key (GroupBy on a Dictionary would use reference equality). The Unit
    // Separator between tag values keeps distinct combinations from colliding.
    private static string GroupKey(IReadOnlyDictionary<string, string> tags, IReadOnlyList<string> groupBy)
        => string.Join("", groupBy.Select(t => Tag(tags, t)));

    private static Dictionary<string, string> GroupTags(IReadOnlyDictionary<string, string> tags, IReadOnlyList<string> groupBy)
        => groupBy.ToDictionary(t => t, t => Tag(tags, t), StringComparer.Ordinal);

    private IEnumerable<Sample> Samples(MetricRef metric)
        => (_counters.TryGetValue(metric.Name, out var list) ? list : []).Where(s => Matches(s.Tags, metric.Tags));

    private static List<T> Bucket<T>(Dictionary<string, List<T>> map, string name)
    {
        if (!map.TryGetValue(name, out var list))
        {
            list = [];
            map[name] = list;
        }

        return list;
    }

    private static Dictionary<string, string> ToTags((string Key, string Value)[] tags)
        => tags.ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal);

    private static bool Matches(IReadOnlyDictionary<string, string> sampleTags, IReadOnlyDictionary<string, string>? filter)
        => filter is null || filter.All(f => sampleTags.TryGetValue(f.Key, out var v) && string.Equals(v, f.Value, StringComparison.Ordinal));

    private static string Tag(IReadOnlyDictionary<string, string> tags, string key)
        => tags.TryGetValue(key, out var v) ? v : "{none}";

    private static DateTime Truncate(MetricResolution resolution, DateTime ts) => resolution switch
    {
        MetricResolution.Fine => new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour, ts.Minute / 5 * 5, 0, DateTimeKind.Utc),
        MetricResolution.Hourly => new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour, 0, 0, DateTimeKind.Utc),
        _ => new DateTime(ts.Year, ts.Month, ts.Day, 0, 0, 0, DateTimeKind.Utc),
    };
}
