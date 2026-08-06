using System.Text;
using Microsoft.EntityFrameworkCore;
using Warp.Core.Data.Entities;
using Warp.Core.Endpoints;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Metrics;
using static Warp.Core.Metrics.WarpMetricCatalog;

namespace Warp.Core.Services;

/// <summary>
/// <see cref="IEndpointQueryService"/> over the user's <typeparamref name="TContext"/>. Counts, error
/// rates and average latency are read from the merged <see cref="Statistic"/> + pending <see cref="Counter"/>
/// aggregates (surviving <see cref="EndpointCallLog"/> deletion); last-failure timestamps and the recent
/// calls list read the retained log rows. The endpoint list is discovered from the aggregate keys — there
/// is no endpoint-definition table. Counter keys follow the <see cref="EndpointCounterKeys"/> layout, whose
/// route segment is the normalized "{METHOD} {template}" identity the flusher also stamps onto each row.
/// </summary>
public class EndpointQueryService<TContext> : IEndpointQueryService
    where TContext : DbContext
{
    private const int RecentCallsLimit = 100;

    private const int RelatedJobsLimit = 100;

    private readonly TContext _context;
    private readonly IMetricSource _metrics;

    public EndpointQueryService(TContext context, IMetricSource metrics)
    {
        _context = context;
        _metrics = metrics;
    }

    public async Task<IReadOnlyList<EndpointListItemModel>> GetEndpoints(CancellationToken ct = default)
    {
        var stats = await LoadStatsAsync(ct);

        return
        [
            .. stats.Totals
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x =>
                    new EndpointListItemModel
                    {
                        Id = EncodeId(x.Key),
                        Method = SplitMethod(x.Key),
                        RouteTemplate = SplitTemplate(x.Key),
                        Route = x.Key,
                        TotalCalls = x.Value.Total,
                        ErrorCount = x.Value.Errors,
                        ErrorRate = Rate(x.Value),
                        AvgDurationMs = x.Value.AvgDurationMs,
                    }),
        ];
    }

    public async Task<IReadOnlyList<EndpointHistoryPointModel>> GetGlobalHistory(CancellationToken ct = default)
    {
        var buckets = await LoadHistoryBucketsAsync(route: null, ct);

        return
        [
            .. buckets
                .OrderBy(x => x.Key)
                .Select(x =>
                    new EndpointHistoryPointModel
                    {
                        Hour = x.Key,
                        Calls = x.Value.Calls,
                        Errors = x.Value.Errors,
                        ErrorRate = x.Value.Calls == 0 ? 0 : (double)x.Value.Errors / x.Value.Calls,
                        AvgDurationMs = x.Value.Calls == 0 ? 0 : (double)x.Value.DurationSum / x.Value.Calls,
                    }),
        ];
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

    public async Task<IReadOnlyList<EndpointAppStatModel>> GetEndpointStatsByApplication(string application, CancellationToken ct = default)
    {
        // Sanitize with the SAME transform the write side (EndpointCounterKeys.AppTotal/AppHistory) applies,
        // BEFORE building the prefix filter — otherwise a colon-bearing app name would query a prefix that
        // never matches (the keys store the sanitized form) and silently return empty.
        var sanitized = EndpointCounterKeys.SanitizeApplication(application);

        var stats = await LoadAppStatsAsync(sanitized, ct);

        return
        [
            .. stats
                .Where(x => string.Equals(x.Key.Application, sanitized, StringComparison.Ordinal))
                .OrderBy(x => x.Key.Route, StringComparer.Ordinal)
                .Select(x =>
                    new EndpointAppStatModel
                    {
                        Application = x.Key.Application,
                        Route = x.Key.Route,
                        Method = SplitMethod(x.Key.Route),
                        RouteTemplate = SplitTemplate(x.Key.Route),
                        Calls = x.Value.Total,
                        Errors = x.Value.Errors,
                        ErrorRate = Rate(x.Value),
                        AvgDurationMs = x.Value.AvgDurationMs,
                    }),
        ];
    }

    // Reads the disjoint per-app total keys (Statistic + not-yet-collapsed Counter rows) and folds each
    // into its (application, route) outcome bucket. A null application loads every app's keys
    // ("endpoint-app:"); a supplied one scopes to that app ("endpoint-app:{app}:") — application is
    // colon-free (config identity), so the prefix is exact. Per-app history keys ride the same prefix but
    // are length-6 and are simply skipped by TryParseApp (they power charts read elsewhere, not these
    // lifetime aggregates). Application is part of the endpoint identity, so the same route under two apps
    // yields two distinct (application, route) buckets.
    private async Task<Dictionary<AppRouteKey, OutcomeCounts>> LoadAppStatsAsync(string? application, CancellationToken ct)
    {
        var map = new Dictionary<AppRouteKey, OutcomeCounts>();
        var scope = application is null ? null : new Dictionary<string, string> { [Tags.Application] = application };
        var calls = new MetricRef(Names.EndpointCalls, scope);
        var duration = new MetricRef(Names.EndpointDuration, scope);

        foreach (var row in await _metrics.GetBreakdownAsync(calls, [Tags.Application, Tags.Route, Tags.Outcome], null, ct))
        {
            Bucket(map, new AppRouteKey(row.Tags[Tags.Application], row.Tags[Tags.Route])).Add(row.Tags[Tags.Outcome], row.Value);
        }

        foreach (var row in await _metrics.GetBreakdownAsync(duration, [Tags.Application, Tags.Route], null, ct))
        {
            Bucket(map, new AppRouteKey(row.Tags[Tags.Application], row.Tags[Tags.Route])).Add(EndpointCounterKeys.DurationToken, row.Value);
        }

        return map;
    }

    public async Task<EndpointDetailModel?> GetEndpointDetail(string id, CancellationToken ct = default)
    {
        var route = TryDecodeId(id);
        if (route is null)
        {
            return null;
        }

        var stats = await LoadStatsAsync(ct, route);

        var totals = stats.Totals.GetValueOrDefault(route);
        if (totals is null)
        {
            return null;
        }

        var method = SplitMethod(route);
        var template = SplitTemplate(route);

        var durationRef = new MetricRef(Names.EndpointDuration, new Dictionary<string, string> { [Tags.Route] = route });
        var p90 = await PercentileAsync(durationRef, 90, ct);
        var p95 = await PercentileAsync(durationRef, 95, ct);
        var p99 = await PercentileAsync(durationRef, 99, ct);

        var groupFailures = await _context.Set<EndpointCallLog>()
            .AsNoTracking()
            .Where(x => x.Method == method)
            .Where(x => x.RouteTemplate == template)
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

        var recentCalls = await _context.Set<EndpointCallLog>()
            .AsNoTracking()
            .Where(x => x.Method == method)
            .Where(x => x.RouteTemplate == template)
            .OrderByDescending(x => x.Timestamp)
            .ThenByDescending(x => x.Id)
            .Take(RecentCallsLimit)
            .Select(x =>
                new EndpointCallSummaryModel
                {
                    Id = x.Id,
                    Timestamp = x.Timestamp,
                    DurationMs = x.DurationMs,
                    Outcome = x.Outcome,
                    StatusCode = x.StatusCode,
                    RemoteIp = x.RemoteIp,
                    UserAgent = x.UserAgent,
                    User = x.User,
                    GroupName = x.GroupName,
                })
            .ToListAsync(ct);

        var groups = stats.Groups
            .Where(x => string.Equals(x.Key.Route, route, StringComparison.Ordinal))
            .OrderBy(x => x.Key.Group, StringComparer.Ordinal)
            .Select(x =>
                new EndpointGroupStatModel
                {
                    Group = x.Key.Group,
                    Calls = x.Value.Total,
                    Errors = x.Value.Errors,
                    ErrorRate = Rate(x.Value),
                    AvgDurationMs = x.Value.AvgDurationMs,
                    LastFailureAt = groupFailureByKey.TryGetValue(x.Key.Group, out var last) ? last : null,
                })
            .ToList();

        return new EndpointDetailModel
        {
            Id = id,
            Method = method,
            RouteTemplate = template,
            Route = route,
            GroupLabel = "Caller",
            TotalCalls = totals.Total,
            ErrorCount = totals.Errors,
            ErrorRate = Rate(totals),
            AvgDurationMs = totals.AvgDurationMs,
            P90DurationMs = p90,
            P95DurationMs = p95,
            P99DurationMs = p99,
            Groups = groups,
            RecentCalls = recentCalls,
            History = await LoadHistoryAsync(route, ct),
        };
    }

    public async Task<EndpointCallDetailModel?> GetCallDetail(string id, Guid callId, CancellationToken ct = default)
    {
        var route = TryDecodeId(id);
        if (route is null)
        {
            return null;
        }

        var method = SplitMethod(route);
        var template = SplitTemplate(route);

        var detail = await _context.Set<EndpointCallLog>()
            .AsNoTracking()
            .Where(x => x.Method == method)
            .Where(x => x.RouteTemplate == template)
            .Where(x => x.Id == callId)
            .Select(x =>
                new EndpointCallDetailModel
                {
                    Id = x.Id,
                    Method = x.Method,
                    RouteTemplate = x.RouteTemplate,
                    Operation = x.Operation,
                    GroupName = x.GroupName,
                    Timestamp = x.Timestamp,
                    DurationMs = x.DurationMs,
                    Outcome = x.Outcome,
                    StatusCode = x.StatusCode,
                    RemoteIp = x.RemoteIp,
                    UserAgent = x.UserAgent,
                    User = x.User,
                    ExceptionType = x.ExceptionType,
                    ExceptionMessage = x.ExceptionMessage,
                    RequestHeaders = x.RequestHeaders,
                    ResponseHeaders = x.ResponseHeaders,
                    RequestBody = x.RequestBody,
                    ResponseBody = x.ResponseBody,
                    MachineName = x.MachineName,
                    TraceId = x.TraceId,
                    TagsJson = x.TagsJson,
                })
            .FirstOrDefaultAsync(ct);

        if (detail is null)
        {
            return null;
        }

        // Request→jobs drill-down: jobs enqueued during this request share its trace id (§ the ambient
        // Activity propagates into Publisher). Only when tracing was active (TraceId set).
        if (detail.TraceId is { } traceId)
        {
            detail.RelatedJobs = await _context.Set<Job>()
                .AsNoTracking()
                .Where(x => x.TraceId == traceId)
                .OrderBy(x => x.ScheduleTime)
                .Take(RelatedJobsLimit)
                .Select(x =>
                    new EndpointRelatedJobModel
                    {
                        Id = x.Id,
                        Type = x.Type,
                        State = x.CurrentState,
                        Queue = x.Queue,
                    })
                .ToListAsync(ct);
        }

        return detail;
    }

    // URL-safe base64 of the "{METHOD} {template}" route so the detail route id survives a path segment
    // (the raw route contains '/' and spaces). Decodes back to the exact route for the stats lookup.
    private static string EncodeId(string route)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(route))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static string? TryDecodeId(string id)
    {
        var normalized = id.Replace('-', '+').Replace('_', '/');
        var padded = (normalized.Length % 4) switch
        {
            2 => normalized + "==",
            3 => normalized + "=",
            _ => normalized,
        };

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string SplitMethod(string route)
    {
        var space = route.IndexOf(' ', StringComparison.Ordinal);

        return space < 0 ? route : route[..space];
    }

    private static string SplitTemplate(string route)
    {
        var space = route.IndexOf(' ', StringComparison.Ordinal);

        return space < 0 ? string.Empty : route[(space + 1)..];
    }

    // Builds the hourly performance time-series for one route from the durable hourly history buckets,
    // oldest first. Bounded by the 7-day hourly-stat retention; hours with no traffic simply don't exist.
    private async Task<List<EndpointHistoryPointModel>> LoadHistoryAsync(string route, CancellationToken ct)
    {
        var buckets = await LoadHistoryBucketsAsync(route, ct);

        return
        [
            .. buckets
                .OrderBy(x => x.Key)
                .Select(x =>
                    new EndpointHistoryPointModel
                    {
                        Hour = x.Key,
                        Calls = x.Value.Calls,
                        Errors = x.Value.Errors,
                        ErrorRate = x.Value.Calls == 0 ? 0 : (double)x.Value.Errors / x.Value.Calls,
                        AvgDurationMs = x.Value.Calls == 0 ? 0 : (double)x.Value.DurationSum / x.Value.Calls,
                    }),
        ];
    }

    // Reads the durable hourly history counters (Statistic plus the not-yet-collapsed Counter rows, so the
    // current hour is not missing) and folds them per hour. A null route aggregates across every endpoint
    // for the global overview; a supplied route scopes to that one. The scoped read uses a prefix; the
    // global read narrows to history keys via the reserved history marker.
    private async Task<Dictionary<DateTime, HistoryBucket>> LoadHistoryBucketsAsync(string? route, CancellationToken ct)
    {
        var buckets = new Dictionary<DateTime, HistoryBucket>();
        var scope = route is null ? null : new Dictionary<string, string> { [Tags.Route] = route };
        var calls = new MetricRef(Names.EndpointCalls, scope);
        var duration = new MetricRef(Names.EndpointDuration, scope);
        var window = new MetricWindow(DateTime.MinValue, DateTime.MaxValue);

        foreach (var point in await _metrics.GetSeriesAsync(new SeriesQuery(calls, window, MetricResolution.Hourly, MetricAggregation.Sum, BreakdownBy: Tags.Outcome), ct))
        {
            HistoryBucketFor(buckets, point.BucketStart).Add(point.TagValue!, point.Value);
        }

        foreach (var point in await _metrics.GetSeriesAsync(new SeriesQuery(duration, window, MetricResolution.Hourly, MetricAggregation.Sum), ct))
        {
            HistoryBucketFor(buckets, point.BucketStart).Add(EndpointCounterKeys.DurationToken, point.Value);
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

    private async Task<double> PercentileAsync(MetricRef metric, int percentile, CancellationToken ct)
    {
        var rows = await _metrics.GetPercentileBreakdownAsync(metric, percentile, [], null, ct);

        return rows.Count == 0 ? 0 : rows[0].Value;
    }

    // Builds the lifetime stat set via the metric seam (§8.3x): per-route outcome counts + duration sum for the
    // Totals, and — for a specific endpoint's detail page — the per-group (caller) breakdown. A null route needs
    // only the Totals (the list page), so the group breakdown is skipped.
    private async Task<StatSet> LoadStatsAsync(CancellationToken ct, string? route = null)
    {
        var set = new StatSet();
        var scope = route is null ? null : new Dictionary<string, string> { [Tags.Route] = route };
        var calls = new MetricRef(Names.EndpointCalls, scope);
        var duration = new MetricRef(Names.EndpointDuration, scope);

        foreach (var row in await _metrics.GetBreakdownAsync(calls, [Tags.Route, Tags.Outcome], null, ct))
        {
            Bucket(set.Totals, row.Tags[Tags.Route]).Add(row.Tags[Tags.Outcome], row.Value);
        }

        foreach (var row in await _metrics.GetBreakdownAsync(duration, [Tags.Route], null, ct))
        {
            Bucket(set.Totals, row.Tags[Tags.Route]).Add(EndpointCounterKeys.DurationToken, row.Value);
        }

        if (route is null)
        {
            return set;
        }

        foreach (var row in await _metrics.GetBreakdownAsync(calls, [Tags.Route, Tags.Group, Tags.Outcome], null, ct))
        {
            Bucket(set.Groups, new GroupKey(row.Tags[Tags.Route], row.Tags[Tags.Group])).Add(row.Tags[Tags.Outcome], row.Value);
        }

        foreach (var row in await _metrics.GetBreakdownAsync(duration, [Tags.Route, Tags.Group], null, ct))
        {
            Bucket(set.Groups, new GroupKey(row.Tags[Tags.Route], row.Tags[Tags.Group])).Add(EndpointCounterKeys.DurationToken, row.Value);
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

    private readonly record struct GroupKey(string Route, string Group);

    private readonly record struct AppRouteKey(string Application, string Route);

    private sealed class StatSet
    {
        public Dictionary<string, OutcomeCounts> Totals { get; } = new(StringComparer.Ordinal);

        public Dictionary<GroupKey, OutcomeCounts> Groups { get; } = [];
    }

    private sealed class OutcomeCounts
    {
        public long Total { get; private set; }

        public long Errors { get; private set; }

        public long DurationSum { get; private set; }

        public double AvgDurationMs => Total == 0 ? 0 : (double)DurationSum / Total;

        public void Add(string outcome, long count)
        {
            if (string.Equals(outcome, EndpointCounterKeys.DurationToken, StringComparison.Ordinal))
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

        private static bool IsError(string outcome) => string.Equals(outcome, "failed", StringComparison.Ordinal);
    }

    // Accumulates one hourly time-series bucket: total calls, error calls, and summed duration. Mirrors the
    // count/error/duration split of OutcomeCounts but over a single hour, for the performance chart.
    private sealed class HistoryBucket
    {
        public long Calls { get; private set; }

        public long Errors { get; private set; }

        public long DurationSum { get; private set; }

        public void Add(string outcome, long value)
        {
            if (string.Equals(outcome, EndpointCounterKeys.DurationToken, StringComparison.Ordinal))
            {
                DurationSum += value;

                return;
            }

            Calls += value;

            if (string.Equals(outcome, "failed", StringComparison.Ordinal))
            {
                Errors += value;
            }
        }
    }
}
