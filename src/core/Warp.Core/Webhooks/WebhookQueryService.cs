using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;

namespace Warp.Core.Webhooks;

/// <summary>
/// <see cref="IWebhookQueryService"/> over the user's <typeparamref name="TContext"/>. List/summary use
/// <c>.Select()</c> projections (§6.4); the single-row detail loads the row then redacts in memory (the
/// <c>Authorization</c>-class header redaction is a JSON parse that has no EF translation). Redaction is
/// unconditional — the per-delivery <see cref="WebhookDelivery.Secret"/> value never leaves the service
/// and the header denylist values are replaced with <c>***</c> (§1.2).
/// </summary>
public class WebhookQueryService<TContext> : IWebhookQueryService
    where TContext : DbContext
{
    // Bounds the deliveries list page so a large table can't return an unbounded result set.
    private const int MaxPageSize = 200;

    // Endpoint stats window (hours) and the most settled rows read for it (newest first): a dashboard read, bounded.
    private const int StatsWindowHours = 24;
    private const int MaxStatsRows = 50_000;

    private readonly DbContext _context;
    private readonly TimeProvider _timeProvider;

    public WebhookQueryService(TContext context)
        : this(context, TimeProvider.System)
    {
    }

    public WebhookQueryService(TContext context, TimeProvider timeProvider)
        : this((DbContext)context, timeProvider)
    {
    }

    // The Warp context (rule 2.14): AddWarp passes IWarpContext.Context, which is not a TContext.
    internal WebhookQueryService(DbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task<PagedList<WebhookDeliveryListItem>> GetDeliveries(WebhookDeliveryFilter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var request = new BaseListRequest
        {
            Page = filter.Page < 0 ? 0 : filter.Page,
            PageSize = filter.PageSize is > 0 ? Math.Min(filter.PageSize, MaxPageSize) : 20,
        };

        return await ApplyFilter(_context.Set<WebhookDelivery>().AsNoTracking(), filter)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Select(x =>
                new WebhookDeliveryListItem
                {
                    Id = x.Id,
                    EventType = x.EventType,
                    EventId = x.EventId,
                    Url = x.Url,
                    GroupName = x.GroupName,
                    Reference = x.Reference,
                    Status = x.Status,
                    SigningMode = x.SigningMode,
                    AttemptCount = x.AttemptCount,
                    NextAttemptAt = x.NextAttemptAt,
                    CreatedAt = x.CreatedAt,
                    LastStatusCode = x.LastStatusCode,
                    LastFailureReason = x.LastFailureReason,
                    LastAttemptAt = x.LastAttemptAt,
                })
            .ToPagedListAsync(request, ct);
    }

    public async Task<IReadOnlyList<WebhookGroupModel>> GetGroups(WebhookGroupBy by, CancellationToken ct = default)
    {
        // Group by the chosen dimension and fold the per-status counts in one pass. Endpoint groups by
        // GroupName, falling back to Url when a delivery carried no group (same identity the list shows).
        var grouped = by == WebhookGroupBy.EventType
            ? _context.Set<WebhookDelivery>().AsNoTracking().GroupBy(x => x.EventType)
            : _context.Set<WebhookDelivery>().AsNoTracking().GroupBy(x => x.GroupName ?? x.Url);

        var rows = await grouped
            .Select(g =>
                new WebhookGroupModel
                {
                    Key = g.Key,
                    Total = g.Count(),
                    Pending = g.Count(x => x.Status == WebhookDeliveryStatus.Pending),
                    Delivered = g.Count(x => x.Status == WebhookDeliveryStatus.Delivered),
                    Exhausted = g.Count(x => x.Status == WebhookDeliveryStatus.Exhausted),
                    Parked = g.Count(x => x.Status == WebhookDeliveryStatus.Parked),
                    Cancelled = g.Count(x => x.Status == WebhookDeliveryStatus.Cancelled),
                    LastActivityAt = g.Max(x => x.CreatedAt),
                })
            .ToListAsync(ct);

        if (by == WebhookGroupBy.Endpoint)
        {
            await AddEndpointHealthAsync(rows, ct);
        }

        return
        [
            .. rows
                .OrderByDescending(x => x.LastActivityAt)
                .ThenBy(x => x.Key, StringComparer.Ordinal),
        ];
    }

    // Endpoint health (8.1): the health record of every group that has one, plus success rate, p95 send-to-delivered
    // time and last success/failure from the deliveries that settled in the last 24 hours. Two-step reads joined in
    // memory (§5.2); the stats read is capped at the newest MaxStatsRows rows.
    private async Task AddEndpointHealthAsync(List<WebhookGroupModel> rows, CancellationToken ct)
    {
        var endpoints = await _context.Set<WebhookEndpoint>()
            .AsNoTracking()
            .ToDictionaryAsync(x => x.Name, StringComparer.Ordinal, ct);

        var since = _timeProvider.GetUtcNow().UtcDateTime.AddHours(-StatsWindowHours);
        var recent = await _context.Set<WebhookDelivery>()
            .AsNoTracking()
            .Where(x => x.LastAttemptAt != null)
            .Where(x => x.LastAttemptAt >= since)
            .OrderByDescending(x => x.LastAttemptAt)
            .Take(MaxStatsRows)
            .Select(x =>
                new
                {
                    Key = x.GroupName ?? x.Url,
                    x.Status,
                    x.CreatedAt,
                    x.LastAttemptAt,
                    x.LastFailureReason,
                })
            .ToListAsync(ct);

        var stats = recent
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (endpoints.TryGetValue(row.Key, out var endpoint))
            {
                row.Health = endpoint.State;
                row.FailingSince = endpoint.FailingSince;
                row.NextProbeAt = endpoint.NextProbeAt;
                row.LastSuccessAt = endpoint.LastSuccessAt;
                row.LastFailureAt = endpoint.LastFailureAt;
            }

            if (!stats.TryGetValue(row.Key, out var group))
            {
                continue;
            }

            var delivered = group
                .Where(x => x.Status == WebhookDeliveryStatus.Delivered)
                .ToList();
            var settled = delivered.Count + group.Count(x => x.Status is WebhookDeliveryStatus.Exhausted or WebhookDeliveryStatus.Parked);

            row.SuccessRate = settled == 0 ? null : (double)delivered.Count / settled;
            row.P95DeliveryMs = Percentile95(delivered.Select(x => (x.LastAttemptAt!.Value - x.CreatedAt).TotalMilliseconds));
            row.LastSuccessAt = Latest(row.LastSuccessAt, delivered.Max(x => x.LastAttemptAt));
            row.LastFailureAt = Latest(row.LastFailureAt, group.Where(x => x.LastFailureReason != null).Max(x => x.LastAttemptAt));
        }
    }

    private static DateTime? Latest(DateTime? a, DateTime? b)
    {
        if (a is null || b is null)
        {
            return a ?? b;
        }

        return a > b ? a : b;
    }

    // Nearest-rank p95; null for an empty set.
    private static double? Percentile95(IEnumerable<double> values)
    {
        var sorted = values
            .Order()
            .ToArray();

        if (sorted.Length == 0)
        {
            return null;
        }

        var rank = (int)Math.Ceiling(0.95 * sorted.Length) - 1;

        return Math.Max(0, sorted[Math.Clamp(rank, 0, sorted.Length - 1)]);
    }

    public async Task<IReadOnlyList<WebhookDeliveryHistoryPoint>> GetDeliveryHistory(WebhookDeliveryFilter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // Bucket by UTC hour DB-side (Year/Month/Day/Hour translate on both Npgsql and SqlServer), counting
        // per current status — deliveries are durable rows (not lossy), so this is exact over the retention.
        var buckets = await ApplyFilter(_context.Set<WebhookDelivery>().AsNoTracking(), filter)
            .GroupBy(x => new { x.CreatedAt.Year, x.CreatedAt.Month, x.CreatedAt.Day, x.CreatedAt.Hour })
            .Select(g =>
                new
                {
                    g.Key.Year,
                    g.Key.Month,
                    g.Key.Day,
                    g.Key.Hour,
                    Delivered = g.Count(x => x.Status == WebhookDeliveryStatus.Delivered),
                    Exhausted = g.Count(x => x.Status == WebhookDeliveryStatus.Exhausted),
                    Pending = g.Count(x => x.Status == WebhookDeliveryStatus.Pending),
                    Parked = g.Count(x => x.Status == WebhookDeliveryStatus.Parked),
                    Cancelled = g.Count(x => x.Status == WebhookDeliveryStatus.Cancelled),
                    Total = g.Count(),
                })
            .ToListAsync(ct);

        return
        [
            .. buckets
                .Select(x =>
                    new WebhookDeliveryHistoryPoint
                    {
                        Hour = new DateTime(x.Year, x.Month, x.Day, x.Hour, 0, 0, DateTimeKind.Utc),
                        Delivered = x.Delivered,
                        Exhausted = x.Exhausted,
                        Pending = x.Pending,
                        Parked = x.Parked,
                        Cancelled = x.Cancelled,
                        Total = x.Total,
                    })
                .OrderBy(x => x.Hour),
        ];
    }

    private static IQueryable<WebhookDelivery> ApplyFilter(IQueryable<WebhookDelivery> query, WebhookDeliveryFilter filter)
    {
        if (filter.Status is not null)
        {
            query = query.Where(x => x.Status == filter.Status);
        }

        if (!string.IsNullOrWhiteSpace(filter.EventType))
        {
            query = query.Where(x => x.EventType == filter.EventType);
        }

        if (!string.IsNullOrWhiteSpace(filter.Reference))
        {
            query = query.Where(x => x.Reference == filter.Reference);
        }

        if (!string.IsNullOrWhiteSpace(filter.GroupName))
        {
            query = query.Where(x => (x.GroupName ?? x.Url) == filter.GroupName);
        }

        if (filter.Since is not null)
        {
            query = query.Where(x => x.CreatedAt >= filter.Since);
        }

        if (filter.Until is not null)
        {
            query = query.Where(x => x.CreatedAt <= filter.Until);
        }

        return query;
    }

    public async Task<WebhookDeliveryDetail?> GetDeliveryDetail(Guid id, CancellationToken ct = default)
    {
        var row = await _context.Set<WebhookDelivery>()
            .AsNoTracking()
            .Where(x => x.Id == id)
            .FirstOrDefaultAsync(ct);

        if (row is null)
        {
            return null;
        }

        // Attempt timeline = the AdapterCallLog rows keyed by CorrelationId (attempts are adapter calls,
        // not a second table). Lead the predicate with AdapterName — the composite index leads with that
        // column, so filtering on CorrelationId alone forfeits the index seek. The delivery id is a
        // globally-unique Guid, so (AdapterName, CorrelationId) still selects exactly this delivery's attempts.
        // Two-step (load the row, then load its attempts) — no _context.Set<>() subquery in a projection (§5.2).
        var correlationId = id.ToString();
        var attempts = await _context.Set<AdapterCallLog>()
            .AsNoTracking()
            .Where(x => x.AdapterName == WebhookConstants.AdapterName)
            .Where(x => x.CorrelationId == correlationId)
            .OrderBy(x => x.Timestamp)
            .Select(x =>
                new WebhookAttemptItem
                {
                    CallId = x.Id,
                    Timestamp = x.Timestamp,
                    DurationMs = x.DurationMs,
                    Outcome = x.Outcome,
                    StatusCode = x.StatusCode,
                    ExceptionType = x.ExceptionType,
                })
            .ToListAsync(ct);

        return new WebhookDeliveryDetail
        {
            Id = row.Id,
            EventType = row.EventType,
            EventId = row.EventId,
            Url = row.Url,
            HeadersJson = RedactHeaders(row.HeadersJson),
            GroupName = row.GroupName,
            Reference = row.Reference,
            PayloadJson = row.PayloadJson,
            SigningMode = row.SigningMode,
            HasSecret = !string.IsNullOrEmpty(row.Secret),
            RetryScheduleSeconds = [.. row.RetrySchedule.Select(x => x.TotalSeconds)],
            SuccessCodesJson = row.SuccessCodesJson,
            Status = row.Status,
            AttemptCount = row.AttemptCount,
            NextAttemptAt = row.NextAttemptAt,
            CreatedAt = row.CreatedAt,
            ExpireAt = row.ExpireAt,
            LastStatusCode = row.LastStatusCode,
            LastFailureReason = row.LastFailureReason,
            LastAttemptAt = row.LastAttemptAt,
            TrackEndpointHealth = row.TrackEndpointHealth,
            Attempts = attempts,
        };
    }

    public async Task<WebhookDeliverySummary> GetSummary(CancellationToken ct = default)
    {
        var counts = await _context.Set<WebhookDelivery>()
            .AsNoTracking()
            .GroupBy(x => x.Status)
            .Select(g =>
                new
                {
                    Status = g.Key,
                    Count = g.Count(),
                })
            .ToListAsync(ct);

        var summary = new WebhookDeliverySummary();

        foreach (var row in counts)
        {
            summary.Total += row.Count;

            switch (row.Status)
            {
                case WebhookDeliveryStatus.Pending:
                    summary.Pending += row.Count;
                    break;
                case WebhookDeliveryStatus.Delivered:
                    summary.Delivered += row.Count;
                    break;
                case WebhookDeliveryStatus.Exhausted:
                    summary.Exhausted += row.Count;
                    break;
                case WebhookDeliveryStatus.Parked:
                    summary.Parked += row.Count;
                    break;
                case WebhookDeliveryStatus.Cancelled:
                    summary.Cancelled += row.Count;
                    break;
                default:
                    break;
            }
        }

        return summary;
    }

    private static string? RedactHeaders(string? headersJson) => WebhookHeaderRedaction.Redact(headersJson);
}

/// <summary>
/// Non-generic header redaction shared by every closed <see cref="WebhookQueryService{TContext}"/> (a
/// static denylist on the generic type would not be shared across close constructions — S2743). Mirrors
/// the adapters' default header denylist (<c>WarpAdapterOptions.RedactedHeaders</c>): these values render
/// redacted on every read surface regardless of what the host stored on the delivery row.
/// </summary>
internal static class WebhookHeaderRedaction
{
    private const string RedactedValue = "***";

    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Proxy-Authorization",
        "Cookie",
        "Set-Cookie",
        "X-Api-Key",
    };

    // Redacts Authorization-class values inside the stored headers JSON object. On a malformed blob the
    // whole value is dropped rather than risk leaking an un-redacted secret header verbatim.
    internal static string? Redact(string? headersJson)
    {
        if (string.IsNullOrWhiteSpace(headersJson))
        {
            return null;
        }

        Dictionary<string, string>? headers;
        try
        {
            headers = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
        }
        catch (JsonException)
        {
            return null;
        }

        if (headers is null || headers.Count == 0)
        {
            return null;
        }

        foreach (var key in headers.Keys.Where(SensitiveHeaders.Contains).ToArray())
        {
            headers[key] = RedactedValue;
        }

        return JsonSerializer.Serialize(headers);
    }
}
