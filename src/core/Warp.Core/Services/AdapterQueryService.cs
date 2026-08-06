using Microsoft.EntityFrameworkCore;
using Warp.Core.Adapters;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Metrics;
using static Warp.Core.Metrics.WarpMetricCatalog;

namespace Warp.Core.Services;

/// <summary>
/// <see cref="IAdapterQueryService"/> over the user's <typeparamref name="TContext"/>. Counts and error
/// rates are read from the merged <see cref="Statistic"/> + pending <see cref="Counter"/> rows (the same
/// merge the dashboard metric cards use) so successes are always counted regardless of the adapter's
/// <c>RecordCalls</c> setting; average latency and last-failure timestamps are read from the retained
/// <see cref="AdapterCallLog"/> rows. Counter keys follow the <see cref="AdapterCounterKeys"/> layout.
/// </summary>
public class AdapterQueryService<TContext> : IAdapterQueryService
    where TContext : DbContext
{
    // Cap the recent-calls list so a hot adapter's detail page stays bounded; older rows remain
    // reachable via retention windows / OTel, not this list.
    private const int RecentCallsLimit = 100;

    private readonly TContext _context;
    private readonly IMetricSource _metrics;

    public AdapterQueryService(TContext context, IMetricSource metrics)
    {
        _context = context;
        _metrics = metrics;
    }

    public async Task<IReadOnlyList<AdapterListItemModel>> GetAdapters(CancellationToken ct = default)
    {
        var definitions = await _context.Set<AdapterDefinition>()
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(x =>
                new
                {
                    x.Name,
                    x.ConfigSummary,
                    x.FirstSeenAt,
                    x.LastSeenAt,
                    x.HasPolicyConflict,
                })
            .ToListAsync(ct);

        if (definitions.Count == 0)
        {
            return [];
        }

        var stats = await LoadStatsAsync(ct);

        var result = new List<AdapterListItemModel>(definitions.Count);

        foreach (var definition in definitions)
        {
            var totals = stats.Totals.GetValueOrDefault(definition.Name);

            result.Add(new AdapterListItemModel
            {
                Name = definition.Name,
                ConfigSummary = definition.ConfigSummary,
                FirstSeenAt = definition.FirstSeenAt,
                LastSeenAt = definition.LastSeenAt,
                TotalCalls = totals?.Total ?? 0,
                ErrorCount = totals?.Errors ?? 0,
                ErrorRate = Rate(totals),
                AvgDurationMs = totals?.AvgDurationMs ?? 0,
                HasPolicyConflict = definition.HasPolicyConflict,
            });
        }

        return result;
    }

    public async Task<AdapterDetailModel?> GetAdapterDetail(string name, CancellationToken ct = default)
    {
        var definition = await _context.Set<AdapterDefinition>()
            .AsNoTracking()
            .Where(x => x.Name == name)
            .Select(x =>
                new
                {
                    x.Name,
                    x.ConfigSummary,
                    x.GroupLabel,
                    x.FirstSeenAt,
                    x.LastSeenAt,
                    x.HasPolicyConflict,
                })
            .FirstOrDefaultAsync(ct);

        if (definition is null)
        {
            return null;
        }

        var stats = await LoadStatsAsync(ct, name);

        var totals = stats.Totals.GetValueOrDefault(name);

        var durationRef = new MetricRef(Names.AdapterDuration, new Dictionary<string, string> { [Tags.Adapter] = name });
        var p90 = await PercentileAsync(durationRef, 90, ct);
        var p95 = await PercentileAsync(durationRef, 95, ct);
        var p99 = await PercentileAsync(durationRef, 99, ct);

        // Average latency comes from the duration-sum ÷ count aggregates (survives AdapterCallLog deletion),
        // not the raw rows. Last-failure timestamps and the recent-calls list below still read raw rows —
        // they degrade gracefully to null/empty once logs are swept.
        var groupFailures = await _context.Set<AdapterCallLog>()
            .AsNoTracking()
            .Where(x => x.AdapterName == name)
            .Where(x => x.GroupName != null)
            .Where(x => x.Outcome != AdapterCallOutcome.Success)
            .GroupBy(x => x.GroupName!)
            .Select(g =>
                new
                {
                    Group = g.Key,
                    LastFailureAt = g.Max(x => x.Timestamp),
                })
            .ToListAsync(ct);

        var groupFailureByKey = groupFailures.ToDictionary(x => x.Group, x => x.LastFailureAt, StringComparer.Ordinal);

        var recentCalls = await _context.Set<AdapterCallLog>()
            .AsNoTracking()
            .Where(x => x.AdapterName == name)
            .OrderByDescending(x => x.Timestamp)
            .ThenByDescending(x => x.Id)
            .Take(RecentCallsLimit)
            .Select(x =>
                new AdapterCallSummaryModel
                {
                    Id = x.Id,
                    Operation = x.Operation,
                    GroupName = x.GroupName,
                    Timestamp = x.Timestamp,
                    DurationMs = x.DurationMs,
                    Attempts = x.Attempts,
                    Outcome = x.Outcome,
                    StatusCode = x.StatusCode,
                    CorrelationId = x.CorrelationId,
                    TagsJson = x.TagsJson,
                })
            .ToListAsync(ct);

        var operations = stats.Operations
            .Where(x => string.Equals(x.Key.Adapter, name, StringComparison.Ordinal))
            .OrderBy(x => x.Key.Value, StringComparer.Ordinal)
            .Select(x =>
                new AdapterOperationStatModel
                {
                    Operation = x.Key.Value,
                    Calls = x.Value.Total,
                    Errors = x.Value.Errors,
                    ErrorRate = Rate(x.Value),
                    AvgDurationMs = x.Value.AvgDurationMs,
                })
            .ToList();

        var groups = stats.Groups
            .Where(x => string.Equals(x.Key.Adapter, name, StringComparison.Ordinal))
            .OrderBy(x => x.Key.Value, StringComparer.Ordinal)
            .Select(x =>
                new AdapterGroupStatModel
                {
                    Group = x.Key.Value,
                    Calls = x.Value.Total,
                    Errors = x.Value.Errors,
                    ErrorRate = Rate(x.Value),
                    AvgDurationMs = x.Value.AvgDurationMs,
                    LastFailureAt = groupFailureByKey.TryGetValue(x.Key.Value, out var last) ? last : null,
                })
            .ToList();

        return new AdapterDetailModel
        {
            Name = definition.Name,
            ConfigSummary = definition.ConfigSummary,
            FirstSeenAt = definition.FirstSeenAt,
            LastSeenAt = definition.LastSeenAt,
            HasPolicyConflict = definition.HasPolicyConflict,
            GroupLabel = definition.GroupLabel ?? "Group",
            TotalCalls = totals?.Total ?? 0,
            ErrorCount = totals?.Errors ?? 0,
            ErrorRate = Rate(totals),
            AvgDurationMs = totals?.AvgDurationMs ?? 0,
            P90DurationMs = p90,
            P95DurationMs = p95,
            P99DurationMs = p99,
            Operations = operations,
            Groups = groups,
            RecentCalls = recentCalls,
            History = await LoadHistoryAsync(name, ct),
        };
    }

    public async Task<AdapterCallDetailModel?> GetCallDetail(string name, Guid callId, CancellationToken ct = default)
    {
        return await _context.Set<AdapterCallLog>()
            .AsNoTracking()
            .Where(x => x.AdapterName == name)
            .Where(x => x.Id == callId)
            .Select(x =>
                new AdapterCallDetailModel
                {
                    Id = x.Id,
                    AdapterName = x.AdapterName,
                    Operation = x.Operation,
                    GroupName = x.GroupName,
                    Timestamp = x.Timestamp,
                    DurationMs = x.DurationMs,
                    Attempts = x.Attempts,
                    Outcome = x.Outcome,
                    StatusCode = x.StatusCode,
                    ExceptionType = x.ExceptionType,
                    ExceptionMessage = x.ExceptionMessage,
                    RequestSummary = x.RequestSummary,
                    RequestHeaders = x.RequestHeaders,
                    ResponseHeaders = x.ResponseHeaders,
                    RequestBody = x.RequestBody,
                    ResponseBody = x.ResponseBody,
                    MachineName = x.MachineName,
                    TraceId = x.TraceId,
                    TagsJson = x.TagsJson,
                    CorrelationId = x.CorrelationId,
                })
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<AdapterHistoryPointModel>> GetGlobalHistory(CancellationToken ct = default)
    {
        var buckets = await LoadHistoryBucketsAsync(name: null, ct);

        return ProjectHistory(buckets);
    }

    public async Task<IReadOnlyList<string>> GetApplications(CancellationToken ct = default)
    {
        var stats = await LoadAppStatsAsync(application: null, ct);

        return
        [
            .. stats.Keys
                .Select(x => x.Application)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal),
        ];
    }

    public async Task<IReadOnlyList<AdapterAppStatModel>> GetAdapterStatsByApplication(string application, CancellationToken ct = default)
    {
        // Sanitize with the SAME transform the write side (AdapterCounterKeys.AppTotal/AppHistory) applies,
        // BEFORE building the prefix filter — otherwise a colon-bearing app name would query a prefix that
        // never matches (the keys store the sanitized form) and silently return empty.
        var sanitized = AdapterCounterKeys.SanitizeApplication(application);

        var stats = await LoadAppStatsAsync(sanitized, ct);

        return
        [
            .. stats
                .Where(x => string.Equals(x.Key.Application, sanitized, StringComparison.Ordinal))
                .OrderBy(x => x.Key.Adapter, StringComparer.Ordinal)
                .Select(x =>
                    new AdapterAppStatModel
                    {
                        Application = x.Key.Application,
                        Adapter = x.Key.Adapter,
                        Calls = x.Value.Total,
                        Errors = x.Value.Errors,
                        ErrorRate = Rate(x.Value),
                        AvgDurationMs = x.Value.AvgDurationMs,
                    }),
        ];
    }

    // Reads the disjoint per-app total keys (Statistic + not-yet-collapsed Counter rows) and folds each
    // into its (application, adapter) outcome bucket. A null application loads every app's keys
    // ("adapter-app:"); a supplied one scopes to that app ("adapter-app:{app}:") — application is
    // colon-free (config identity), so the prefix is exact. Per-app history keys ride the same prefix but
    // are length-6 and are simply skipped by TryParseApp (they power charts read elsewhere, not these
    // lifetime aggregates).
    private async Task<Dictionary<AppAdapterKey, OutcomeCounts>> LoadAppStatsAsync(string? application, CancellationToken ct)
    {
        var map = new Dictionary<AppAdapterKey, OutcomeCounts>();
        var scope = application is null ? null : new Dictionary<string, string> { [Tags.Application] = application };
        var calls = new MetricRef(Names.AdapterCalls, scope);
        var duration = new MetricRef(Names.AdapterDuration, scope);

        foreach (var row in await _metrics.GetBreakdownAsync(calls, [Tags.Application, Tags.Adapter, Tags.Outcome], null, ct))
        {
            Bucket(map, new AppAdapterKey(row.Tags[Tags.Application], row.Tags[Tags.Adapter])).Add(row.Tags[Tags.Outcome], row.Value);
        }

        foreach (var row in await _metrics.GetBreakdownAsync(duration, [Tags.Application, Tags.Adapter], null, ct))
        {
            Bucket(map, new AppAdapterKey(row.Tags[Tags.Application], row.Tags[Tags.Adapter])).Add(AdapterCounterKeys.DurationToken, row.Value);
        }

        return map;
    }

    // Builds the hourly performance time-series for one adapter from the durable hourly history buckets,
    // oldest first. Bounded by the 7-day hourly-stat retention; hours with no traffic simply don't exist.
    private async Task<List<AdapterHistoryPointModel>> LoadHistoryAsync(string name, CancellationToken ct)
    {
        var buckets = await LoadHistoryBucketsAsync(name, ct);

        return ProjectHistory(buckets);
    }

    // Reads the durable hourly history counters (Statistic plus the not-yet-collapsed Counter rows, so the
    // current hour is not missing) and folds them per hour. A null name aggregates across every adapter for
    // the global overview; a supplied name scopes to that one. The scoped read uses a prefix; the global
    // read narrows to history keys via the reserved history marker.
    private async Task<Dictionary<DateTime, HistoryBucket>> LoadHistoryBucketsAsync(string? name, CancellationToken ct)
    {
        var buckets = new Dictionary<DateTime, HistoryBucket>();
        var scope = name is null ? null : new Dictionary<string, string> { [Tags.Adapter] = name };
        var calls = new MetricRef(Names.AdapterCalls, scope);
        var duration = new MetricRef(Names.AdapterDuration, scope);

        // Open-ended window — bounded by the 7-day hourly-stat retention. Count outcomes split by outcome so the
        // HistoryBucket can tally calls + errors; the duration sum comes from the dur metric.
        var window = new MetricWindow(DateTime.MinValue, DateTime.MaxValue);

        foreach (var point in await _metrics.GetSeriesAsync(new SeriesQuery(calls, window, MetricResolution.Hourly, MetricAggregation.Sum, BreakdownBy: Tags.Outcome), ct))
        {
            HistoryBucketFor(buckets, point.BucketStart).Add(point.TagValue!, point.Value);
        }

        foreach (var point in await _metrics.GetSeriesAsync(new SeriesQuery(duration, window, MetricResolution.Hourly, MetricAggregation.Sum), ct))
        {
            HistoryBucketFor(buckets, point.BucketStart).Add(AdapterCounterKeys.DurationToken, point.Value);
        }

        return buckets;
    }

    private static HistoryBucket HistoryBucketFor(Dictionary<DateTime, HistoryBucket> buckets, DateTime hour)
    {
        if (!buckets.TryGetValue(hour, out var bucket))
        {
            bucket = new HistoryBucket();
            buckets[hour] = bucket;
        }

        return bucket;
    }

    private static List<AdapterHistoryPointModel> ProjectHistory(Dictionary<DateTime, HistoryBucket> buckets)
    {
        return
        [
            .. buckets
                .OrderBy(x => x.Key)
                .Select(x =>
                    new AdapterHistoryPointModel
                    {
                        Hour = x.Key,
                        Calls = x.Value.Calls,
                        Errors = x.Value.Errors,
                        ErrorRate = x.Value.Calls == 0 ? 0 : (double)x.Value.Errors / x.Value.Calls,
                        AvgDurationMs = x.Value.Calls == 0 ? 0 : (double)x.Value.DurationSum / x.Value.Calls,
                    }),
        ];
    }

    // Builds the lifetime stat set via the metric seam (§8.3x): per-adapter outcome counts + duration sum for
    // the Totals, and — for a specific adapter's detail page — the per-operation and per-group breakdowns. When
    // <paramref name="name"/> is null only the Totals are needed (the list page), so the finer breakdowns are
    // skipped. The local backend reproduces the merged Statistic+Counter read the inline scan used to do.
    private async Task<StatSet> LoadStatsAsync(CancellationToken ct, string? name = null)
    {
        var set = new StatSet();
        var scope = name is null ? null : new Dictionary<string, string> { [Tags.Adapter] = name };
        var calls = new MetricRef(Names.AdapterCalls, scope);
        var duration = new MetricRef(Names.AdapterDuration, scope);

        foreach (var row in await _metrics.GetBreakdownAsync(calls, [Tags.Adapter, Tags.Outcome], null, ct))
        {
            Bucket(set.Totals, row.Tags[Tags.Adapter]).Add(row.Tags[Tags.Outcome], row.Value);
        }

        foreach (var row in await _metrics.GetBreakdownAsync(duration, [Tags.Adapter], null, ct))
        {
            Bucket(set.Totals, row.Tags[Tags.Adapter]).Add(AdapterCounterKeys.DurationToken, row.Value);
        }

        if (name is null)
        {
            return set;
        }

        await FoldDimensionAsync(calls, duration, Tags.Operation, set.Operations, ct);
        await FoldDimensionAsync(calls, duration, Tags.Group, set.Groups, ct);

        return set;
    }

    // Folds one finer dimension (operation | group) of the calls + duration metrics into its (adapter, value)
    // bucket map: outcome counts from calls, the duration sum from the dur metric.
    private async Task FoldDimensionAsync(MetricRef calls, MetricRef duration, string dimension, Dictionary<DimensionKey, OutcomeCounts> map, CancellationToken ct)
    {
        foreach (var row in await _metrics.GetBreakdownAsync(calls, [Tags.Adapter, dimension, Tags.Outcome], null, ct))
        {
            Bucket(map, new DimensionKey(row.Tags[Tags.Adapter], row.Tags[dimension])).Add(row.Tags[Tags.Outcome], row.Value);
        }

        foreach (var row in await _metrics.GetBreakdownAsync(duration, [Tags.Adapter, dimension], null, ct))
        {
            Bucket(map, new DimensionKey(row.Tags[Tags.Adapter], row.Tags[dimension])).Add(AdapterCounterKeys.DurationToken, row.Value);
        }
    }

    private async Task<double> PercentileAsync(MetricRef metric, int percentile, CancellationToken ct)
    {
        var rows = await _metrics.GetPercentileBreakdownAsync(metric, percentile, [], null, ct);

        return rows.Count == 0 ? 0 : rows[0].Value;
    }

    private static OutcomeCounts Bucket<TKey>(Dictionary<TKey, OutcomeCounts> map, TKey key)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out var counts))
        {
            counts = new OutcomeCounts();
            map[key] = counts;
        }

        return counts;
    }

    private static double Rate(OutcomeCounts? counts)
    {
        if (counts is null || counts.Total == 0)
        {
            return 0;
        }

        return (double)counts.Errors / counts.Total;
    }

    private readonly record struct DimensionKey(string Adapter, string Value);

    private readonly record struct AppAdapterKey(string Application, string Adapter);

    private sealed class StatSet
    {
        public Dictionary<string, OutcomeCounts> Totals { get; } = new(StringComparer.Ordinal);

        public Dictionary<DimensionKey, OutcomeCounts> Operations { get; } = [];

        public Dictionary<DimensionKey, OutcomeCounts> Groups { get; } = [];
    }

    private sealed class OutcomeCounts
    {
        public long Total { get; private set; }

        public long Errors { get; private set; }

        public long DurationSum { get; private set; }

        public double AvgDurationMs => Total == 0 ? 0 : (double)DurationSum / Total;

        public void Add(string outcome, long count)
        {
            // The duration-sum token rides the same keys as the outcome counts but is NOT a call — fold it
            // into DurationSum, never the Total denominator.
            if (string.Equals(outcome, AdapterCounterKeys.DurationToken, StringComparison.Ordinal))
            {
                DurationSum += count;

                return;
            }

            Total += count;

            if (IsError(outcome))
            {
                Errors += count;
            }
        }

        // Error outcome tokens mirror AdapterCounterKeys.OutcomeToken (failed / throttled /
        // circuit_open). "success" and "unknown" count toward the denominator only.
        private static bool IsError(string outcome) => outcome switch
        {
            "failed" => true,
            "throttled" => true,
            "circuit_open" => true,
            _ => false,
        };
    }

    // Accumulates one hourly time-series bucket: total calls, error calls, and summed duration — the same
    // count/error/duration split as OutcomeCounts but over a single hour, for the performance chart.
    private sealed class HistoryBucket
    {
        public long Calls { get; private set; }

        public long Errors { get; private set; }

        public long DurationSum { get; private set; }

        public void Add(string outcome, long value)
        {
            if (string.Equals(outcome, AdapterCounterKeys.DurationToken, StringComparison.Ordinal))
            {
                DurationSum += value;

                return;
            }

            Calls += value;

            if (outcome is "failed" or "throttled" or "circuit_open")
            {
                Errors += value;
            }
        }
    }
}
