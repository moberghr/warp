using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Warp.Core.Data.Entities;

namespace Warp.Core.BackgroundServices;

/// <summary>
/// Read-only dashboard queries for the BackgroundServices addon.
/// All methods use <c>AsNoTracking()</c> and <c>.Select()</c> projections (§5.3, §6.4).
/// No <c>_context.Set&lt;&gt;()</c> subqueries inside <c>.Select()</c> (§5.2).
/// </summary>
public class BackgroundServiceQueryService<TContext> : IBackgroundServiceQueryService
    where TContext : DbContext
{
    private readonly DbContext _context;

    public BackgroundServiceQueryService(TContext context)
        : this((DbContext)context)
    {
    }

    // The Warp context (rule 2.14): AddWarp passes IWarpContext.Context, which is not a TContext.
    internal BackgroundServiceQueryService(DbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<BackgroundServiceListItemDto>> ListAsync(CancellationToken ct)
    {
        // Load definitions first so we have DeclaredScope without subqueries inside GroupBy.
        // Order by FirstSeenAt so the dashboard list page renders services in creation order
        // (oldest registered service first). Adding a Name tiebreaker handles the rare case
        // of two definitions inserted in the same tick.
        var definitions = await _context.Set<BackgroundServiceDefinition>()
            .AsNoTracking()
            .OrderBy(d => d.FirstSeenAt)
            .ThenBy(d => d.Name)
            .Select(d => new
            {
                d.Name,
                d.DeclaredScope,
            })
            .ToListAsync(ct);

        if (definitions.Count == 0)
        {
            return [];
        }

        // Aggregate instance counts per service name via GroupBy. EF Core translates
        // GroupBy aggregations to SQL GROUP BY — no _context.Set<>() subquery anti-pattern.
        var aggregates = await _context.Set<BackgroundServiceInstance>()
            .AsNoTracking()
            .GroupBy(x => x.ServiceName)
            .Select(g => new
            {
                ServiceName = g.Key,
                RunningCount = g.Count(x => x.Status == BackgroundServiceStatus.Running),
                WaitingCount = g.Count(x => x.Status == BackgroundServiceStatus.Waiting),
                FaultedCount = g.Count(x => x.Status == BackgroundServiceStatus.Faulted),
                ConfigurationMismatchCount = g.Count(x => x.Status == BackgroundServiceStatus.ConfigurationMismatch),
                TotalInstances = g.Count(),
                TotalRestartCount = g.Sum(x => x.RestartCount),
            })
            .ToListAsync(ct);

        // Load the most-recent LastError per service for faulted instances.
        // Two-step: first get the service names that have any faulted instance, then load
        // the most-recent LastError value. This avoids a correlated subquery inside .Select().
        var faultedServiceNames = aggregates
            .Where(a => a.FaultedCount > 0)
            .Select(a => a.ServiceName)
            .ToList();

        Dictionary<string, string?> lastErrors;

        if (faultedServiceNames.Count > 0)
        {
            var rawErrors = await _context.Set<BackgroundServiceInstance>()
                .AsNoTracking()
                .Where(x => faultedServiceNames.Contains(x.ServiceName))
                .Where(x => x.Status == BackgroundServiceStatus.Faulted)
                .Where(x => x.LastError != null)
                .OrderByDescending(x => x.LastErrorAt)
                .Select(x => new { x.ServiceName, x.LastError })
                .ToListAsync(ct);

            // Keep the first (most-recent) entry per service name via GroupBy.
            lastErrors = rawErrors
                .GroupBy(x => x.ServiceName, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().LastError, StringComparer.Ordinal);
        }
        else
        {
            lastErrors = [];
        }

        var aggregatesByName = aggregates.ToDictionary(a => a.ServiceName, StringComparer.Ordinal);

        List<BackgroundServiceListItemDto> result = [];

        foreach (var d in definitions)
        {
            aggregatesByName.TryGetValue(d.Name, out var agg);
            lastErrors.TryGetValue(d.Name, out var lastError);

            result.Add(new BackgroundServiceListItemDto
            {
                Name = d.Name,
                Scope = d.DeclaredScope,
                RunningCount = agg?.RunningCount ?? 0,
                WaitingCount = agg?.WaitingCount ?? 0,
                FaultedCount = agg?.FaultedCount ?? 0,
                ConfigurationMismatchCount = agg?.ConfigurationMismatchCount ?? 0,
                TotalInstances = agg?.TotalInstances ?? 0,
                TotalRestartCount = agg?.TotalRestartCount ?? 0,
                LastErrorType = ExtractExceptionType(lastError),
            });
        }

        return result;
    }

    public async Task<BackgroundServiceDetailDto?> GetAsync(string name, CancellationToken ct)
    {
        // Step 1: load Definition. Return null when no row exists.
        var definition = await _context.Set<BackgroundServiceDefinition>()
            .AsNoTracking()
            .Where(d => d.Name == name)
            .Select(d => new
            {
                d.Name,
                d.DeclaredScope,
                d.FirstSeenAt,
                d.LastSeenAt,
            })
            .FirstOrDefaultAsync(ct);

        if (definition is null)
        {
            return null;
        }

        // Step 2: load instances for this service via nav-property projection — ServerName
        // is resolved through the Instance.Server FK so EF Core emits a single LEFT JOIN.
        // Order by StartedAt so per-instance tabs render in creation order (longest-running
        // first, with a ServerId tiebreaker for instances that started in the same tick).
        var instances = await _context.Set<BackgroundServiceInstance>()
            .AsNoTracking()
            .Where(i => i.ServiceName == name)
            .OrderBy(i => i.StartedAt)
            .ThenBy(i => i.ServerId)
            .Select(i => new BackgroundServiceInstanceDto
            {
                ServerId = i.ServerId,
                ServerName = i.Server == null ? null : i.Server.ServerName,
                ServiceName = i.ServiceName,
                DeclaredScope = i.DeclaredScope,
                Status = i.Status,
                StartedAt = i.StartedAt,
                LastHeartbeatAt = i.LastHeartbeatAt,
                LastError = i.LastError,
                LastErrorAt = i.LastErrorAt,
                RestartCount = i.RestartCount,
            })
            .ToListAsync(ct);

        return new BackgroundServiceDetailDto
        {
            Name = definition.Name,
            DeclaredScope = definition.DeclaredScope,
            FirstSeenAt = definition.FirstSeenAt,
            LastSeenAt = definition.LastSeenAt,
            Instances = instances,
        };
    }

    public async Task<BackgroundServiceLeaseDto?> GetLeaseAsync(string name, CancellationToken ct)
    {
        return await _context.Set<BackgroundServiceLease>()
            .AsNoTracking()
            .Where(l => l.ServiceName == name)
            .Select(l => new BackgroundServiceLeaseDto
            {
                ServiceName = l.ServiceName,
                HolderServerId = l.HolderServerId,
                HolderServerName = l.HolderServer == null ? null : l.HolderServer.ServerName,
                LeaseExpiresAt = l.LeaseExpiresAt,
            })
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<BackgroundServiceLogDto>> GetLogsAsync(
        string name,
        BackgroundServiceLogSource? source,
        LogLevel? minLevel,
        long? fromId,
        int limit,
        CancellationToken ct)
    {
        // Clamp limit to prevent runaway queries.
        var effectiveLimit = Math.Min(limit, 500);

        var query = _context.Set<BackgroundServiceLog>()
            .AsNoTracking()
            .Where(l => l.ServiceName == name);

        if (source.HasValue)
        {
            query = query.Where(l => l.Source == source.Value);
        }

        if (minLevel.HasValue)
        {
            query = query.Where(l => l.Level >= minLevel.Value);
        }

        if (fromId.HasValue)
        {
            query = query.Where(l => l.Id > fromId.Value);
        }

        // ServerName resolved through the BackgroundServiceLog.Server nav property — EF
        // emits a single LEFT JOIN per query.
        return await query
            .OrderByDescending(l => l.Id)
            .Take(effectiveLimit)
            .Select(l => new BackgroundServiceLogDto
            {
                Id = l.Id,
                ServerId = l.ServerId,
                ServerName = l.Server == null ? null : l.Server.ServerName,
                ServiceName = l.ServiceName,
                Timestamp = l.Timestamp,
                Level = l.Level,
                Source = l.Source,
                Message = l.Message,
                ExceptionType = l.ExceptionType,
                ExceptionMessage = l.ExceptionMessage,
            })
            .ToListAsync(ct);
    }

    // Extracts just the exception type name from a LastError string that was stored as
    // "ExceptionType.FullName: message" by RecordFaultAsync. Returns null when input is null.
    private static string? ExtractExceptionType(string? lastError)
    {
        if (string.IsNullOrEmpty(lastError))
        {
            return null;
        }

        var colonIndex = lastError.IndexOf(':', StringComparison.Ordinal);

        return colonIndex > 0 ? lastError[..colonIndex].Trim() : lastError.Trim();
    }
}
