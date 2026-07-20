using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.BackgroundServices;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;

namespace Warp.Worker.Services;

/// <summary>
/// Deletes expired jobs + logs, trims old hourly stats, deletes server logs past their
/// per-task retention, and caps RecurringJobLog history. Also handles count-based
/// cleanup when <see cref="WarpServerConfiguration.MaxExpirableJobCount"/> is set.
/// </summary>
public sealed class ExpirationCleanup<TContext> : IServerTask
    where TContext : DbContext
{
    private readonly DbContext _context;
    private readonly TimeProvider _time;
    private readonly WarpServerConfiguration _configuration;
    private readonly IEnumerable<WarpBackgroundService> _backgroundServices;

    public ExpirationCleanup(
        IWarpServerContext serverContext,
        TimeProvider time,
        IOptions<WarpServerConfiguration> configuration,
        IEnumerable<WarpBackgroundService>? backgroundServices = null)
    {
        _context = serverContext.Context;
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
        await CleanupOrphanedBackgroundServiceDefinitionsAsync(ct);
        await CleanupExpiredAdapterCallLogsAsync(ct);
        await CleanupOrphanedAdapterDefinitionsAsync(ct);
        await CleanupExpiredWebhookDeliveriesAsync(ct);

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
        var globalRetentionCount = _configuration.BackgroundServiceLogRetentionCount;
        var globalRetentionAge = _configuration.BackgroundServiceLogRetentionAge;
        var now = _time.GetUtcNow().UtcDateTime;

        // Build per-service retention overrides keyed by Name. When a service supplies
        // an override, it takes precedence over the global WarpServerConfiguration value.
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

    // Removes BackgroundServiceDefinition rows whose service is no longer registered on any
    // server. Orphan signal: no live BackgroundServiceInstance row references the name AND
    // LastSeenAt is older than the deploy-race grace window. The Instance check is the primary
    // live-presence indicator (Heartbeat task refreshes Instance.LastHeartbeatAt every ~3s,
    // ServerCleanup removes them on server departure); the LastSeenAt threshold protects
    // against deleting and immediately recreating a Definition during a rolling deploy gap
    // (losing FirstSeenAt history but no data). Order relative to CleanupBackgroundServiceLogsAsync
    // doesn't matter: BackgroundServiceLog has a FK cascade from Instance (§8.18), not from
    // Definition, so log rows tied to a deleted Definition were already removed when their
    // Instance disappeared.
    // Deletes AdapterCallLog rows past their stamped ExpireAt. Call logs are diagnostics, not an
    // audit trail (§8.2 stance) — the flusher stamps ExpireAt from the per-adapter / global retention
    // and this sweep drops anything expired. This is the highest-volume adapter table (a row per
    // outbound call under RecordCalls = All), so the delete runs in ExpirationBatchSize id batches.
    // Honest scope of the batching: the whole task tick shares one xact-lock transaction, so row locks
    // accumulate across batches until commit — what batching buys is per-STATEMENT bounds, below SQL
    // Server's ~5k lock-escalation threshold so the table never escalates to a table lock against the
    // flusher's live INSERTs, not earlier lock release. Loops to exhaustion up to MaxSweepBatchesPerTick
    // with any remainder draining next tick.
    //
    // The cap is a backstop: with a sane retention, fresh rows are stamped a future ExpireAt and never
    // requalify against the snapshotted now, so the loop naturally terminates. A pathological zero or
    // negative retention would let the flusher's live INSERTs requalify mid-loop — the cap converts that
    // into bounded work per tick instead of an endless chase.
    private const int MaxSweepBatchesPerTick = 100;

    internal async Task<int> CleanupExpiredAdapterCallLogsAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var batchSize = _configuration.ExpirationBatchSize;
        var batches = 0;
        var total = 0;

        while (!ct.IsCancellationRequested && batches < MaxSweepBatchesPerTick)
        {
            batches++;
            var ids = await _context.Set<AdapterCallLog>()
                .Where(x => x.ExpireAt != null)
                .Where(x => x.ExpireAt < now)
                .Select(x => x.Id)
                .Take(batchSize)
                .ToListAsync(ct);

            if (ids.Count == 0)
            {
                break;
            }

            total += await _context.Set<AdapterCallLog>()
                .Where(x => ids.Contains(x.Id))
                .ExecuteDeleteAsync(ct);

            if (ids.Count < batchSize)
            {
                break;
            }
        }

        return total;
    }

    // Deletes AdapterDefinition rows whose LastSeenAt is older than the orphan grace. Adapters run in
    // non-server processes, so there is no live-instance signal (unlike background services) — staleness
    // alone drives removal. The flusher lazily refreshes LastSeenAt while an adapter is in use, so a
    // still-active adapter stays well within the grace window. One row per adapter NAME (not per call),
    // so the single statement is bounded by registration cardinality — no id batching needed.
    internal async Task CleanupOrphanedAdapterDefinitionsAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var threshold = now.Subtract(_configuration.AdapterDefinitionOrphanGrace);

        await _context.Set<AdapterDefinition>()
            .Where(x => x.LastSeenAt < threshold)
            .ExecuteDeleteAsync(ct);
    }

    // Deletes settled WebhookDelivery rows past their stamped ExpireAt. Delivery rows are operational
    // history, not an audit trail (§8.2 stance) — the dispatcher stamps ExpireAt from WebhookDeliveryRetention
    // and this sweep drops anything expired. Pending deliveries are excluded: an in-flight delivery whose
    // ExpireAt elapses mid-schedule (a long backoff can outrun retention) must never vanish underneath its
    // own scheduled executor job — only Delivered/Exhausted rows are eligible. The WebhookDelivery table is
    // always in the schema (§2.11), like AdapterCallLog, so no model guard is needed. Attempt rows
    // (AdapterCallLog) expire independently on their own retention. A volume table (a row per send), so
    // the delete runs in ExpirationBatchSize id batches like the call-log sweep — same honest scope:
    // per-statement bounds (no lock escalation), not earlier lock release, capped by MaxSweepBatchesPerTick.
    internal async Task<int> CleanupExpiredWebhookDeliveriesAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var batchSize = _configuration.ExpirationBatchSize;
        var batches = 0;
        var total = 0;

        while (!ct.IsCancellationRequested && batches < MaxSweepBatchesPerTick)
        {
            batches++;
            var ids = await _context.Set<WebhookDelivery>()
                .Where(x => x.Status != WebhookDeliveryStatus.Pending)
                .Where(x => x.ExpireAt != null)
                .Where(x => x.ExpireAt < now)
                .Select(x => x.Id)
                .Take(batchSize)
                .ToListAsync(ct);

            if (ids.Count == 0)
            {
                break;
            }

            total += await _context.Set<WebhookDelivery>()
                .Where(x => ids.Contains(x.Id))
                .ExecuteDeleteAsync(ct);

            if (ids.Count < batchSize)
            {
                break;
            }
        }

        return total;
    }

    internal async Task CleanupOrphanedBackgroundServiceDefinitionsAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var threshold = now.Subtract(_configuration.BackgroundServiceDefinitionOrphanGrace);

        // Single statement so the active-Instance check and the row delete share one DB
        // snapshot — eliminates any window between SELECT and DELETE where a concurrent
        // RegisterAsync could insert an Instance row we'd miss. §5.2 forbids _context.Set<>()
        // subqueries inside .Select() projections; .Where() with .Any() is permitted. EF Core
        // 10.0.5 emits a NOT EXISTS subquery on both providers — covered by the
        // OrphanDefinitionCleanupTests_PostgreSql / _SqlServer integration tests.
        await _context.Set<BackgroundServiceDefinition>()
            .Where(x => x.LastSeenAt < threshold)
            .Where(x => !_context.Set<BackgroundServiceInstance>()
                .Any(y => y.ServiceName == x.Name))
            .ExecuteDeleteAsync(ct);
    }
}
