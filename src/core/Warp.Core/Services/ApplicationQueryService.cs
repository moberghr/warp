using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Warp.Core.Data.Entities;
using Warp.Core.Models;

namespace Warp.Core.Services;

/// <summary>
/// <see cref="IApplicationQueryService"/> over the user's <typeparamref name="TContext"/>. Reads the two
/// instance tables independently (<c>Server</c> + <c>ApplicationInstance</c>) with <c>AsNoTracking()</c> +
/// <c>.Select()</c> projections and merges them in memory into the unified <see cref="InstanceView"/> shape
/// (§5.2 two-step read, no cross-table SQL union). A single liveness window —
/// <c>WarpConfiguration.ApplicationInstanceStaleGrace</c> — classifies both server and non-server instances
/// as live/stale (servers heartbeat faster on the 3s server <c>Heartbeat</c>, but the same generous grace
/// still marks a healthy server live, so one window keeps the projection simple).
/// </summary>
public class ApplicationQueryService<TContext> : IApplicationQueryService
    where TContext : DbContext
{
    // Cap the per-instance lifecycle timeline so the detail page stays bounded; older rows remain within
    // the retention window until swept.
    private const int RecentEventsLimit = 50;

    private readonly TContext _context;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _liveThreshold;

    public ApplicationQueryService(TContext context, TimeProvider timeProvider, IOptions<WarpConfiguration> configuration)
    {
        _context = context;
        _timeProvider = timeProvider;
        _liveThreshold = configuration.Value.ApplicationInstanceStaleGrace;
    }

    public async Task<IReadOnlyList<ApplicationSummaryModel>> GetApplications(CancellationToken ct = default)
    {
        var instances = await LoadInstancesAsync(application: null, ct);

        return
        [
            .. instances
                .GroupBy(x => x.Application, StringComparer.Ordinal)
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => Summarize(x.Key, [.. x])),
        ];
    }

    public async Task<ApplicationDetailModel?> GetApplicationDetail(string application, CancellationToken ct = default)
    {
        var instances = await LoadInstancesAsync(application, ct);
        if (instances.Count == 0)
        {
            return null;
        }

        return new ApplicationDetailModel
        {
            Name = application,
            Instances = instances,
            Versions = DistinctSorted(instances, x => x.Version),
            Environments = DistinctSorted(instances, x => x.Environment),
        };
    }

    public async Task<ApplicationInstanceDetailModel?> GetInstanceDetail(string application, Guid instanceId, CancellationToken ct = default)
    {
        var instances = await LoadInstancesAsync(application, ct);

        var instance = instances.FirstOrDefault(x => x.Id == instanceId);
        if (instance is null)
        {
            return null;
        }

        var recentEvents = await _context.Set<ApplicationInstanceLog>()
            .AsNoTracking()
            .Where(x => x.InstanceId == instanceId)
            .OrderByDescending(x => x.Timestamp)
            .ThenByDescending(x => x.Id)
            .Take(RecentEventsLimit)
            .Select(x =>
                new ApplicationInstanceLogModel
                {
                    Id = x.Id,
                    InstanceId = x.InstanceId,
                    ApplicationName = x.ApplicationName,
                    Timestamp = x.Timestamp,
                    EventType = x.EventType,
                    Message = x.Message,
                })
            .ToListAsync(ct);

        return new ApplicationInstanceDetailModel
        {
            Instance = instance,
            RecentEvents = recentEvents,
        };
    }

    // Reads Server rows (with a non-null Application — the feature is opt-in) and ApplicationInstance rows
    // separately, projects each into the unified InstanceView, and merges them in memory. A supplied
    // application scopes both reads to that name. IsLive is computed once against a single "now" snapshot.
    private async Task<List<InstanceView>> LoadInstancesAsync(string? application, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var liveFloor = now - _liveThreshold;

        var serverQuery = _context.Set<Server>()
            .AsNoTracking()
            .Where(x => x.Application != null);

        if (application is not null)
        {
            serverQuery = serverQuery.Where(x => x.Application == application);
        }

        var servers = await serverQuery
            .Select(x =>
                new
                {
                    x.Id,
                    x.Application,
                    x.ServerName,
                    x.StartedTime,
                    x.LastHeartbeatTime,
                    x.CpuUsagePercent,
                    x.MemoryWorkingSetBytes,
                    x.Version,
                    x.Environment,
                })
            .ToListAsync(ct);

        var instanceQuery = _context.Set<ApplicationInstance>().AsNoTracking();

        if (application is not null)
        {
            instanceQuery = instanceQuery.Where(x => x.ApplicationName == application);
        }

        var nonServers = await instanceQuery
            .Select(x =>
                new
                {
                    x.Id,
                    x.ApplicationName,
                    x.MachineName,
                    x.StartedAt,
                    x.LastHeartbeatAt,
                    x.CpuUsagePercent,
                    x.MemoryWorkingSetBytes,
                    x.Version,
                    x.Environment,
                })
            .ToListAsync(ct);

        var result = new List<InstanceView>(servers.Count + nonServers.Count);

        foreach (var x in servers)
        {
            result.Add(new InstanceView
            {
                Id = x.Id,
                Application = x.Application!,
                MachineName = x.ServerName,
                StartedAt = x.StartedTime,
                LastHeartbeatAt = x.LastHeartbeatTime,
                CpuUsagePercent = x.CpuUsagePercent,
                MemoryWorkingSetBytes = x.MemoryWorkingSetBytes,
                IsServer = true,
                Version = x.Version,
                Environment = x.Environment,
                IsLive = x.LastHeartbeatTime > liveFloor,
            });
        }

        foreach (var x in nonServers)
        {
            result.Add(new InstanceView
            {
                Id = x.Id,
                Application = x.ApplicationName,
                MachineName = x.MachineName,
                StartedAt = x.StartedAt,
                LastHeartbeatAt = x.LastHeartbeatAt,
                CpuUsagePercent = x.CpuUsagePercent,
                MemoryWorkingSetBytes = x.MemoryWorkingSetBytes,
                IsServer = false,
                Version = x.Version,
                Environment = x.Environment,
                IsLive = x.LastHeartbeatAt > liveFloor,
            });
        }

        return result;
    }

    private static ApplicationSummaryModel Summarize(string name, List<InstanceView> instances)
    {
        var live = instances.Where(x => x.IsLive).ToList();

        var cpu = live.Where(x => x.CpuUsagePercent.HasValue).ToList();
        var memory = live.Where(x => x.MemoryWorkingSetBytes.HasValue).ToList();

        return new ApplicationSummaryModel
        {
            Name = name,
            InstanceCount = instances.Count,
            LiveInstanceCount = live.Count,
            TotalCpuUsagePercent = cpu.Count == 0 ? null : cpu.Sum(x => x.CpuUsagePercent!.Value),
            TotalMemoryWorkingSetBytes = memory.Count == 0 ? null : memory.Sum(x => x.MemoryWorkingSetBytes!.Value),
            Versions = DistinctSorted(instances, x => x.Version),
            Environments = DistinctSorted(instances, x => x.Environment),
        };
    }

    private static IReadOnlyList<string> DistinctSorted(List<InstanceView> instances, Func<InstanceView, string?> selector)
    {
        return
        [
            .. instances
                .Select(selector)
                .Where(x => x is not null)
                .Select(x => x!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal),
        ];
    }
}
