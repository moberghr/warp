using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Warp.Core.BackgroundServices;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;

namespace Warp.Worker.Services;

/// <summary>
/// Deletes expired jobs + logs, trims old hourly stats, deletes server logs past their
/// per-task retention, and caps RecurringJobLog history. Also handles count-based
/// cleanup when <see cref="WarpWorkerConfiguration.MaxExpirableJobCount"/> is set.
/// </summary>
public sealed class ExpirationCleanup<TContext> : IServerTask
    where TContext : DbContext
{
    private readonly TContext _context;
    private readonly TimeProvider _time;
    private readonly WarpWorkerConfiguration _configuration;
    private readonly IEnumerable<WarpBackgroundService> _backgroundServices;

    public ExpirationCleanup(
        TContext context,
        TimeProvider time,
        IOptions<WarpWorkerConfiguration> configuration,
        IEnumerable<WarpBackgroundService>? backgroundServices = null)
    {
        _context = context;
        _time = time;
        _configuration = configuration.Value;
        _backgroundServices = backgroundServices ?? [];
    }

    public string Name => "ExpirationCleanup";

    public string? LockKey => "warp:expiration-cleanup";

    public TimeSpan? DefaultInterval => _configuration.ExpirationCleanupInterval;

    public async Task<string?> ExecuteAsync(CancellationToken ct)
    {
        var timeExpired = await RunCleanupAsync(ct);
        var countCleaned = _configuration.MaxExpirableJobCount.HasValue
            ? await RunCountBasedCleanupAsync(_configuration.MaxExpirableJobCount.Value, _configuration.ExpirationBatchSize, ct)
            : 0;

        await CleanupRecurringJobLogsAsync(ct);
        await CleanupBackgroundServiceLogsAsync(ct);

        var total = timeExpired + countCleaned;
        if (total == 0)
        {
            return null;
        }

        return countCleaned > 0
            ? $"Cleaned up {timeExpired} expired + {countCleaned} over-threshold jobs"
            : $"Cleaned up {timeExpired} expired jobs";
    }

    internal async Task<int> RunCleanupAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var batchSize = _configuration.ExpirationBatchSize;

        // Only delete jobs that have no children at all. Internal nodes of an expired tree
        // wait until their (already-expired) children are cleaned in an earlier tick — this
        // prevents the self-FK fk_job_job_parent_job_id from firing when Take(batchSize)
        // would otherwise return a parent without all of its children. Trees drain one level
        // per tick.
        var expiredJobIds = await _context.Set<Job>()
            .Where(x => x.ExpireAt != null && x.ExpireAt < now)
            .Where(x => !x.ChildJobs.Any())
            .Select(x => x.Id)
            .Take(batchSize)
            .ToListAsync(ct);

        if (expiredJobIds.Count == 0)
        {
            return 0;
        }

        await _context.Set<JobLog>()
            .Where(x => expiredJobIds.Contains(x.JobId))
            .ExecuteDeleteAsync(ct);

        await _context.Set<Job>()
            .Where(x => expiredJobIds.Contains(x.Id))
            .ExecuteDeleteAsync(ct);

        // Hourly bucket rows (any key ending in :yyyy-MM-dd-HH) older than 7 days. Generic so
        // addon-defined hourly metrics get pruned with the same retention. Coarse SQL filter
        // narrows to keys with at least one ':', then the in-memory parse rejects keys whose
        // suffix isn't actually a date — so an addon writing :foo-bar-baz wouldn't be deleted.
        var hourlyCutoff = now.AddDays(-7);
        var candidateKeys = await _context.Set<Statistic>()
            .Where(x => EF.Functions.Like(x.Key, "%:%"))
            .Select(x => x.Key)
            .ToListAsync(ct);

        var staleKeys = candidateKeys
            .Where(k => TryParseHourlySuffix(k, out var hour) && hour < hourlyCutoff)
            .ToList();

        if (staleKeys.Count > 0)
        {
            await _context.Set<Statistic>()
                .Where(x => staleKeys.Contains(x.Key))
                .ExecuteDeleteAsync(ct);
        }

        var serverTasks = await _context.Set<ServerTask>()
            .Select(x => new { x.Id, x.IntervalSeconds })
            .ToListAsync(ct);

        foreach (var task in serverTasks)
        {
            var retentionSeconds = (task.IntervalSeconds ?? 60) * 300;
            var cutoff = now.AddSeconds(-retentionSeconds);
            await _context.Set<ServerLog>()
                .Where(x => x.ServerTaskId == task.Id && x.Timestamp < cutoff)
                .ExecuteDeleteAsync(ct);
        }

        await _context.Set<ServerLog>()
            .Where(x => x.ServerTaskId == null && x.Timestamp < now.AddDays(-1))
            .ExecuteDeleteAsync(ct);

        return expiredJobIds.Count;
    }

    private static bool TryParseHourlySuffix(string key, out DateTime hour)
    {
        hour = default;
        var lastColon = key.LastIndexOf(':');
        if (lastColon < 0)
        {
            return false;
        }

        return DateTime.TryParseExact(
            key.AsSpan(lastColon + 1),
            "yyyy-MM-dd-HH",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out hour);
    }

    internal async Task<int> RunCountBasedCleanupAsync(int maxCount, int batchSize, CancellationToken ct)
    {
        var totalDeleted = 0;

        while (true)
        {
            var expirableCount = await _context.Set<Job>()
                .Where(x => x.ExpireAt != null)
                .CountAsync(ct);

            if (expirableCount <= maxCount)
            {
                break;
            }

            var toDelete = Math.Min(expirableCount - maxCount, batchSize);

            // Same FK-safety constraint as RunCleanupAsync: only delete leaves so the
            // self-FK fk_job_job_parent_job_id can't fire on a parent whose children
            // happen to land in a different batch.
            var jobIds = await _context.Set<Job>()
                .Where(x => x.ExpireAt != null)
                .Where(x => !x.ChildJobs.Any())
                .OrderBy(x => x.ExpireAt)
                .Select(x => x.Id)
                .Take(toDelete)
                .ToListAsync(ct);

            if (jobIds.Count == 0)
            {
                break;
            }

            await _context.Set<JobLog>()
                .Where(x => jobIds.Contains(x.JobId))
                .ExecuteDeleteAsync(ct);

            await _context.Set<Job>()
                .Where(x => jobIds.Contains(x.Id))
                .ExecuteDeleteAsync(ct);

            totalDeleted += jobIds.Count;
        }

        return totalDeleted;
    }

    internal async Task CleanupRecurringJobLogsAsync(CancellationToken ct)
    {
        var recurringJobIds = await _context.Set<RecurringJobLog>()
            .GroupBy(l => l.RecurringJobId)
            .Where(g => g.Count() > 100)
            .Select(g => g.Key)
            .ToListAsync(ct);

        foreach (var recurringJobId in recurringJobIds)
        {
            var idsToKeep = await _context.Set<RecurringJobLog>()
                .Where(l => l.RecurringJobId == recurringJobId)
                .OrderByDescending(l => l.CreatedAt)
                .Take(100)
                .Select(l => l.Id)
                .ToListAsync(ct);

            await _context.Set<RecurringJobLog>()
                .Where(l => l.RecurringJobId == recurringJobId && !idsToKeep.Contains(l.Id))
                .ExecuteDeleteAsync(ct);
        }
    }

    internal async Task CleanupBackgroundServiceLogsAsync(CancellationToken ct)
    {
        // BackgroundServiceLog is only in the model when AddBackgroundService<T>() was called.
        // Deployments without the addon must not throw — skip silently.
        if (_context.Model.FindEntityType(typeof(BackgroundServiceLog)) == null)
        {
            return;
        }

        var globalRetentionCount = _configuration.BackgroundServiceLogRetentionCount;
        var globalRetentionAge = _configuration.BackgroundServiceLogRetentionAge;
        var now = _time.GetUtcNow().UtcDateTime;

        // Build per-service retention overrides keyed by Name. When a service supplies
        // an override, it takes precedence over the global WarpWorkerConfiguration value.
        var perServiceCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var perServiceAge = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);

        foreach (var service in _backgroundServices)
        {
            if (service.LogRetentionCountOverride.HasValue)
            {
                perServiceCount[service.Name] = service.LogRetentionCountOverride.Value;
            }

            if (service.LogRetentionAgeOverride.HasValue)
            {
                perServiceAge[service.Name] = service.LogRetentionAgeOverride.Value;
            }
        }

        // Find all (ServerId, ServiceName) pairs that have any rows. We filter by count
        // inside the loop using the resolved per-service retention value.
        var allInstances = await _context.Set<BackgroundServiceLog>()
            .GroupBy(l => new { l.ServerId, l.ServiceName })
            .Select(g => new { g.Key.ServerId, g.Key.ServiceName, Count = g.Count() })
            .ToListAsync(ct);

        foreach (var instance in allInstances)
        {
            var retentionCount = perServiceCount.TryGetValue(instance.ServiceName, out var overrideCount)
                ? overrideCount
                : globalRetentionCount;

            if (instance.Count <= retentionCount)
            {
                continue;
            }

            // Find the Id of the Nth-most-recent entry (1-based: retain top retentionCount rows).
            var cutoffId = await _context.Set<BackgroundServiceLog>()
                .Where(l => l.ServerId == instance.ServerId)
                .Where(l => l.ServiceName == instance.ServiceName)
                .OrderByDescending(l => l.Id)
                .Skip(retentionCount)
                .Select(l => l.Id)
                .FirstOrDefaultAsync(ct);

            if (cutoffId != 0)
            {
                await _context.Set<BackgroundServiceLog>()
                    .Where(l => l.ServerId == instance.ServerId)
                    .Where(l => l.ServiceName == instance.ServiceName)
                    .Where(l => l.Id <= cutoffId)
                    .ExecuteDeleteAsync(ct);
            }
        }

        // Age-based sweep — per-service age override applies when present; otherwise falls
        // back to the global retention age. Runs independently of the count cap.
        var serviceNamesWithAgeOverride = perServiceAge.Keys.ToList();

        if (serviceNamesWithAgeOverride.Count > 0)
        {
            // Delete rows for services without an age override using the global cutoff.
            var globalAgeCutoff = now.Subtract(globalRetentionAge);
            await _context.Set<BackgroundServiceLog>()
                .Where(l => !serviceNamesWithAgeOverride.Contains(l.ServiceName))
                .Where(l => l.Timestamp < globalAgeCutoff)
                .ExecuteDeleteAsync(ct);

            // For each service with an age override, apply its specific cutoff.
            foreach (var (serviceName, overrideAge) in perServiceAge)
            {
                var overrideAgeCutoff = now.Subtract(overrideAge);
                await _context.Set<BackgroundServiceLog>()
                    .Where(l => l.ServiceName == serviceName)
                    .Where(l => l.Timestamp < overrideAgeCutoff)
                    .ExecuteDeleteAsync(ct);
            }
        }
        else
        {
            // No age overrides — single sweep with the global cutoff.
            var ageCutoff = now.Subtract(globalRetentionAge);
            await _context.Set<BackgroundServiceLog>()
                .Where(l => l.Timestamp < ageCutoff)
                .ExecuteDeleteAsync(ct);
        }
    }
}
