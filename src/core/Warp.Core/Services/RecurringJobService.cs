using Microsoft.EntityFrameworkCore;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Events;
using Warp.Core.Models;
using Warp.Core.Notifications;

namespace Warp.Core.Services;

public interface IRecurringJobService
{
    Task<PagedList<RecurringJobModel>> GetRecurringJobs(BaseListRequest request);

    Task<RecurringJobDetailModel?> GetRecurringJobById(int id);

    Task<PagedList<RecurringJobHistoryModel>> GetRecurringJobHistory(int id, BaseListRequest request);

    Task TriggerRecurringJob(int id);

    Task DeleteRecurringJob(int id);

    Task EnableRecurringJob(int id);

    Task DisableRecurringJob(int id);
}

public class RecurringJobService<TContext> : IRecurringJobService
    where TContext : DbContext
{
    private readonly TContext _context;
    private readonly TimeProvider _timeProvider;
    private readonly IWarpNotificationTransport _notificationTransport;
    private readonly ServerTaskSignals<TContext> _signals;

    public RecurringJobService(TContext context, TimeProvider timeProvider, IWarpNotificationTransport notificationTransport, ServerTaskSignals<TContext> signals)
    {
        _context = context;
        _timeProvider = timeProvider;
        _notificationTransport = notificationTransport;
        _signals = signals;
    }

    public async Task<PagedList<RecurringJobModel>> GetRecurringJobs(BaseListRequest request)
    {
        return await _context.Set<RecurringJob>()
            .OrderBy(x => x.NextExecution)
            .ThenBy(x => x.Name)
            .Select(x => new RecurringJobModel
            {
                Id = x.Id,
                Name = x.Name!,
                Cron = x.Cron!,
                Type = x.Type!,
                NextExecution = x.NextExecution,
                LastExecution = x.LastExecution,
                CreatedAt = x.CreatedAt,
                DisabledAt = x.DisabledAt,
            })
            .ToPagedListAsync(request);
    }

    public async Task<RecurringJobDetailModel?> GetRecurringJobById(int id)
    {
        return await _context.Set<RecurringJob>()
            .Where(x => x.Id == id)
            .Select(x => new RecurringJobDetailModel
            {
                Id = x.Id,
                Name = x.Name!,
                Cron = x.Cron!,
                Type = x.Type!,
                Message = x.Message,
                NextExecution = x.NextExecution,
                LastExecution = x.LastExecution,
                CreatedAt = x.CreatedAt,
                UpdatedAt = x.UpdatedAt,
                DisabledAt = x.DisabledAt,
            })
            .FirstOrDefaultAsync();
    }

    public async Task<PagedList<RecurringJobHistoryModel>> GetRecurringJobHistory(int id, BaseListRequest request)
    {
        return await _context.Set<RecurringJobLog>()
            .Where(l => l.RecurringJobId == id)
            .OrderByDescending(l => l.CreatedAt)
            .Select(l => new RecurringJobHistoryModel
            {
                JobId = l.JobId,
                CreatedAt = l.CreatedAt,
                JobExists = l.Job != null,
                Type = l.Job != null ? l.Job.Type : null,
                CurrentState = l.Job != null ? l.Job.CurrentState : null,
                Skipped = l.Skipped,
            })
            .ToPagedListAsync(request);
    }

    public async Task TriggerRecurringJob(int id)
    {
        var recurringJob = await _context.Set<RecurringJob>().FindAsync(id) ?? throw new ArgumentException("Recurring job not found.", nameof(id));

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var job = new Job
        {
            Type = recurringJob.Type,
            Message = recurringJob.Message,
            CreateTime = now,
            ScheduleTime = now,
            CurrentState = State.Enqueued,
            Queue = recurringJob.Queue,
        };

        await _context.Set<Job>().AddAsync(job);
        await _context.Set<JobLog>().AddAsync(new JobLog
        {
            JobId = job.Id,
            EventType = "Created",
            Timestamp = now,
            Level = "Information",
            Message = $"Job {job.Id} was created from recurring job {recurringJob.Id}",
        });
        _context.Set<RecurringJobLog>().Add(new RecurringJobLog
        {
            RecurringJobId = recurringJob.Id,
            JobId = job.Id,
            CreatedAt = now,
        });

        var pending = NotificationDispatch.CapturePending(_context);
        await _context.SaveChangesAsync();
        await NotificationDispatch.DispatchAsync(pending, _signals, _notificationTransport);
    }

    public async Task DeleteRecurringJob(int id)
    {
        var recurringJob = await _context.Set<RecurringJob>().FindAsync(id) ?? throw new ArgumentException("Recurring job not found.", nameof(id));
        _context.Set<RecurringJob>().Remove(recurringJob);
        await _context.SaveChangesAsync();
    }

    public async Task EnableRecurringJob(int id)
    {
        var recurringJob = await _context.Set<RecurringJob>().FindAsync(id) ?? throw new ArgumentException("Recurring job not found.", nameof(id));
        recurringJob.DisabledAt = null;
        await _context.SaveChangesAsync();
    }

    public async Task DisableRecurringJob(int id)
    {
        var recurringJob = await _context.Set<RecurringJob>().FindAsync(id) ?? throw new ArgumentException("Recurring job not found.", nameof(id));
        recurringJob.DisabledAt = _timeProvider.GetUtcNow().UtcDateTime;
        await _context.SaveChangesAsync();
    }
}
