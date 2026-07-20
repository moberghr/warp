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

    // The list page's per-adapter average latency is computed over the last 24h only. Without a window
    // the GroupBy full-scans AdapterCallLog on every list load; the (AdapterName, Timestamp) index serves
    // this bound cheaply, and a rolling day is a representative "current latency" for the dashboard.
    private const int LatencyWindowHours = 24;

    private readonly TContext _context;
    private readonly TimeProvider _timeProvider;

    public AdapterQueryService(TContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
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
        var latency = await LoadAdapterLatencyAsync(ct);

        var result = new List<AdapterListItemModel>(definitions.Count);

        foreach (var definition in definitions)
        {
            var totals = stats.Totals.GetValueOrDefault(definition.Name);
            var avg = latency.GetValueOrDefault(definition.Name);

            result.Add(new AdapterListItemModel
            {
                Name = definition.Name,
                ConfigSummary = definition.ConfigSummary,
                FirstSeenAt = definition.FirstSeenAt,
                LastSeenAt = definition.LastSeenAt,
                TotalCalls = totals?.Total ?? 0,
                ErrorCount = totals?.Errors ?? 0,
                ErrorRate = Rate(totals),
                AvgDurationMs = avg,
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

        var stats = await LoadStatsAsync(ct);

        var totals = stats.Totals.GetValueOrDefault(name);

        var operationLatency = await _context.Set<AdapterCallLog>()
            .AsNoTracking()
            .Where(x => x.AdapterName == name)
            .GroupBy(x => x.Operation)
            .Select(g =>
                new
                {
                    Operation = g.Key,
                    Avg = g.Average(x => x.DurationMs),
                })
            .ToListAsync(ct);

        var operationLatencyByKey = operationLatency.ToDictionary(x => x.Operation, x => x.Avg, StringComparer.Ordinal);

        var groupLatency = await _context.Set<AdapterCallLog>()
            .AsNoTracking()
            .Where(x => x.AdapterName == name)
            .Where(x => x.GroupName != null)
            .GroupBy(x => x.GroupName!)
            .Select(g =>
                new
                {
                    Group = g.Key,
                    Avg = g.Average(x => x.DurationMs),
                })
            .ToListAsync(ct);

        var groupLatencyByKey = groupLatency.ToDictionary(x => x.Group, x => x.Avg, StringComparer.Ordinal);

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
                    AvgDurationMs = operationLatencyByKey.GetValueOrDefault(x.Key.Value),
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
                    AvgDurationMs = groupLatencyByKey.GetValueOrDefault(x.Key.Value),
                    LastFailureAt = groupFailureByKey.TryGetValue(x.Key.Value, out var last) ? last : null,
                })
            .ToList();

        var adapterAvg = await _context.Set<AdapterCallLog>()
            .AsNoTracking()
            .Where(x => x.AdapterName == name)
            .AverageAsync(x => (double?)x.DurationMs, ct) ?? 0;

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
            AvgDurationMs = adapterAvg,
            Operations = operations,
            Groups = groups,
            RecentCalls = recentCalls,
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

    private async Task<Dictionary<string, double>> LoadAdapterLatencyAsync(CancellationToken ct)
    {
        var since = _timeProvider.GetUtcNow().UtcDateTime.AddHours(-LatencyWindowHours);

        var rows = await _context.Set<AdapterCallLog>()
            .AsNoTracking()
            .Where(x => x.Timestamp >= since)
            .GroupBy(x => x.AdapterName)
            .Select(g =>
                new
                {
                    Name = g.Key,
                    Avg = g.Average(x => x.DurationMs),
                })
            .ToListAsync(ct);

        return rows.ToDictionary(x => x.Name, x => x.Avg, StringComparer.Ordinal);
    }

    // Loads every adapter-namespaced Statistic + pending Counter row once, merges them (aggregated
    // Statistic value + un-collapsed Counter rows for the same key), and folds each into its
    // adapter / operation / group outcome bucket. The prefix is a constant, so there is no
    // user-supplied LIKE pattern to escape.
    private async Task<StatSet> LoadStatsAsync(CancellationToken ct)
    {
        const string prefix = AdapterCounterKeys.Prefix + ":";

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
                    Value = (long)g.Sum(c => c.Value),
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

        public void Add(string outcome, long count)
        {
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
}
