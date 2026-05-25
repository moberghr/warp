using Microsoft.EntityFrameworkCore;
using Warp.Core.Data.Entities;
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
        var jobs = await Jobs()
            .Where(x => x.CurrentState == State.Processing)
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
            .AsQueryable().ToPagedListAsync(request);
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
                RetriedTimes = x.RetriedTimes,
                MaxRetries = x.MaxRetries,
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
            .ToListAsync();
    }

    public async Task<PagedList<JobModel>> GetFailedJobsByType(BaseListRequest request, string type)
    {
        return await Jobs()
            .Where(x => x.CurrentState == State.Failed && x.Type == type)
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

    /// <summary>
    /// Base query that returns only actual jobs (excludes messages and batches).
    /// </summary>
    private IQueryable<Job> Jobs()
    {
        return _context.Set<Job>().Where(j => j.Kind == JobKind.Job);
    }

    private IQueryable<JobModel> GetScheduledJobsQuery()
    {
        var query = Jobs()
            .Where(x => x.CurrentState == State.Scheduled)
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
                })
            .AsQueryable();

        return query;
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

        return jobs
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
                })
            .AsQueryable();
    }
}
