using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.ErrorGrouping;
using Warp.Core.Models;

namespace Warp.Core.Services;

/// <summary>
/// Reads the error grouping / Issues surfaces (§8.29). List + detail project the durable <see cref="ErrorGroup"/>
/// rows; the detail's trend is folded from the durable <c>errorgroup:</c> hourly Statistic keys so it survives
/// raw-row cleanup (§8.22). <c>IsNew</c> / <c>IsRegressed</c> are computed here off the injected
/// <see cref="TimeProvider"/>.
/// </summary>
public sealed class ErrorGroupQueryService<TContext> : IErrorGroupQueryService
    where TContext : DbContext
{
    private const int TrendHours = 24;

    private readonly TContext _context;
    private readonly TimeProvider _timeProvider;

    public ErrorGroupQueryService(TContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task<ErrorGroupListModel> GetGroups(ErrorSource? source, ErrorGroupStatus? status, string? application, ErrorKind? kind, int page, int pageSize, CancellationToken ct)
    {
        var size = pageSize is > 0 and <= 200 ? pageSize : 50;
        var index = page < 0 ? 0 : page;

        var query = _context.Set<ErrorGroup>().AsNoTracking();

        if (source is not null)
        {
            query = query.Where(x => x.Source == source);
        }

        if (status is not null)
        {
            query = query.Where(x => x.Status == status);
        }

        if (application is not null)
        {
            query = query.Where(x => x.Application == application);
        }

        if (kind is not null)
        {
            query = query.Where(x => x.Kind == kind);
        }

        var total = await query.CountAsync(ct);
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var groups = await query
            .OrderByDescending(x => x.LastSeenAt)
            .Skip(index * size)
            .Take(size)
            .Select(x =>
                new
                {
                    x.Fingerprint,
                    x.Source,
                    x.Kind,
                    x.ExceptionType,
                    x.Title,
                    x.Culprit,
                    x.StatusCode,
                    x.Application,
                    x.FirstSeenAt,
                    x.LastSeenAt,
                    x.Count,
                    x.Status,
                    x.StatusChangedAt,
                })
            .ToListAsync(ct);

        var items = groups.ConvertAll(x =>
            new ErrorGroupSummaryModel
            {
                Fingerprint = x.Fingerprint,
                Source = x.Source,
                Kind = x.Kind,
                ExceptionType = x.ExceptionType,
                Title = x.Title,
                Culprit = x.Culprit,
                StatusCode = x.StatusCode,
                Application = x.Application,
                FirstSeenAt = x.FirstSeenAt,
                LastSeenAt = x.LastSeenAt,
                Count = x.Count,
                Status = x.Status,
                IsNew = IsNew(x.FirstSeenAt, now),
                IsRegressed = IsRegressed(x.Status, x.StatusChangedAt),
            });

        return new ErrorGroupListModel { Items = items, Total = total };
    }

    public async Task<ErrorGroupDetailModel?> GetGroup(string fingerprint, CancellationToken ct)
    {
        var group = await _context.Set<ErrorGroup>()
            .AsNoTracking()
            .Where(x => x.Fingerprint == fingerprint)
            .FirstOrDefaultAsync(ct);

        if (group is null)
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        return new ErrorGroupDetailModel
        {
            Fingerprint = group.Fingerprint,
            Source = group.Source,
            Kind = group.Kind,
            ExceptionType = group.ExceptionType,
            Title = group.Title,
            Culprit = group.Culprit,
            StatusCode = group.StatusCode,
            Application = group.Application,
            FirstSeenAt = group.FirstSeenAt,
            LastSeenAt = group.LastSeenAt,
            Count = group.Count,
            Status = group.Status,
            IsNew = IsNew(group.FirstSeenAt, now),
            IsRegressed = IsRegressed(group.Status, group.StatusChangedAt),
            LastSample = group.LastSample,
            SampleTraceId = group.SampleTraceId,
            FirstSeenVersion = group.FirstSeenVersion,
            LastSeenVersion = group.LastSeenVersion,
            Environment = group.Environment,
            RecentSamples = ParseSamples(group.RecentSamples),
            Trend = await LoadTrendAsync(fingerprint, ct),
        };
    }

    private static List<ErrorSampleModel> ParseSamples(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ErrorSampleModel>>(json, ErrorSampleJson.Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<ErrorGroupTrendPoint>> LoadTrendAsync(string fingerprint, CancellationToken ct)
    {
        var prefix = ErrorGroupKeys.HourlyScanPrefix(fingerprint);

        // Plain StartsWith translates to SQL LIKE (codebase convention, cf. ClientEventQueryService).
        var rows = await _context.Set<Statistic>()
            .AsNoTracking()
            .Where(x => x.Key.StartsWith(prefix))
            .Select(x => new { x.Key, x.Value })
            .ToListAsync(ct);

        var byHour = new Dictionary<DateTime, long>();
        foreach (var row in rows)
        {
            var bucket = row.Key[(row.Key.LastIndexOf(':') + 1)..];
            if (ErrorGroupKeys.TryParseHour(bucket, out var hour))
            {
                byHour[hour] = byHour.GetValueOrDefault(hour) + row.Value;
            }
        }

        return [.. byHour
            .OrderBy(x => x.Key)
            .TakeLast(TrendHours)
            .Select(x =>
                new ErrorGroupTrendPoint { Hour = x.Key, Count = x.Value }),];
    }

    private static bool IsNew(DateTime firstSeenAt, DateTime now)
        => firstSeenAt >= now.AddHours(-24);

    private static bool IsRegressed(ErrorGroupStatus status, DateTime? statusChangedAt)
        => status == ErrorGroupStatus.Unresolved && statusChangedAt is not null;
}

/// <summary>Shared camelCase options for the <c>RecentSamples</c> JSON (§8.29) — non-generic to avoid a static field in a generic type (S2743).</summary>
internal static class ErrorSampleJson
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}
