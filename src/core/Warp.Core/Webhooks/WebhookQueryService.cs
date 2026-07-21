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

    private readonly TContext _context;

    public WebhookQueryService(TContext context) => _context = context;

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
                })
            .ToPagedListAsync(request);
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
                    LastActivityAt = g.Max(x => x.CreatedAt),
                })
            .ToListAsync(ct);

        return
        [
            .. rows
                .OrderByDescending(x => x.LastActivityAt)
                .ThenBy(x => x.Key, StringComparer.Ordinal),
        ];
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
