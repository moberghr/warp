using Microsoft.EntityFrameworkCore;
using Warp.Core.Data.Entities;
using Warp.Core.Endpoints;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Models;

namespace Warp.Core.Services;

public interface IJobQueryService
{
    Task<PagedList<JobModel>> GetJobsList(BaseListRequest request, State state);

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
}

public class JobQueryService<TContext> : IJobQueryService
    where TContext : DbContext
{
    private readonly TContext _context;
    private readonly TimeProvider _timeProvider;

    public JobQueryService(TContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task<PagedList<JobModel>> GetJobsList(BaseListRequest request, State state)
    {
        return await GetJobsByState(state)
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
        return await GetJobsByState(State.Awaiting).ToPagedListAsync(request);
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
                    EndpointId = EndpointRouteId.Encode($"{origin.Method} {origin.RouteTemplate}"),
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

    private IQueryable<JobModel> GetJobsByState(State state)
    {
        var jobs = Jobs()
            .Where(x => x.CurrentState == state);

        // Enqueued: exclude future-scheduled jobs (those show under Scheduled)
        if (state == State.Enqueued)
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            jobs = jobs.Where(x => x.ScheduleTime <= now);
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
