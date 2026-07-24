using Cronos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Helper;

namespace Warp.Worker.Services;

/// <summary>
/// Polls recurring jobs and creates the next occurrence when due. Decouples scheduling
/// from execution — recurring jobs fire regardless of whether the previous execution
/// succeeded or failed. The dedup check uses the latest RecurringJobLog entry rather
/// than the oldest to catch the correct outstanding job.
/// </summary>
public sealed class RecurringJobScheduler<TContext> : IServerTask
    where TContext : DbContext
{
    private readonly DbContext _context;
    private readonly TimeProvider _time;
    private readonly WarpServerConfiguration _configuration;

    public RecurringJobScheduler(
        IWarpServerContext serverContext,
        TimeProvider time,
        IOptions<WarpServerConfiguration> configuration)
    {
        _context = serverContext.Context;
        _time = time;
        _configuration = configuration.Value;
    }

    public string Name => "RecurringJobScheduler";

    public string? LockKey => "warp:recurring-scheduler";

    public TimeSpan? DefaultInterval => _configuration.RecurringJobSchedulerInterval;

    public bool RerunImmediately => false;

    public async Task<string?> ExecuteAsync(CancellationToken ct)
    {
        var count = await ScheduleRecurringJobsAsync(ct);

        return count > 0 ? $"Scheduled {count} recurring jobs" : null;
    }

    internal async Task<int> ScheduleRecurringJobsAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var count = 0;

        var recurringJobs = await _context.Set<RecurringJob>()
            .Where(x => x.NextExecution != null && x.NextExecution <= now)
            .ToListAsync(ct);

        foreach (var recurringJob in recurringJobs)
        {
            var latestLog = await _context.Set<RecurringJobLog>()
                .Where(l => l.RecurringJobId == recurringJob.Id)
                .OrderByDescending(l => l.CreatedAt)
                .Select(l => new { l.JobId, JobState = l.Job != null ? l.Job.CurrentState : (State?)null })
                .FirstOrDefaultAsync(ct);

            if (latestLog?.JobState is State.Enqueued or State.Processing)
            {
                continue;
            }

            var nextExecution = CronExpression.Parse(recurringJob.Cron!)
                .GetNextOccurrence(DateTime.SpecifyKind(now, DateTimeKind.Utc));

            if (recurringJob.DisabledAt != null)
            {
                _context.Set<RecurringJobLog>().Add(new RecurringJobLog
                {
                    RecurringJobId = recurringJob.Id,
                    Skipped = true,
                    CreatedAt = now,
                });

                recurringJob.LastExecution = recurringJob.NextExecution;
                recurringJob.NextExecution = nextExecution;
                count++;

                continue;
            }

            var newJob = JobHelper.CreateJob(
                message: recurringJob.Message!,
                type: recurringJob.Type!,
                scheduleTime: now,
                queue: recurringJob.Queue,
                parentId: null,
                state: State.Enqueued,
                now: now);

            // This path bypasses Publisher, so root the trace here: each firing is its own
            // unit of work and gets a fresh trace (mirrors Publisher's root fallback).
            newJob.TraceId = newJob.Id;

            // Application (§ multi-app observability) is intentionally left null: a recurring definition
            // carries no owning application, so a firing has no publishing app to inherit. Recurring-job
            // firings are therefore unattributed until this is revisited.
            _context.Set<Job>().Add(newJob);
            _context.Set<JobLog>().Add(new JobLog
            {
                JobId = newJob.Id,
                EventType = "Created",
                Timestamp = now,
                Level = "Information",
                Message = $"Job {newJob.Id} created for recurring job {recurringJob.Id}",
            });
            _context.Set<RecurringJobLog>().Add(new RecurringJobLog
            {
                RecurringJobId = recurringJob.Id,
                JobId = newJob.Id,
                CreatedAt = now,
            });

            recurringJob.LastExecution = recurringJob.NextExecution;
            recurringJob.NextExecution = nextExecution;

            count++;
        }

        if (count > 0)
        {
            await _context.SaveChangesAsync(ct);
        }

        return count;
    }
}
