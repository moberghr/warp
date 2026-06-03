using System.Globalization;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Models;

namespace Warp.Core.Services;

public interface IDashboardStatsService
{
    Task<DashboardStatistics> GetWarpStatus();

    WarpInfo GetWarpInfo();

    Task<List<StatsHistoryPoint>> GetStatsHistory(int hours = 24);

    Task<List<CounterModel>> GetCounters();

    Task<List<CounterHistoryPoint>> GetCountersHistory(int hours = 24);

    Task<List<ServerModel>> GetServers();

    Task<int> GetServerCount();

    Task<ServerModel?> GetServerById(Guid serverId);

    Task<PagedList<ServerLogModel>> GetServerLogs(Guid serverId, BaseListRequest request, string? taskName = null);

    Task<List<ServerTaskSummary>> GetServerTaskSummaries(Guid serverId);

    Task<PagedList<WorkerJobLogModel>> GetWorkerJobLogs(Guid workerId, BaseListRequest request);

    Task<WorkerDetailModel?> GetWorkerById(Guid workerId);
}

public class DashboardStatsService<TContext> : IDashboardStatsService
    where TContext : DbContext
{
    private readonly TContext _context;
    private readonly TimeProvider _timeProvider;
    private readonly WarpConfiguration _configuration;

    public DashboardStatsService(TContext context, TimeProvider timeProvider, IOptions<WarpConfiguration>? configuration = null)
    {
        _context = context;
        _timeProvider = timeProvider;
        _configuration = configuration?.Value ?? new WarpConfiguration();
    }

    public WarpInfo GetWarpInfo()
    {
        var version = typeof(WarpConfiguration).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(WarpConfiguration).Assembly.GetName().Version?.ToString();

        // Strip Git metadata appended by SourceLink (e.g. "1.2.3+abc123") so the
        // statusbar shows the package version, not the build hash.
        if (version is not null)
        {
            var plus = version.IndexOf('+', StringComparison.Ordinal);
            if (plus > 0)
            {
                version = version[..plus];
            }
        }

        var parts = ParseConnectionParts(_context.Database.GetConnectionString());
        var isPostgres = parts.ContainsKey("Host");

        return new WarpInfo
        {
            Version = version,
            Provider = isPostgres ? "PostgreSQL" : "SQL Server",
            Host = parts.GetValueOrDefault("Host") ?? parts.GetValueOrDefault("Server") ?? parts.GetValueOrDefault("Data Source"),
            Database = parts.GetValueOrDefault("Database") ?? parts.GetValueOrDefault("Initial Catalog"),
            Schema = _configuration.Schema,
        };
    }

    private static Dictionary<string, string> ParseConnectionParts(string? connectionString)
    {
        var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(connectionString))
        {
            return parts;
        }

        foreach (var raw in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = raw.Trim();
            var eq = trimmed.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            parts[trimmed[..eq].Trim()] = trimmed[(eq + 1)..].Trim();
        }

        return parts;
    }

    public async Task<DashboardStatistics> GetWarpStatus()
    {
        var total = await GetTotalJobsCount();
        var pending = await GetPendingJobsCount();
        var scheduled = await GetScheduledJobsCount();
        var created = await GetJobsCount(State.Enqueued);
        var completed = await GetJobsCount(State.Completed);
        var failed = await GetJobsCount(State.Failed);
        var processing = await GetProcessingJobsCount();

        var servers = await GetServerCount();
        var awaiting = await GetJobsCount(State.Awaiting);
        var deleted = await GetJobsCount(State.Deleted);
        var messages = await _context.Set<Job>()
            .Where(x => x.Kind == JobKind.Message)
            .Where(x => x.CurrentState == State.Enqueued
                || x.CurrentState == State.Awaiting
                || x.CurrentState == State.Processing
                || x.CurrentState == State.Scheduled)
            .CountAsync();
        var batches = await _context.Set<Job>()
            .Where(x => x.Kind == JobKind.Batch && x.CurrentState != State.Deleted)
            .CountAsync();

        // Per-state batch counts
        var batchStateCounts = await _context.Set<Job>()
            .Where(x => x.Kind == JobKind.Batch)
            .GroupBy(x => x.CurrentState)
            .Select(g => new { State = g.Key, Count = g.Count() })
            .ToListAsync();

        var batchesCompleted = batchStateCounts.Where(x => x.State == State.Completed).Sum(x => x.Count);
        var batchesFailed = batchStateCounts.Where(x => x.State == State.Failed).Sum(x => x.Count);
        var batchesDeleted = batchStateCounts.Where(x => x.State == State.Deleted).Sum(x => x.Count);

        var batchesProcessing = batchStateCounts.Where(x => x.State == State.Processing).Sum(x => x.Count);
        var batchesAwaiting = batchStateCounts.Where(x => x.State == State.Awaiting).Sum(x => x.Count);
        var batchesScheduled = batchStateCounts.Where(x => x.State == State.Scheduled).Sum(x => x.Count);
        var batchesEnqueued = batchStateCounts.Where(x => x.State == State.Enqueued).Sum(x => x.Count);

        // Per-state message counts
        var messageStateCounts = await _context.Set<Job>()
            .Where(x => x.Kind == JobKind.Message)
            .GroupBy(x => x.CurrentState)
            .Select(g => new { State = g.Key, Count = g.Count() })
            .ToListAsync();

        var messagesAwaiting = messageStateCounts.Where(x => x.State == State.Awaiting).Sum(x => x.Count);
        var messagesScheduled = messageStateCounts.Where(x => x.State == State.Scheduled).Sum(x => x.Count);
        var messagesEnqueued = messageStateCounts.Where(x => x.State == State.Enqueued).Sum(x => x.Count);
        var messagesProcessing = messageStateCounts.Where(x => x.State == State.Processing).Sum(x => x.Count);
        var messagesCompleted = messageStateCounts.Where(x => x.State == State.Completed).Sum(x => x.Count);
        var messagesFailed = messageStateCounts.Where(x => x.State == State.Failed).Sum(x => x.Count);
        var messagesDeleted = messageStateCounts.Where(x => x.State == State.Deleted).Sum(x => x.Count);

        var totalSucceeded = await GetCombinedStatValue("stats:succeeded");
        var totalFailed = await GetCombinedStatValue("stats:failed");
        var totalDeleted = await GetCombinedStatValue("stats:deleted");
        var model = new DashboardStatistics
        {
            Total = total,
            Pending = pending,
            Scheduled = scheduled,
            Created = created,
            Completed = completed,
            Failed = failed,
            Processing = processing,
            Servers = servers,
            Awaiting = awaiting,
            Deleted = deleted,
            Messages = messages,
            Batches = batches,
            BatchesProcessing = batchesProcessing,
            BatchesCompleted = batchesCompleted,
            BatchesFailed = batchesFailed,
            BatchesAwaiting = batchesAwaiting,
            BatchesDeleted = batchesDeleted,
            BatchesScheduled = batchesScheduled,
            BatchesEnqueued = batchesEnqueued,
            MessagesAwaiting = messagesAwaiting,
            MessagesScheduled = messagesScheduled,
            MessagesEnqueued = messagesEnqueued,
            MessagesProcessing = messagesProcessing,
            MessagesCompleted = messagesCompleted,
            MessagesFailed = messagesFailed,
            MessagesDeleted = messagesDeleted,
            TotalSucceeded = totalSucceeded,
            TotalFailed = totalFailed,
            TotalDeleted = totalDeleted,
            TotalCreated = 0,
            DatabaseConnection = GetSafeDatabaseConnection(),
        };

        return model;
    }

    public async Task<int> GetServerCount()
    {
        return await _context.Set<Server>().CountAsync();
    }

    public async Task<List<ServerModel>> GetServers()
    {
        var servers = await _context.Set<Server>().OrderBy(s => s.StartedTime).ToListAsync();

        var workers = await _context.Set<Worker>().Include(w => w.WorkerGroup).OrderBy(w => w.WorkerGroupId).ThenBy(w => w.Id).ToListAsync();

        var processingJobs = await _context.Set<Job>()
            .Where(x => x.CurrentState == State.Processing)
            .Where(x => x.CurrentWorkerId != null)
            .Select(x => new { x.CurrentWorkerId, x.Id, x.Type })
            .ToListAsync();

        var jobByWorker = processingJobs
            .GroupBy(j => j.CurrentWorkerId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var workersByServer = workers
            .GroupBy(w => w.ServerId)
            .ToDictionary(g => g.Key, g => g.ToList());

        return servers.ConvertAll(s => new ServerModel
        {
            Id = s.Id,
            ServerName = s.ServerName,
            StartedTime = s.StartedTime,
            LastHeartbeatTime = s.LastHeartbeatTime,
            ServiceCount = s.ServiceCount,
            CpuUsagePercent = s.CpuUsagePercent,
            MemoryWorkingSetBytes = s.MemoryWorkingSetBytes,
            PausedAt = s.PausedAt,
            Workers = workersByServer.GetValueOrDefault(s.Id, [])
                .ConvertAll(w =>
                {
                    jobByWorker.TryGetValue(w.Id, out var activeJob);
                    return new WorkerModel
                    {
                        WorkerId = w.Id,
                        StartedTime = w.StartedTime,
                        LastHeartbeatTime = w.LastHeartbeatTime,
                        CurrentJobId = activeJob?.Id,
                        CurrentJobType = activeJob?.Type,
                        Queues = w.WorkerGroup?.Queues,
                        PollingIntervalMs = w.WorkerGroup?.PollingIntervalMs,
                        WorkerGroupId = w.WorkerGroupId,
                        WorkerGroupPausedAt = w.WorkerGroup?.PausedAt,
                    };
                }),
        });
    }

    public async Task<ServerModel?> GetServerById(Guid serverId)
    {
        var server = await _context.Set<Server>()
            .Where(s => s.Id == serverId)
            .FirstOrDefaultAsync();

        if (server == null)
        {
            return null;
        }

        var workers = await _context.Set<Worker>()
            .Include(w => w.WorkerGroup)
            .Where(w => w.ServerId == serverId)
            .OrderBy(w => w.WorkerGroupId).ThenBy(w => w.Id)
            .ToListAsync();

        var processingJobs = await _context.Set<Job>()
            .Where(x => x.CurrentState == State.Processing && x.CurrentWorkerId != null)
            .Select(x => new { x.CurrentWorkerId, x.Id, x.Type })
            .ToListAsync();

        var jobByWorker = processingJobs
            .GroupBy(j => j.CurrentWorkerId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        return new ServerModel
        {
            Id = server.Id,
            ServerName = server.ServerName,
            StartedTime = server.StartedTime,
            LastHeartbeatTime = server.LastHeartbeatTime,
            ServiceCount = server.ServiceCount,
            CpuUsagePercent = server.CpuUsagePercent,
            MemoryWorkingSetBytes = server.MemoryWorkingSetBytes,
            PausedAt = server.PausedAt,
            Workers = workers.ConvertAll(w =>
            {
                jobByWorker.TryGetValue(w.Id, out var activeJob);
                return new WorkerModel
                {
                    WorkerId = w.Id,
                    StartedTime = w.StartedTime,
                    LastHeartbeatTime = w.LastHeartbeatTime,
                    CurrentJobId = activeJob?.Id,
                    CurrentJobType = activeJob?.Type,
                    Queues = w.WorkerGroup?.Queues,
                    PollingIntervalMs = w.WorkerGroup?.PollingIntervalMs,
                    WorkerGroupId = w.WorkerGroupId,
                    WorkerGroupPausedAt = w.WorkerGroup?.PausedAt,
                };
            }),
        };
    }

    public async Task<List<ServerTaskSummary>> GetServerTaskSummaries(Guid serverId)
    {
        return await _context.Set<ServerTask>()
            .Where(x => x.ServerId == serverId)
            .Select(x => new ServerTaskSummary
            {
                TaskName = x.TaskName,
                IntervalSeconds = x.IntervalSeconds,
                LastStatus = x.LastStatus,
                LastMessage = x.LastMessage,
                LastRun = x.LastRun,
                LastDurationMs = x.LastDurationMs,
            })
            .ToListAsync();
    }

    public async Task<PagedList<ServerLogModel>> GetServerLogs(Guid serverId, BaseListRequest request, string? taskName = null)
    {
        var query = _context.Set<ServerLog>()
            .Where(x => x.ServerId == serverId);

        if (taskName != null)
        {
            // Find the ServerTask ID for this task name, then filter logs
            var taskId = await _context.Set<ServerTask>()
                .Where(x => x.ServerId == serverId && x.TaskName == taskName)
                .Select(x => (int?)x.Id)
                .FirstOrDefaultAsync();

            if (taskId == null)
            {
                return new PagedList<ServerLogModel>(0, [], 0);
            }

            query = query.Where(x => x.ServerTaskId == taskId);
        }

        return await query
            .OrderByDescending(x => x.Timestamp)
            .Select(x => new ServerLogModel
            {
                Id = x.Id,
                TaskName = x.ServerTask != null ? x.ServerTask.TaskName : "Server",
                Status = x.Status,
                Message = x.Message,
                Timestamp = x.Timestamp,
                DurationMs = x.DurationMs,
            })
            .ToPagedListAsync(request);
    }

    public async Task<List<StatsHistoryPoint>> GetStatsHistory(int hours = 24)
    {
        var since = _timeProvider.GetUtcNow().UtcDateTime.AddHours(-hours);

        var aggregated = await _context.Set<Statistic>()
            .Where(x => x.Key.StartsWith("stats:succeeded:") || x.Key.StartsWith("stats:failed:"))
            .Select(x => new { x.Key, x.Value })
            .ToListAsync();

        var pending = await _context.Set<Counter>()
            .Where(x => x.Key.StartsWith("stats:succeeded:") || x.Key.StartsWith("stats:failed:"))
            .GroupBy(x => x.Key)
            .Select(g => new { Key = g.Key, Value = (long)g.Sum(c => c.Value) })
            .ToListAsync();

        // Merge both into a single list
        var hourlyStats = aggregated.Concat(pending)
            .GroupBy(x => x.Key)
            .Select(g => new { Key = g.Key, Value = g.Sum(x => x.Value) })
            .ToList();

        // Parse keys like "stats:succeeded:2026-03-28-14" into date + metric
        var points = new Dictionary<string, StatsHistoryPoint>(StringComparer.Ordinal);

        foreach (var stat in hourlyStats)
        {
            var parts = stat.Key.Split(':');
            if (parts.Length != 3)
            {
                continue;
            }

            var metric = parts[1]; // "succeeded" or "failed"
            var hourStr = parts[2]; // "2026-03-28-14"

            if (!DateTime.TryParseExact(
                hourStr,
                "yyyy-MM-dd-HH",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var hour))
            {
                continue;
            }

            if (hour < since)
            {
                continue;
            }

            if (!points.TryGetValue(hourStr, out var point))
            {
                point = new StatsHistoryPoint { Hour = hour };
                points[hourStr] = point;
            }

            if (string.Equals(metric, "succeeded", StringComparison.Ordinal))
            {
                point.Succeeded = stat.Value;
            }
            else if (string.Equals(metric, "failed", StringComparison.Ordinal))
            {
                point.Failed = stat.Value;
            }
        }

        return [.. points.Values.OrderBy(p => p.Hour)];
    }

    private async Task<long> GetCombinedStatValue(string key)
    {
        var aggregated = await _context.Set<Statistic>()
            .Where(x => x.Key == key)
            .Select(x => x.Value)
            .FirstOrDefaultAsync();

        var pending = await _context.Set<Counter>()
            .Where(x => x.Key == key)
            .SumAsync(x => x.Value);

        return aggregated + pending;
    }

    public async Task<List<CounterModel>> GetCounters()
    {
        // Merge aggregated Statistic rows with pending Counter rows so the page reflects the
        // same view as the dashboard's metric cards (which use GetCombinedStatValue per key).
        var aggregated = await _context.Set<Statistic>()
            .Select(x => new { x.Key, x.Value })
            .ToListAsync();

        var pending = await _context.Set<Counter>()
            .GroupBy(x => x.Key)
            .Select(g => new { Key = g.Key, Value = (long)g.Sum(c => c.Value) })
            .ToListAsync();

        var merged = aggregated.Concat(pending)
            .Where(x => !IsHourlyKey(x.Key))
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .Select(g => new CounterModel { Key = g.Key, Value = g.Sum(x => x.Value) })
            .OrderBy(x => x.Key, StringComparer.Ordinal);

        return [.. merged];
    }

    /// <summary>
    /// Hourly bucket keys (e.g. <c>stats:succeeded:2026-05-07-10</c>) are internal accounting for
    /// the historical chart, not user-facing counters. The Counters page table only shows
    /// rolled-up keys; the Counters chart consumes the hourly rows via <see cref="GetCountersHistory"/>.
    /// </summary>
    private static bool IsHourlyKey(string key) => TryParseHourlyKey(key, out _, out _);

    private static bool TryParseHourlyKey(string key, out string baseKey, out DateTime hour)
    {
        baseKey = string.Empty;
        hour = default;

        var lastColon = key.LastIndexOf(':');
        if (lastColon < 0)
        {
            return false;
        }

        if (!DateTime.TryParseExact(
            key.AsSpan(lastColon + 1),
            "yyyy-MM-dd-HH",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out hour))
        {
            return false;
        }

        baseKey = key[..lastColon];

        return true;
    }

    public async Task<List<CounterHistoryPoint>> GetCountersHistory(int hours = 24)
    {
        // All hourly buckets within the window, merged from Statistic + pending Counter rows so
        // a freshly-written counter row shows up immediately even if the aggregator hasn't run.
        var since = _timeProvider.GetUtcNow().UtcDateTime.AddHours(-hours);

        var aggregated = await _context.Set<Statistic>()
            .Where(x => EF.Functions.Like(x.Key, "%:%"))
            .Select(x => new { x.Key, x.Value })
            .ToListAsync();

        var pending = await _context.Set<Counter>()
            .Where(x => EF.Functions.Like(x.Key, "%:%"))
            .GroupBy(x => x.Key)
            .Select(g => new { Key = g.Key, Value = (long)g.Sum(c => c.Value) })
            .ToListAsync();

        var merged = aggregated.Concat(pending)
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .Select(g => new { Key = g.Key, Value = g.Sum(x => x.Value) });

        var points = new List<CounterHistoryPoint>();
        foreach (var row in merged)
        {
            if (!TryParseHourlyKey(row.Key, out var baseKey, out var hour) || hour < since)
            {
                continue;
            }

            points.Add(new CounterHistoryPoint
            {
                Hour = hour,
                Key = baseKey,
                Value = row.Value,
            });
        }

        return [.. points.OrderBy(p => p.Hour).ThenBy(p => p.Key, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Base query that returns only actual jobs (excludes messages and batches).
    /// </summary>
    private IQueryable<Job> Jobs()
    {
        return _context.Set<Job>().Where(j => j.Kind == JobKind.Job);
    }

    private async Task<int> GetTotalJobsCount()
    {
        return await Jobs().CountAsync();
    }

    private async Task<int> GetPendingJobsCount()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        return await Jobs()
            .Where(x => x.ScheduleTime < now)
            .CountAsync();
    }

    private async Task<int> GetScheduledJobsCount()
    {
        return await Jobs()
            .Where(x => x.CurrentState == State.Scheduled)
            .CountAsync();
    }

    private async Task<int> GetJobsCount(State state)
    {
        var query = Jobs()
            .Where(x => x.CurrentState == state);

        if (state == State.Enqueued)
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            query = query.Where(x => x.ScheduleTime <= now);
        }

        return await query.CountAsync();
    }

    private string? GetSafeDatabaseConnection()
    {
        var connectionString = _context.Database.GetConnectionString();
        if (string.IsNullOrEmpty(connectionString))
        {
            return null;
        }

        // A connection string can legally contain the same key twice — ADO.NET's
        // SqlConnectionStringBuilder resolves this by taking the LAST value. Tests
        // that scope per-server connection pools by appending `Application Name=...`
        // to an already-configured base string produce this shape, and a naive
        // ToDictionary throws on the duplicate. Fold via last-wins.
        var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = raw.Trim();
            var eq = trimmed.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            parts[trimmed[..eq].Trim()] = trimmed[(eq + 1)..].Trim();
        }

        var isPostgres = parts.ContainsKey("Host");
        var provider = isPostgres ? "PostgreSQL Server" : "SQL Server";
        var host = parts.GetValueOrDefault("Host") ?? parts.GetValueOrDefault("Server") ?? parts.GetValueOrDefault("Data Source") ?? "unknown";
        var db = parts.GetValueOrDefault("Database") ?? parts.GetValueOrDefault("Initial Catalog") ?? string.Empty;

        return $"{provider}: Host: {host}, DB: {db}";
    }

    public async Task<WorkerDetailModel?> GetWorkerById(Guid workerId)
    {
        var worker = await _context.Set<Worker>()
            .Include(w => w.WorkerGroup)
            .Where(w => w.Id == workerId)
            .FirstOrDefaultAsync();

        if (worker == null)
        {
            return null;
        }

        var server = await _context.Set<Server>()
            .Where(s => s.Id == worker.ServerId)
            .FirstOrDefaultAsync();

        var activeJob = await _context.Set<Job>()
            .Where(j => j.CurrentWorkerId == workerId)
            .Select(j => new { j.Id, j.Type })
            .FirstOrDefaultAsync();

        return new WorkerDetailModel
        {
            WorkerId = worker.Id,
            StartedTime = worker.StartedTime,
            LastHeartbeatTime = worker.LastHeartbeatTime,
            CurrentJobId = activeJob?.Id,
            CurrentJobType = activeJob?.Type,
            Queues = worker.WorkerGroup?.Queues,
            PollingIntervalMs = worker.WorkerGroup?.PollingIntervalMs,
            ServerPausedAt = server?.PausedAt,
            WorkerGroupId = worker.WorkerGroupId,
            WorkerGroupPausedAt = worker.WorkerGroup?.PausedAt,
            ServerId = worker.ServerId,
            ServerName = server?.ServerName ?? "Unknown",
        };
    }

    public async Task<PagedList<WorkerJobLogModel>> GetWorkerJobLogs(Guid workerId, BaseListRequest request)
    {
        return await _context.Set<JobLog>()
            .Where(x => x.WorkerId == workerId)
            .OrderByDescending(x => x.Timestamp)
            .Select(x => new WorkerJobLogModel
            {
                Id = x.Id,
                JobId = x.JobId,
                JobType = _context.Set<Job>()
                    .Where(j => j.Id == x.JobId)
                    .Select(j => j.Type)
                    .FirstOrDefault(),
                EventType = x.EventType,
                Timestamp = x.Timestamp,
                Level = x.Level,
                Message = x.Message,
                Exception = x.Exception,
                DurationMs = x.DurationMs,
            })
            .ToPagedListAsync(request);
    }

    private async Task<int> GetProcessingJobsCount()
    {
        return await Jobs()
            .Where(x => x.CurrentState == State.Processing)
            .CountAsync();
    }
}
