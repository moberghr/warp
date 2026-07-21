using Microsoft.EntityFrameworkCore;
using Warp.Core.Adapters;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;

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

    public AdapterQueryService(TContext context)
    {
        _context = context;
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

        var (p90, p95, p99) = Percentiles(stats.DurationBuckets.GetValueOrDefault(name));

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
        var prefix = name is null
            ? $"{AdapterCounterKeys.Prefix}:"
            : $"{AdapterCounterKeys.Prefix}:{name}:{AdapterCounterKeys.HistoryMarker}:";

        var histMarker = $":{AdapterCounterKeys.HistoryMarker}:";

        var aggregated = await _context.Set<Statistic>()
            .AsNoTracking()
            .Where(x => x.Key.StartsWith(prefix))
            .Where(x => x.Key.Contains(histMarker))
            .Select(x =>
                new
                {
                    x.Key,
                    x.Value,
                })
            .ToListAsync(ct);

        var pending = await _context.Set<Counter>()
            .AsNoTracking()
            .Where(x => x.Key.StartsWith(prefix))
            .Where(x => x.Key.Contains(histMarker))
            .GroupBy(x => x.Key)
            .Select(g =>
                new
                {
                    Key = g.Key,
                    Value = g.Sum(c => (long)c.Value),
                })
            .ToListAsync(ct);

        var buckets = new Dictionary<DateTime, HistoryBucket>();

        foreach (var row in aggregated.Concat(pending))
        {
            if (!AdapterCounterKeys.TryParseHistory(row.Key, out var keyName, out var outcome, out var hour))
            {
                continue;
            }

            if (name is not null && !string.Equals(keyName, name, StringComparison.Ordinal))
            {
                continue;
            }

            if (!buckets.TryGetValue(hour, out var bucket))
            {
                bucket = new HistoryBucket();
                buckets[hour] = bucket;
            }

            bucket.Add(outcome, row.Value);
        }

        return buckets;
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

    // Loads adapter-namespaced Statistic + pending Counter rows once, merges them (aggregated
    // Statistic value + un-collapsed Counter rows for the same key), and folds each into its
    // adapter / operation / group outcome bucket. When <paramref name="name"/> is given the load is
    // scoped to that adapter's keys ("adapter:{name}:") so a detail page never materialises every
    // adapter's stat rows — adapter names are colon-free (the key delimiter), so the prefix is exact.
    private async Task<StatSet> LoadStatsAsync(CancellationToken ct, string? name = null)
    {
        var prefix = name is null
            ? AdapterCounterKeys.Prefix + ":"
            : AdapterCounterKeys.Prefix + ":" + name + ":";

        var aggregated = await _context.Set<Statistic>()
            .AsNoTracking()
            .Where(x => x.Key.StartsWith(prefix))
            .Select(x =>
                new
                {
                    x.Key,
                    x.Value,
                })
            .ToListAsync(ct);

        var pending = await _context.Set<Counter>()
            .AsNoTracking()
            .Where(x => x.Key.StartsWith(prefix))
            .GroupBy(x => x.Key)
            .Select(g =>
                new
                {
                    Key = g.Key,
                    Value = g.Sum(c => (long)c.Value),
                })
            .ToListAsync(ct);

        var merged = aggregated
            .Concat(pending)
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .Select(g =>
                new
                {
                    Key = g.Key,
                    Value = g.Sum(x => x.Value),
                });

        var set = new StatSet();

        foreach (var row in merged)
        {
            // Latency-histogram bucket rows ride the same "adapter:" prefix but are not count/error rows —
            // accumulate them into the per-adapter bucket map for the percentile walk, never the StatSet.
            if (AdapterCounterKeys.TryParsePct(row.Key, out var pctAdapter, out var upperMs))
            {
                if (!set.DurationBuckets.TryGetValue(pctAdapter, out var buckets))
                {
                    buckets = [];
                    set.DurationBuckets[pctAdapter] = buckets;
                }

                buckets[upperMs] = buckets.GetValueOrDefault(upperMs) + row.Value;

                continue;
            }

            if (!AdapterCounterKeys.TryParse(row.Key, out var parsed))
            {
                continue;
            }

            var bucket = parsed.Dimension switch
            {
                AdapterStatDimension.Total => Bucket(set.Totals, parsed.Adapter),
                AdapterStatDimension.Operation => Bucket(set.Operations, new DimensionKey(parsed.Adapter, parsed.Value)),
                AdapterStatDimension.Group => Bucket(set.Groups, new DimensionKey(parsed.Adapter, parsed.Value)),
                _ => null,
            };

            bucket?.Add(parsed.Outcome, row.Value);
        }

        return set;
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

    // Walks the ascending latency buckets cumulatively: the percentile for quantile q over N samples is the
    // upper bound of the smallest bucket whose cumulative count reaches ceil(q*N). The overflow bucket
    // (int.MaxValue, "> 10000 ms") reports the last real bound (10000) as a displayable floor. Returns 0
    // when there is no bucket data.
    private static (double P90, double P95, double P99) Percentiles(Dictionary<int, long>? buckets)
    {
        if (buckets is null || buckets.Count == 0)
        {
            return (0, 0, 0);
        }

        var total = buckets.Values.Sum();
        if (total == 0)
        {
            return (0, 0, 0);
        }

        return (
            Quantile(buckets, total, 0.90),
            Quantile(buckets, total, 0.95),
            Quantile(buckets, total, 0.99));
    }

    private static double Quantile(Dictionary<int, long> buckets, long total, double q)
    {
        var threshold = (long)Math.Ceiling(q * total);
        long cumulative = 0;

        foreach (var bound in AdapterCounterKeys.Buckets)
        {
            cumulative += buckets.GetValueOrDefault(bound);

            if (cumulative >= threshold)
            {
                // Overflow bucket → report the last real bound (10000) rather than int.MaxValue.
                return bound == int.MaxValue ? AdapterCounterKeys.Buckets[^2] : bound;
            }
        }

        return AdapterCounterKeys.Buckets[^2];
    }

    private sealed class StatSet
    {
        public Dictionary<string, OutcomeCounts> Totals { get; } = new(StringComparer.Ordinal);

        public Dictionary<DimensionKey, OutcomeCounts> Operations { get; } = [];

        public Dictionary<DimensionKey, OutcomeCounts> Groups { get; } = [];

        // Per-adapter latency histogram: adapter name → (bucket upper bound → count).
        public Dictionary<string, Dictionary<int, long>> DurationBuckets { get; } = new(StringComparer.Ordinal);
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
