using Microsoft.EntityFrameworkCore;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Models;

namespace Warp.Core.Services;

public interface IJobGroupQueryService
{
    Task<PagedList<JobGroupModel>> GetJobGroups(JobKind kind, BaseListRequest request, string? state = null);

    Task<JobGroupDetailModel?> GetJobGroupById(Guid id);

    Task<PagedList<JobModel>> GetJobGroupJobs(Guid id, BaseListRequest request, string? state = null);

    Task<Dictionary<string, int>> GetJobGroupJobCounts(Guid id);
}

public class JobGroupQueryService<TContext> : IJobGroupQueryService
    where TContext : DbContext
{
    private readonly TContext _context;

    public JobGroupQueryService(TContext context)
    {
        _context = context;
    }

    public async Task<PagedList<JobGroupModel>> GetJobGroups(JobKind kind, BaseListRequest request, string? state = null)
    {
        var query = _context.Set<Job>()
            .Where(x => x.Kind == kind);

        if (kind == JobKind.Batch)
        {
            query = state switch
            {
                "processing" => query.Where(x => x.CurrentState == State.Processing),
                "awaiting" => query.Where(x => x.CurrentState == State.Awaiting),
                "completed" => query.Where(x => x.CurrentState == State.Completed),
                "failed" => query.Where(x => x.CurrentState == State.Failed),
                "deleted" => query.Where(x => x.CurrentState == State.Deleted),
                _ => query,
            };
        }
        else
        {
            query = state switch
            {
                "enqueued" => query.Where(x => x.CurrentState == State.Enqueued),
                "processing" => query.Where(x => x.CurrentState == State.Processing),
                "completed" => query.Where(x => x.CurrentState == State.Completed),
                "failed" => query.Where(x => x.CurrentState == State.Failed),
                _ => query,
            };
        }

        return await query
            .OrderByDescending(x => x.CreateTime)
            .Select(x => new JobGroupModel
            {
                Id = x.Id,
                Kind = x.Kind,
                Type = x.Type,
                Payload = x.Message,
                Queue = x.Queue,
                CurrentState = x.CurrentState,
                JobCount = x.JobCount,
                CreateTime = x.CreateTime,
                ContinuationOptions = x.ContinuationOptions,
                TotalJobs = x.ChildJobs.Count(c => c.Kind == JobKind.Job),
                CompletedJobs = x.ChildJobs.Count(c => c.Kind == JobKind.Job && c.CurrentState == State.Completed),
                FailedJobs = x.ChildJobs.Count(c => c.Kind == JobKind.Job && c.CurrentState == State.Failed),
            })
            .ToPagedListAsync(request);
    }

    public async Task<JobGroupDetailModel?> GetJobGroupById(Guid id)
    {
        var jobGroup = await _context.Set<Job>()
            .Where(x => x.Id == id && (x.Kind == JobKind.Message || x.Kind == JobKind.Batch))
            .Select(x => new JobGroupDetailModel
            {
                Id = x.Id,
                Kind = x.Kind,
                Type = x.Type,
                Payload = x.Message,
                Queue = x.Queue,
                CurrentState = x.CurrentState,
                JobCount = x.JobCount,
                CreateTime = x.CreateTime,
                ContinuationOptions = x.ContinuationOptions,
                ParentJobId = x.ParentJobId,
                ParentJobKind = x.ParentJob != null ? x.ParentJob.Kind : null,
                TraceId = x.TraceId,
            })
            .FirstOrDefaultAsync();

        if (jobGroup == null)
        {
            return null;
        }

        jobGroup.SpawnedJobsCount = await _context.Set<Job>()
            .Where(x => x.ParentJobId == id && x.Kind == JobKind.Job)
            .CountAsync();

        jobGroup.TotalJobs = jobGroup.SpawnedJobsCount;

        jobGroup.Logs = await _context.Set<JobLog>()
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
            })
            .ToListAsync();

        // Find continuation batches (child batches linked via ParentJobId)
        jobGroup.Continuations = await _context.Set<Job>()
            .Where(x => x.ParentJobId == id && x.Kind == JobKind.Batch)
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

        return jobGroup;
    }

    public async Task<PagedList<JobModel>> GetJobGroupJobs(Guid id, BaseListRequest request, string? state = null)
    {
        var query = _context.Set<Job>()
            .Where(x => x.ParentJobId == id && x.Kind == JobKind.Job);

        query = state switch
        {
            "awaiting" => query.Where(x => x.CurrentState == State.Awaiting),
            "scheduled" => query.Where(x => x.CurrentState == State.Scheduled),
            "enqueued" => query.Where(x => x.CurrentState == State.Enqueued),
            "processing" => query.Where(x => x.CurrentState == State.Processing),
            "completed" => query.Where(x => x.CurrentState == State.Completed),
            "failed" => query.Where(x => x.CurrentState == State.Failed),
            "deleted" => query.Where(x => x.CurrentState == State.Deleted),
            _ => query,
        };

        return await query
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

    public async Task<Dictionary<string, int>> GetJobGroupJobCounts(Guid id)
    {
        var counts = await _context.Set<Job>()
            .Where(x => x.ParentJobId == id && x.Kind == JobKind.Job)
            .GroupBy(x => x.CurrentState)
            .Select(g => new { State = g.Key, Count = g.Count() })
            .ToListAsync();

        return counts.ToDictionary(x => x.State.ToString().ToLowerInvariant(), x => x.Count);
    }
}
