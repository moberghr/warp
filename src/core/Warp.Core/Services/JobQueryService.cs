using Microsoft.EntityFrameworkCore;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Metrics;
using Warp.Core.Models;
using static Warp.Core.Metrics.WarpMetricCatalog;

namespace Warp.Core.Services;

public interface IJobQueryService
{
    Task<PagedList<JobModel>> GetJobsList(BaseListRequest request, State state, string? application = null);

    Task<PagedList<JobModel>> GetScheduledJobs(BaseListRequest request);

    Task<PagedList<JobModel>> GetJobStatesInProcess(BaseListRequest request);

    Task<PagedList<JobModel>> GetAwaitingJobs(BaseListRequest request);

    Task<PagedList<JobModel>> GetSiblingJobs(Guid jobId, BaseListRequest request);

    Task<PagedList<JobModel>> GetChildJobs(Guid jobId, BaseListRequest request);

    Task<PagedList<JobModel>> GetTraceJobs(Guid jobId, BaseListRequest request);

    Task<List<TraceJobModel>> GetTraceTree(Guid traceId);

    Task<UnifiedJobDetailModel?> GetJobDetailById(Guid id);

    Task<int> CountProcessingJobs();

    Task<List<TypeCountModel>> GetFailedJobTypeCounts();

    Task<PagedList<JobModel>> GetFailedJobsByType(BaseListRequest request, string type);

    Task<PagedList<JobModel>> GetJobsByType(BaseListRequest request, string type, State? state, string? application = null);

    Task<JobExecutionMetricsModel> GetJobExecutionMetrics(string? application = null);

    Task<QueueMetricsModel> GetQueueMetrics(string? application = null);
}

public class JobQueryService<TContext> : IJobQueryService
    where TContext : DbContext
{
    private readonly TContext _context;
    private readonly TimeProvider _timeProvider;
    private readonly IMetricSource _metrics;

    public JobQueryService(TContext context, TimeProvider timeProvider, IMetricSource metrics)
    {
        _context = context;
        _timeProvider = timeProvider;
        _metrics = metrics;
    }

    public async Task<PagedList<JobModel>> GetJobsList(BaseListRequest request, State state, string? application = null)
    {
        return await GetJobsByState(state, application)
            .ToPagedListAsync(request);
    }

    public async Task<PagedList<JobModel>> GetScheduledJobs(BaseListRequest request)
    {
        var jobs = await GetScheduledJobsQuery()
            .ToPagedListAsync(request);

        return jobs;
    }

    public async Task<int> CountProcessingJobs()
    {
        return await Jobs()
            .Where(x => x.CurrentState == State.Processing)
            .CountAsync();
    }

    public async Task<PagedList<JobModel>> GetJobStatesInProcess(BaseListRequest request)
    {
        var processing = Jobs().Where(x => x.CurrentState == State.Processing);

        var jobs = await OrderByCreateTimeDescending(processing)
            .Select(x => new JobModel
            {
                Id = x.Id,
                CurrentState = x.CurrentState,
                CancellationMode = x.CancellationMode,
                HandlerType = x.HandlerType,
                CreateTime = x.CreateTime,
                Message = x.Message,
                ScheduleTime = x.ScheduleTime,
                Type = x.Type,
            })
            .ToPagedListAsync(request);
        return jobs;
    }

    public async Task<PagedList<JobModel>> GetAwaitingJobs(BaseListRequest request)
    {
        return await GetJobsByState(State.Awaiting, application: null).ToPagedListAsync(request);
    }

    public async Task<PagedList<JobModel>> GetSiblingJobs(Guid jobId, BaseListRequest request)
    {
        var parentJobId = await _context.Set<Job>()
            .Where(x => x.Id == jobId)
            .Select(x => x.ParentJobId)
            .FirstOrDefaultAsync();

        if (parentJobId == null)
        {
            return new PagedList<JobModel>(0, [], 0);
        }

        return await _context.Set<Job>()
            .Where(x => x.ParentJobId == parentJobId && x.Kind == JobKind.Job && x.Id != jobId)
            .OrderByDescending(x => x.CreateTime)
            .Select(x => new JobModel
            {
                Id = x.Id,
                Type = x.Type,
                Message = x.Message,
                CreateTime = x.CreateTime,
                ScheduleTime = x.ScheduleTime,
                CurrentState = x.CurrentState,
                CancellationMode = x.CancellationMode,
                HandlerType = x.HandlerType,
            })
            .ToPagedListAsync(request);
    }

    public async Task<PagedList<JobModel>> GetChildJobs(Guid jobId, BaseListRequest request)
    {
        return await _context.Set<Job>()
            .Where(x => x.ParentJobId == jobId)
            .OrderByDescending(x => x.CreateTime)
            .Select(x => new JobModel
            {
                Id = x.Id,
                Type = x.Type,
                Message = x.Message,
                CreateTime = x.CreateTime,
                ScheduleTime = x.ScheduleTime,
                CurrentState = x.CurrentState,
                CancellationMode = x.CancellationMode,
                HandlerType = x.HandlerType,
            })
            .ToPagedListAsync(request);
    }

    public async Task<PagedList<JobModel>> GetTraceJobs(Guid jobId, BaseListRequest request)
    {
        var traceId = await _context.Set<Job>()
            .Where(x => x.Id == jobId)
            .Select(x => x.TraceId)
            .FirstOrDefaultAsync();

        if (traceId == null)
        {
            return new PagedList<JobModel>(0, [], 0);
        }

        return await _context.Set<Job>()
            .Where(x => x.TraceId == traceId && x.Id != jobId)
            .OrderBy(x => x.CreateTime)
            .Select(x => new JobModel
            {
                Id = x.Id,
                Type = x.Type,
                Message = x.Message,
                CreateTime = x.CreateTime,
                ScheduleTime = x.ScheduleTime,
                CurrentState = x.CurrentState,
                CancellationMode = x.CancellationMode,
                HandlerType = x.HandlerType,
            })
            .ToPagedListAsync(request);
    }

    public async Task<List<TraceJobModel>> GetTraceTree(Guid traceId)
    {
        return await _context.Set<Job>()
            .AsNoTracking()
            .Where(x => x.TraceId == traceId)
            .OrderBy(x => x.CreateTime)
            .Select(x => new TraceJobModel
            {
                Id = x.Id,
                Kind = x.Kind,
                Type = x.Type,
                HandlerType = x.HandlerType,
                CurrentState = x.CurrentState,
                ParentJobId = x.ParentJobId,
                SpawnedByJobId = x.SpawnedByJobId,
                CreateTime = x.CreateTime,
            })
            .ToListAsync();
    }

    public async Task<UnifiedJobDetailModel?> GetJobDetailById(Guid id)
    {
        var job = await _context.Set<Job>()
            .Where(x => x.Id == id)
            .Select(x => new UnifiedJobDetailModel
            {
                Id = x.Id,
                Kind = x.Kind,
                Type = x.Type,
                CurrentState = x.CurrentState,
                CreateTime = x.CreateTime,
                CancellationMode = x.CancellationMode,
                Message = x.Message,
                HandlerType = x.HandlerType,
                ScheduleTime = x.ScheduleTime == DateTime.MinValue ? null : x.ScheduleTime,
                ContinuationOptions = x.ContinuationOptions,
                Queue = x.Queue,
                TraceId = x.TraceId,
                MetadataJson = x.Metadata,
            })
            .FirstOrDefaultAsync();

        if (job == null)
        {
            return null;
        }

        // Logs
        job.Logs = await _context.Set<JobLog>()
            .Where(x => x.JobId == id)
            .OrderBy(x => x.Timestamp)
            .Select(x => new JobLogModel
            {
                Id = x.Id,
                EventType = x.EventType,
                Timestamp = x.Timestamp,
                Level = x.Level,
                Message = x.Message,
                Exception = x.Exception,
                DurationMs = x.DurationMs,
                WorkerId = x.WorkerId,
                Name = x.Name,
                Value = x.Value,
            })
            .ToListAsync();

        // Origin: the inbound HTTP request that started this trace (reverse of the endpoint's
        // request→jobs drill-down — jobs enqueued during a request share its trace id). Only when
        // tracing was active (TraceId set); the earliest request row on the trace is the originator.
        if (job.TraceId is { } traceId)
        {
            var origin = await _context.Set<EndpointCallLog>()
                .AsNoTracking()
                .Where(x => x.TraceId == traceId)
                .OrderBy(x => x.Timestamp)
                .Select(x =>
                    new
                    {
                        x.Id,
                        x.Method,
                        x.RouteTemplate,
                        x.User,
                    })
                .FirstOrDefaultAsync();

            if (origin != null)
            {
                job.Origin = new JobOriginModel
                {
                    Method = origin.Method,
                    RouteTemplate = origin.RouteTemplate,
                    User = origin.User,
                    CallId = origin.Id,
                    EndpointId = UrlSafeId.Encode($"{origin.Method} {origin.RouteTemplate}"),
                };
            }
        }

        // Parent job details
        var parentJobId = await _context.Set<Job>()
            .Where(x => x.Id == id)
            .Select(x => x.ParentJobId)
            .FirstOrDefaultAsync();

        if (parentJobId != null)
        {
            job.ParentJob = await _context.Set<Job>()
                .Where(x => x.Id == parentJobId)
                .Select(x => new ContinuationInfo
                {
                    Id = x.Id,
                    Kind = x.Kind,
                    CurrentState = x.CurrentState,
                    Type = x.Type,
                    HandlerType = x.HandlerType,
                })
                .FirstOrDefaultAsync();
        }

        // Spawned-by job details
        var spawnedByJobId = await _context.Set<Job>()
            .Where(x => x.Id == id)
            .Select(x => x.SpawnedByJobId)
            .FirstOrDefaultAsync();

        if (spawnedByJobId != null)
        {
            job.SpawnedByJob = await _context.Set<Job>()
                .Where(x => x.Id == spawnedByJobId)
                .Select(x => new ContinuationInfo
                {
                    Id = x.Id,
                    Kind = x.Kind,
                    CurrentState = x.CurrentState,
                    Type = x.Type,
                    HandlerType = x.HandlerType,
                })
                .FirstOrDefaultAsync();
        }

        // Continuations (children linked via ParentJobId)
        // For batches/messages, exclude their own Job children (shown in FilteredJobsTable)
        var continuationsQuery = _context.Set<Job>()
            .Where(x => x.ParentJobId == id);

        if (job.Kind == JobKind.Batch || job.Kind == JobKind.Message)
        {
            continuationsQuery = continuationsQuery.Where(x => x.Kind != JobKind.Job);
        }

        job.Continuations = await continuationsQuery
            .OrderBy(x => x.CreateTime)
            .Select(x => new ContinuationInfo
            {
                Id = x.Id,
                Kind = x.Kind,
                CurrentState = x.CurrentState,
                Type = x.Type,
                HandlerType = x.HandlerType,
            })
            .ToListAsync();

        // Spawned jobs (created by this job's handler)
        job.SpawnedJobs = await _context.Set<Job>()
            .Where(x => x.SpawnedByJobId == id)
            .OrderBy(x => x.CreateTime)
            .Select(x => new ContinuationInfo
            {
                Id = x.Id,
                Kind = x.Kind,
                CurrentState = x.CurrentState,
                Type = x.Type,
                HandlerType = x.HandlerType,
            })
            .ToListAsync();

        // Batch: compute completed/failed from children
        if (job.Kind == JobKind.Batch)
        {
            var childCounts = await _context.Set<Job>()
                .Where(x => x.ParentJobId == id && x.Kind == JobKind.Job)
                .GroupBy(x => x.CurrentState)
                .Select(g => new { State = g.Key, Count = g.Count() })
                .ToListAsync();

            job.TotalJobs = childCounts.Sum(c => c.Count);
            job.CompletedJobs = childCounts.Where(c => c.State == State.Completed).Sum(c => c.Count);
            job.FailedJobs = childCounts.Where(c => c.State == State.Failed).Sum(c => c.Count);
        }

        return job;
    }

    public async Task<List<TypeCountModel>> GetFailedJobTypeCounts()
    {
        return await Jobs()
            .Where(x => x.CurrentState == State.Failed)
            .GroupBy(x => x.Type)
            .Select(g => new TypeCountModel { Type = g.Key!, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Type)
            .ToListAsync();
    }

    public async Task<PagedList<JobModel>> GetFailedJobsByType(BaseListRequest request, string type)
    {
        var failed = Jobs().Where(x => x.CurrentState == State.Failed && x.Type == type);

        return await OrderByFinishedTimeDescending(failed)
            .Select(x => new JobModel
            {
                Id = x.Id,
                Type = x.Type,
                Message = x.Message,
                CreateTime = x.CreateTime,
                ScheduleTime = x.ScheduleTime,
                CurrentState = x.CurrentState,
                CancellationMode = x.CancellationMode,
                HandlerType = x.HandlerType,
            })
            .ToPagedListAsync(request);
    }

    // #1: all jobs of a given type across states (newest-first by create time), with an optional state
    // filter. Backs the clickable job type in the dashboard. Ordered by create time (not finished time)
    // because the result mixes terminal and in-flight jobs.
    public async Task<PagedList<JobModel>> GetJobsByType(BaseListRequest request, string type, State? state, string? application = null)
    {
        var jobs = Jobs().Where(x => x.Type == type);

        if (state is { } s)
        {
            jobs = jobs.Where(x => x.CurrentState == s);
        }

        if (!string.IsNullOrEmpty(application))
        {
            jobs = jobs.Where(x => x.Application == application);
        }

        return await OrderByCreateTimeDescending(jobs)
            .Select(x =>
                new JobModel
                {
                    Id = x.Id,
                    Type = x.Type,
                    Message = x.Message,
                    CreateTime = x.CreateTime,
                    ScheduleTime = x.ScheduleTime,
                    CurrentState = x.CurrentState,
                    CancellationMode = x.CancellationMode,
                    HandlerType = x.HandlerType,
                })
            .ToPagedListAsync(request);
    }

    /// <summary>
    /// Base query that returns only actual jobs (excludes messages and batches).
    /// </summary>
    private IQueryable<Job> Jobs()
    {
        return _context.Set<Job>().Where(j => j.Kind == JobKind.Job);
    }

    // Orders jobs by latest terminal-event timestamp descending. Translates to
    // ORDER BY (SELECT MAX(timestamp) FROM job_log WHERE job_id = j.id AND
    // event_type IN (...)) DESC. Correlated subquery cost is bounded by the composite
    // (job_id, event_type, timestamp) index on job_log. Only meaningful for terminal-state
    // listings (see IsTerminalState) where a terminal log row is guaranteed to exist.
    private IOrderedQueryable<Job> OrderByFinishedTimeDescending(IQueryable<Job> jobs)
    {
        return jobs.OrderByDescending(x =>
            _context.Set<JobLog>()
                .Where(l => l.JobId == x.Id)
                .Where(l => TerminalEvents.EventTypes.Contains(l.EventType))
                .Max(l => (DateTime?)l.Timestamp) ?? x.CreateTime);
    }

    // Non-terminal-state pages (Enqueued/Processing/Scheduled/Awaiting) never have a
    // terminal log row, so OrderByFinishedTimeDescending would issue a correlated subquery
    // for every row only to fall through to CreateTime. Use a plain CreateTime sort here —
    // same result, no subquery cost.
    private static IOrderedQueryable<Job> OrderByCreateTimeDescending(IQueryable<Job> jobs)
        => jobs.OrderByDescending(x => x.CreateTime);

    // Paired with TerminalEventTypes — both lists must be updated together when a new
    // terminal State is introduced. The compiler-checked `nameof()` references above and
    // the enum constants here keep that pairing grep-able.
    private static bool IsTerminalState(State state)
        => state is State.Completed or State.Failed or State.Deleted;

    private IQueryable<JobModel> GetScheduledJobsQuery()
    {
        var jobs = Jobs().Where(x => x.CurrentState == State.Scheduled);

        return OrderByCreateTimeDescending(jobs)
            .Select(x =>
                new JobModel
                {
                    Id = x.Id,
                    CurrentState = x.CurrentState,
                    CancellationMode = x.CancellationMode,
                    HandlerType = x.HandlerType,
                    CreateTime = x.CreateTime,
                    Message = x.Message,
                    ScheduleTime = x.ScheduleTime,
                    Type = x.Type,
                });
    }

    // Per-job-TYPE + per-HANDLER execution metrics (§8.19 multi-app observability), read from the durable
    // Statistic aggregates (plus not-yet-collapsed Counter rows so a just-folded value is not missing) folded
    // from the jobstat: / jobstat-app: counter family (§ JobStatsKeys). Read-only §5.3 (AsNoTracking + Select).
    // Because the metrics live in Statistic — not Job — they remain readable AFTER the underlying Job rows are
    // cleaned up (the whole point). A null application reads the app-agnostic totals + latency histogram
    // (percentiles populated); a supplied application reads the disjoint per-app slice (count/duration/error
    // rate; percentiles 0 — the app family carries no histogram, to bound counter volume).
    public async Task<JobExecutionMetricsModel> GetJobExecutionMetrics(string? application = null)
    {
        var ct = CancellationToken.None;
        var scope = application is null ? null : new Dictionary<string, string> { [Tags.Application] = application };
        var exec = new MetricRef(Names.JobExecution, scope);
        var dur = new MetricRef(Names.JobExecutionDuration, scope);

        var byType = new Dictionary<string, ExecutionAccumulator>(StringComparer.Ordinal);
        var byHandler = new Dictionary<string, ExecutionAccumulator>(StringComparer.Ordinal);

        // Outcome counts + duration sum, per type and per handler dimension.
        await FoldExecutionAsync(byType, exec, dur, Tags.Type, ct);
        await FoldExecutionAsync(byHandler, exec, dur, Tags.Handler, ct);

        // Percentiles only for the app-agnostic read (the per-app slice carries no histogram, §8.19).
        var (typeP95, typeP99) = await PercentilesAsync(dur, Tags.Type, application is null, ct);
        var (handlerP95, handlerP99) = await PercentilesAsync(dur, Tags.Handler, application is null, ct);

        return new JobExecutionMetricsModel
        {
            ByType = Project(byType, typeP95, typeP99),
            ByHandler = Project(byHandler, handlerP95, handlerP99),
        };
    }

    // Folds the outcome counts (from the calls metric) and the duration sum (from the duration metric) of one
    // jobstat/qwait dimension into its per-identity accumulator map, via the seam.
    private async Task FoldExecutionAsync(Dictionary<string, ExecutionAccumulator> map, MetricRef counts, MetricRef duration, string idTag, CancellationToken ct)
    {
        foreach (var row in await _metrics.GetBreakdownAsync(counts, [idTag, Tags.Outcome], null, ct))
        {
            Accumulator(map, row.Tags[idTag]).Add(row.Tags[Tags.Outcome], row.Value);
        }

        foreach (var row in await _metrics.GetBreakdownAsync(duration, [idTag], null, ct))
        {
            Accumulator(map, row.Tags[idTag]).Add(JobStatsKeys.DurationToken, row.Value);
        }
    }

    // Per-identity p95 / p99 of a duration histogram metric (empty when the read carries no histogram).
    private async Task<(Dictionary<string, double> P95, Dictionary<string, double> P99)> PercentilesAsync(MetricRef duration, string idTag, bool hasHistogram, CancellationToken ct)
    {
        if (!hasHistogram)
        {
            return (new Dictionary<string, double>(StringComparer.Ordinal), new Dictionary<string, double>(StringComparer.Ordinal));
        }

        var p95 = (await _metrics.GetPercentileBreakdownAsync(duration, 95, [idTag], null, ct)).ToDictionary(r => r.Tags[idTag], r => r.Value, StringComparer.Ordinal);
        var p99 = (await _metrics.GetPercentileBreakdownAsync(duration, 99, [idTag], null, ct)).ToDictionary(r => r.Tags[idTag], r => r.Value, StringComparer.Ordinal);

        return (p95, p99);
    }

    private static ExecutionAccumulator Accumulator(Dictionary<string, ExecutionAccumulator> map, string id)
    {
        if (!map.TryGetValue(id, out var acc))
        {
            acc = new ExecutionAccumulator();
            map[id] = acc;
        }

        return acc;
    }

    // Per-queue queue-wait (avg + p95/p99 from the qwait: Counter→Statistic fold, reusing the same
    // ExecutionAccumulator/percentile walk as job-execution metrics) merged with the latest backlog gauge
    // (qbacklog: Statistic, upserted by BacklogSampler). Like job-execution metrics these survive Job-row
    // cleanup. A null application reads the app-agnostic families (percentiles populated); a supplied
    // application reads the disjoint per-app slice (no histogram → percentiles 0). §8.26.
    public async Task<QueueMetricsModel> GetQueueMetrics(string? application = null)
    {
        var ct = CancellationToken.None;
        var scope = application is null ? null : new Dictionary<string, string> { [Tags.Application] = application };
        var waitValue = new MetricRef(Names.QueueWait, scope);       // wait-time sum + latency histogram
        var waitCount = new MetricRef(Names.QueueWaitCount, scope);  // claim tally

        var wait = new Dictionary<string, ExecutionAccumulator>(StringComparer.Ordinal);
        foreach (var row in await _metrics.GetBreakdownAsync(waitCount, [Tags.Queue], null, ct))
        {
            Accumulator(wait, row.Tags[Tags.Queue]).Add(QueueWaitKeys.CountToken, row.Value);
        }

        foreach (var row in await _metrics.GetBreakdownAsync(waitValue, [Tags.Queue], null, ct))
        {
            Accumulator(wait, row.Tags[Tags.Queue]).Add(QueueWaitKeys.DurationToken, row.Value);
        }

        var (p95, p99) = await PercentilesAsync(waitValue, Tags.Queue, application is null, ct);

        // Backlog is a queue-global gauge (never application-attributable, §8.23), always read from the global
        // family. Each qbacklog key is a single UPSERT row, so a grouped sum is the current gauge value.
        var depth = (await _metrics.GetBreakdownAsync(new MetricRef(Names.QueueDepth), [Tags.Queue], null, ct))
            .ToDictionary(r => r.Tags[Tags.Queue], r => r.Value, StringComparer.Ordinal);
        var oldest = (await _metrics.GetBreakdownAsync(new MetricRef(Names.QueueOldestAge), [Tags.Queue], null, ct))
            .ToDictionary(r => r.Tags[Tags.Queue], r => r.Value, StringComparer.Ordinal);

        var queues = wait.Keys
            .Union(depth.Keys, StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .Select(queue =>
            {
                var acc = wait.GetValueOrDefault(queue) ?? new ExecutionAccumulator();

                return new QueueMetricModel
                {
                    Queue = queue,
                    ClaimedCount = acc.ExecutedCount,
                    AvgWaitMs = acc.AvgDurationMs,
                    P95WaitMs = p95.GetValueOrDefault(queue),
                    P99WaitMs = p99.GetValueOrDefault(queue),
                    BacklogDepth = depth.GetValueOrDefault(queue),
                    OldestAgeSeconds = oldest.GetValueOrDefault(queue),
                };
            })
            .ToList();

        return new QueueMetricsModel { Queues = queues };
    }

    private static IReadOnlyList<JobExecutionStatModel> Project(Dictionary<string, ExecutionAccumulator> map, Dictionary<string, double> p95, Dictionary<string, double> p99)
    {
        return
        [
            .. map
                .OrderByDescending(x => x.Value.ExecutedCount)
                .ThenBy(x => x.Key, StringComparer.Ordinal)
                .Select(x =>
                    new JobExecutionStatModel
                    {
                        Identifier = x.Key,
                        ExecutedCount = x.Value.ExecutedCount,
                        ErrorCount = x.Value.Errors,
                        ErrorRate = x.Value.ErrorRate,
                        AvgDurationMs = x.Value.AvgDurationMs,
                        P95DurationMs = p95.GetValueOrDefault(x.Key),
                        P99DurationMs = p99.GetValueOrDefault(x.Key),
                    }),
        ];
    }

    private IQueryable<JobModel> GetJobsByState(State state, string? application)
    {
        var jobs = Jobs()
            .Where(x => x.CurrentState == state);

        // Enqueued: exclude future-scheduled jobs (those show under Scheduled)
        if (state == State.Enqueued)
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            jobs = jobs.Where(x => x.ScheduleTime <= now);
        }

        // Optional per-application (creator/provenance) filter for the multi-app dashboard (§8.19).
        if (!string.IsNullOrEmpty(application))
        {
            jobs = jobs.Where(x => x.Application == application);
        }

        var ordered = IsTerminalState(state)
            ? OrderByFinishedTimeDescending(jobs)
            : OrderByCreateTimeDescending(jobs);

        return ordered
            .Select(x =>
                new JobModel
                {
                    Id = x.Id,
                    CurrentState = x.CurrentState,
                    CancellationMode = x.CancellationMode,
                    HandlerType = x.HandlerType,
                    CreateTime = x.CreateTime,
                    Message = x.Message,
                    ScheduleTime = x.ScheduleTime,
                    Type = x.Type,
                });
    }

    // Accumulates one job type / handler / queue's execution metrics from its seam breakdown rows: the execution
    // count (succeeded + failed, or per-queue claim count), error count (failed), and summed duration. The "dur"
    // token folds into DurationSum, never the execution Total, so the average is sum ÷ executions. Percentiles now
    // come from the seam (GetPercentileBreakdownAsync), not this accumulator.
    private sealed class ExecutionAccumulator
    {
        public long ExecutedCount { get; private set; }

        public long Errors { get; private set; }

        public long DurationSum { get; private set; }

        public double AvgDurationMs => ExecutedCount == 0 ? 0 : (double)DurationSum / ExecutedCount;

        public double ErrorRate => ExecutedCount == 0 ? 0 : (double)Errors / ExecutedCount;

        public void Add(string token, long value)
        {
            if (string.Equals(token, JobStatsKeys.DurationToken, StringComparison.Ordinal))
            {
                DurationSum += value;

                return;
            }

            ExecutedCount += value;

            if (string.Equals(token, JobStatsKeys.FailedToken, StringComparison.Ordinal))
            {
                Errors += value;
            }
        }
    }
}

// Terminal-state event types written by the worker on job finalization. Sourced from
// the State enum via nameof so adding a new terminal state surfaces the rename here.
// The IsTerminalState helper in JobQueryService is the paired check on the State side.
// Non-generic peer class because a static field on a generic type would be duplicated
// per closed type (S2743).
internal static class TerminalEvents
{
    public static readonly string[] EventTypes =
    [
        nameof(State.Completed),
        nameof(State.Failed),
        nameof(State.Deleted),
    ];
}
