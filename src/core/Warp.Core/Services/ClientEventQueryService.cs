using Microsoft.EntityFrameworkCore;
using Warp.Core.ClientObservability;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;

namespace Warp.Core.Services;

/// <summary>
/// Reads client (browser) observability (§8.27). The summary is folded from the durable <c>clientevent:</c>
/// Counter/Statistic keys (survives <see cref="ClientEventLog"/> cleanup, §8.22); the event stream + detail
/// read raw rows. CLS vital values are unscaled (÷1000) back to their unitless form on the way out.
/// </summary>
public sealed class ClientEventQueryService<TContext> : IClientEventQueryService
    where TContext : DbContext
{
    private const int TopNames = 10;

    private readonly TContext _context;

    public ClientEventQueryService(TContext context) => _context = context;

    public async Task<ClientObservabilitySummaryModel> GetSummary(string? application, CancellationToken ct)
    {
        var global = await LoadMergedAsync(ClientEventKeys.Prefix + ":", ct);

        var typeCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        var topErrors = new Dictionary<string, long>(StringComparer.Ordinal);
        var topEvents = new Dictionary<string, long>(StringComparer.Ordinal);
        var vitalCount = new Dictionary<string, long>(StringComparer.Ordinal);
        var vitalDur = new Dictionary<string, long>(StringComparer.Ordinal);
        var vitalBuckets = new Dictionary<string, Dictionary<int, long>>(StringComparer.Ordinal);
        var history = new Dictionary<string, ClientHistoryAccumulator>(StringComparer.Ordinal);

        foreach (var (key, value) in global)
        {
            if (ClientEventKeys.TryParseTypeTotal(key, out var totalType))
            {
                typeCounts[totalType] = value;
            }
            else if (ClientEventKeys.TryParseTypeHistory(key, out var histType, out var hour))
            {
                Hour(history, hour).Add(histType, value);
            }
            else if (ClientEventKeys.TryParseNameTotal(key, out var nameType, out var name))
            {
                if (string.Equals(nameType, ClientEventKeys.TypeToken(ClientEventType.Error), StringComparison.Ordinal))
                {
                    topErrors[name] = value;
                }
                else if (string.Equals(nameType, ClientEventKeys.TypeToken(ClientEventType.Event), StringComparison.Ordinal))
                {
                    topEvents[name] = value;
                }
            }
            else if (ClientEventKeys.TryParseVitalPct(key, out var pctVital, out var upperMs))
            {
                Bucket(vitalBuckets, pctVital)[upperMs] = value;
            }
            else if (ClientEventKeys.TryParseVital(key, out var vital, out var token))
            {
                if (string.Equals(token, ClientEventKeys.CountToken, StringComparison.Ordinal))
                {
                    vitalCount[vital] = value;
                }
                else
                {
                    vitalDur[vital] = value;
                }
            }
        }

        // A per-application view overrides only the per-type counts (the per-app slice carries type totals
        // only, §8.27); vitals + top names stay global.
        if (application is not null)
        {
            typeCounts.Clear();
            foreach (var (key, value) in await LoadMergedAsync(ClientEventKeys.AppPrefix + ":" + ClientEventKeys.Sanitize(application) + ":", ct))
            {
                if (ClientEventKeys.TryParseAppTypeTotal(key, out _, out var appType))
                {
                    typeCounts[appType] = value;
                }
            }
        }

        var errors = typeCounts.GetValueOrDefault(ClientEventKeys.TypeToken(ClientEventType.Error));
        var logs = typeCounts.GetValueOrDefault(ClientEventKeys.TypeToken(ClientEventType.Log));
        var events = typeCounts.GetValueOrDefault(ClientEventKeys.TypeToken(ClientEventType.Event));
        var vitals = typeCounts.GetValueOrDefault(ClientEventKeys.TypeToken(ClientEventType.Vital));
        var total = errors + logs + events + vitals;

        return new ClientObservabilitySummaryModel
        {
            Application = application,
            ErrorCount = errors,
            LogCount = logs,
            EventCount = events,
            VitalCount = vitals,
            ErrorRate = total > 0 ? (double)errors / total : 0,
            TopErrors = TopN(topErrors),
            TopEvents = TopN(topEvents),
            Vitals = BuildVitals(vitalCount, vitalDur, vitalBuckets),
            History = [.. history
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x =>
                    new ClientHistoryPointModel
                    {
                        Hour = x.Key,
                        Errors = x.Value.Errors,
                        Logs = x.Value.Logs,
                        Events = x.Value.Events,
                        Vitals = x.Value.Vitals,
                    }),],
        };
    }

    public async Task<ClientEventPageModel> GetEvents(ClientEventFilter filter, CancellationToken ct)
    {
        var pageSize = filter.PageSize is > 0 and <= 200 ? filter.PageSize : 50;
        var page = filter.Page < 0 ? 0 : filter.Page;

        var query = _context.Set<ClientEventLog>().AsNoTracking();

        if (filter.Application is not null)
        {
            query = query.Where(x => x.Application == filter.Application);
        }

        if (filter.Type is not null)
        {
            query = query.Where(x => x.Type == filter.Type);
        }

        if (filter.SessionId is not null)
        {
            query = query.Where(x => x.SessionId == filter.SessionId);
        }

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(x => x.Timestamp)
            .Skip(page * pageSize)
            .Take(pageSize)
            .Select(x =>
                new ClientEventModel
                {
                    Id = x.Id,
                    Application = x.Application,
                    Type = x.Type,
                    Name = x.Name,
                    Level = x.Level,
                    Message = x.Message,
                    Value = x.Value,
                    Url = x.Url,
                    TraceId = x.TraceId,
                    SessionId = x.SessionId,
                    Timestamp = x.Timestamp,
                })
            .ToListAsync(ct);

        return new ClientEventPageModel { Items = items, Total = total };
    }

    public async Task<ClientEventDetailModel?> GetEvent(Guid id, CancellationToken ct)
    {
        return await _context.Set<ClientEventLog>()
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x =>
                new ClientEventDetailModel
                {
                    Id = x.Id,
                    Application = x.Application,
                    Type = x.Type,
                    Name = x.Name,
                    Level = x.Level,
                    Message = x.Message,
                    Stack = x.Stack,
                    Value = x.Value,
                    Url = x.Url,
                    TraceId = x.TraceId,
                    SessionId = x.SessionId,
                    Release = x.Release,
                    UserAgent = x.UserAgent,
                    RemoteIp = x.RemoteIp,
                    Properties = x.Properties,
                    Breadcrumbs = x.Breadcrumbs,
                    Timestamp = x.Timestamp,
                    ReceivedAt = x.ReceivedAt,
                })
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetApplications(CancellationToken ct)
    {
        var apps = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (key, _) in await LoadMergedAsync(ClientEventKeys.AppPrefix + ":", ct))
        {
            if (ClientEventKeys.TryParseAppTypeTotal(key, out var app, out _))
            {
                apps.Add(app);
            }
        }

        return [.. apps];
    }

    public async Task<ClientSessionModel?> GetSession(string sessionId, CancellationToken ct)
    {
        var events = await _context.Set<ClientEventLog>()
            .AsNoTracking()
            .Where(x => x.SessionId == sessionId)
            .OrderBy(x => x.Timestamp)
            .Select(x =>
                new ClientSessionEntryModel
                {
                    Kind = "client",
                    Timestamp = x.Timestamp,
                    TraceId = x.TraceId,
                    EventId = x.Id,
                    Type = x.Type,
                    Name = x.Name,
                    Level = x.Level,
                    Message = x.Message,
                    Value = x.Value,
                    Url = x.Url,
                })
            .ToListAsync(ct);

        if (events.Count == 0)
        {
            return null;
        }

        var traceIds = events
            .Where(x => x.TraceId.HasValue)
            .Select(x => x.TraceId!.Value)
            .Distinct()
            .ToList();

        var serverCalls = new List<ClientSessionEntryModel>();
        if (traceIds.Count > 0)
        {
            serverCalls = await _context.Set<EndpointCallLog>()
                .AsNoTracking()
                .Where(x => x.TraceId.HasValue)
                .Where(x => traceIds.Contains(x.TraceId!.Value))
                .Select(x =>
                    new ClientSessionEntryModel
                    {
                        Kind = "endpoint",
                        Timestamp = x.Timestamp,
                        TraceId = x.TraceId,
                        Method = x.Method,
                        Route = x.RouteTemplate,
                        StatusCode = x.StatusCode,
                        DurationMs = x.DurationMs,
                        Outcome = x.Outcome.ToString(),
                    })
                .ToListAsync(ct);
        }

        var application = await _context.Set<ClientEventLog>()
            .AsNoTracking()
            .Where(x => x.SessionId == sessionId)
            .Select(x => x.Application)
            .FirstOrDefaultAsync(ct);

        return new ClientSessionModel
        {
            SessionId = sessionId,
            Application = application,
            Entries = [.. events.Concat(serverCalls).OrderBy(x => x.Timestamp)],
        };
    }

    private async Task<IReadOnlyList<KeyValuePair<string, long>>> LoadMergedAsync(string prefix, CancellationToken ct)
    {
        var merged = new Dictionary<string, long>(StringComparer.Ordinal);

        var stats = await _context.Set<Statistic>()
            .AsNoTracking()
            .Where(x => x.Key.StartsWith(prefix))
            .Select(x => new { x.Key, x.Value })
            .ToListAsync(ct);
        foreach (var row in stats)
        {
            merged[row.Key] = merged.GetValueOrDefault(row.Key) + row.Value;
        }

        var counters = await _context.Set<Counter>()
            .AsNoTracking()
            .Where(x => x.Key.StartsWith(prefix))
            .Select(x => new { x.Key, x.Value })
            .ToListAsync(ct);
        foreach (var row in counters)
        {
            merged[row.Key] = merged.GetValueOrDefault(row.Key) + row.Value;
        }

        return [.. merged];
    }

    private static List<ClientNameCountModel> TopN(Dictionary<string, long> counts)
    {
        return [.. counts
            .OrderByDescending(x => x.Value)
            .Take(TopNames)
            .Select(x =>
                new ClientNameCountModel { Name = x.Key, Count = x.Value }),];
    }

    private static List<ClientVitalStatModel> BuildVitals(
        Dictionary<string, long> counts,
        Dictionary<string, long> durations,
        Dictionary<string, Dictionary<int, long>> buckets)
    {
        return [.. counts
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x =>
            {
                var count = x.Value;
                var scaledAvg = count > 0 ? (double)durations.GetValueOrDefault(x.Key) / count : 0;
                var scaledP75 = Quantile(buckets.GetValueOrDefault(x.Key), count, 0.75);

                return new ClientVitalStatModel
                {
                    Name = x.Key,
                    SampleCount = count,
                    AvgValue = Unscale(x.Key, scaledAvg),
                    P75Value = Unscale(x.Key, scaledP75),
                };
            }),];
    }

    // CLS is folded ×1000 to share the integer histogram; unscale it back to its unitless form for display.
    private static double Unscale(string vital, double value)
    {
        if (string.Equals(vital, "CLS", StringComparison.OrdinalIgnoreCase))
        {
            return value / 1000;
        }

        return value;
    }

    private static double Quantile(Dictionary<int, long>? bucketCounts, long total, double q)
    {
        if (bucketCounts is null || total == 0)
        {
            return 0;
        }

        var target = q * total;
        long cumulative = 0;
        foreach (var bound in ClientEventKeys.Buckets)
        {
            cumulative += bucketCounts.GetValueOrDefault(bound);
            if (cumulative >= target)
            {
                // The overflow bucket (int.MaxValue) reports the last real bound rather than a nonsense number.
                return bound == int.MaxValue ? ClientEventKeys.Buckets[^2] : bound;
            }
        }

        return ClientEventKeys.Buckets[^2];
    }

    private static ClientHistoryAccumulator Hour(Dictionary<string, ClientHistoryAccumulator> history, string hour)
    {
        if (!history.TryGetValue(hour, out var acc))
        {
            acc = new ClientHistoryAccumulator();
            history[hour] = acc;
        }

        return acc;
    }

    private static Dictionary<int, long> Bucket(Dictionary<string, Dictionary<int, long>> buckets, string vital)
    {
        if (!buckets.TryGetValue(vital, out var set))
        {
            set = [];
            buckets[vital] = set;
        }

        return set;
    }

    private sealed class ClientHistoryAccumulator
    {
        public long Errors { get; private set; }

        public long Logs { get; private set; }

        public long Events { get; private set; }

        public long Vitals { get; private set; }

        public void Add(string typeToken, long value)
        {
            if (string.Equals(typeToken, ClientEventKeys.TypeToken(ClientEventType.Error), StringComparison.Ordinal))
            {
                Errors += value;
            }
            else if (string.Equals(typeToken, ClientEventKeys.TypeToken(ClientEventType.Log), StringComparison.Ordinal))
            {
                Logs += value;
            }
            else if (string.Equals(typeToken, ClientEventKeys.TypeToken(ClientEventType.Event), StringComparison.Ordinal))
            {
                Events += value;
            }
            else if (string.Equals(typeToken, ClientEventKeys.TypeToken(ClientEventType.Vital), StringComparison.Ordinal))
            {
                Vitals += value;
            }
        }
    }
}
