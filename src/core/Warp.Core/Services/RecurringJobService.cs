using Microsoft.EntityFrameworkCore;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Events;
using Warp.Core.Models;
using Warp.Core.Notifications;

namespace Warp.Core.Services;

/// <summary>
/// Reads and operates on recurring job definitions. Every single-definition method keys on the
/// <c>name</c> the definition was registered under (<see cref="IRecurringJobPublisher.AddOrUpdateRecurringJob{T}"/>)
/// — the identity a caller already holds, unique-indexed, and stable across a delete-and-re-register
/// (the surrogate <c>Id</c> is not). Names are trimmed before lookup, so they match however the
/// caller spaced them. A name no definition matches throws <see cref="ArgumentException"/> from the
/// command methods and reads as "not found" (null / empty page) from the queries.
/// </summary>
public interface IRecurringJobService
{
    Task<PagedList<RecurringJobModel>> GetRecurringJobs(BaseListRequest request);

    Task<RecurringJobDetailModel?> GetRecurringJob(string name);

    Task<PagedList<RecurringJobHistoryModel>> GetRecurringJobHistory(string name, BaseListRequest request);

    Task TriggerRecurringJob(string name);

    Task DeleteRecurringJob(string name);

    Task EnableRecurringJob(string name);

    Task DisableRecurringJob(string name);
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
        var page = await _context.Set<RecurringJob>()
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

        await AttachLastRun(page.Items);

        return page;
    }

    public async Task<RecurringJobDetailModel?> GetRecurringJob(string name)
    {
        var jobName = RecurringJobName.Normalize(name);

        return await _context.Set<RecurringJob>()
            .Where(x => x.Name == jobName)
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

    public async Task<PagedList<RecurringJobHistoryModel>> GetRecurringJobHistory(string name, BaseListRequest request)
    {
        var jobName = RecurringJobName.Normalize(name);

        // Two-step over the name (§5.2): RecurringJobLog carries no navigation back to its
        // definition, so resolve the surrogate key the log rows are indexed on first.
        var id = await _context.Set<RecurringJob>()
            .Where(x => x.Name == jobName)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync();

        if (id is null)
        {
            return new PagedList<RecurringJobHistoryModel>(0, [], 0);
        }

        return await _context.Set<RecurringJobLog>()
            .Where(l => l.RecurringJobId == id)
            .OrderByDescending(l => l.CreatedAt)
            .Select(l => new RecurringJobHistoryModel
            {
                JobId = l.JobId,
                CreatedAt = l.CreatedAt,
                JobExists = l.Job != null,
                Type = l.Job != null ? l.Job.Type : null,
                CurrentState = l.Job != null ? l.Job.CurrentState : l.FinalState,
                Skipped = l.Skipped,
            })
            .ToPagedListAsync(request);
    }

    public async Task TriggerRecurringJob(string name)
    {
        var recurringJob = await LoadByName(name);

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

    public async Task DeleteRecurringJob(string name)
    {
        var recurringJob = await LoadByName(name);
        _context.Set<RecurringJob>().Remove(recurringJob);
        await _context.SaveChangesAsync();
    }

    public async Task EnableRecurringJob(string name)
    {
        var recurringJob = await LoadByName(name);
        recurringJob.DisabledAt = null;
        await _context.SaveChangesAsync();
    }

    public async Task DisableRecurringJob(string name)
    {
        var recurringJob = await LoadByName(name);
        recurringJob.DisabledAt = _timeProvider.GetUtcNow().UtcDateTime;
        await _context.SaveChangesAsync();
    }

    // Tracked load for the four command methods — they all mutate or remove the row, so this is the
    // one place that loads a full entity rather than a projection (§5.3).
    private async Task<RecurringJob> LoadByName(string name)
    {
        var jobName = RecurringJobName.Normalize(name);

        return await _context.Set<RecurringJob>()
            .Where(x => x.Name == jobName)
            .FirstOrDefaultAsync()
            ?? throw new ArgumentException($"Recurring job '{jobName}' not found.", nameof(name));
    }

    // Two-step fetch over the page's definitions (§5.2 — no Set<> subquery inside a projection):
    // newest non-skipped log id per definition, then those rows joined to their job via the nav property.
    private async Task AttachLastRun(List<RecurringJobModel> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var ids = items.ConvertAll(x => x.Id);

        var latestLogIds = await _context.Set<RecurringJobLog>()
            .Where(x => ids.Contains(x.RecurringJobId))
            .Where(x => !x.Skipped)
            .GroupBy(x => x.RecurringJobId)
            .Select(x => x.Max(y => y.Id))
            .ToListAsync();

        var runs = await _context.Set<RecurringJobLog>()
            .Where(x => latestLogIds.Contains(x.Id))
            .Select(x =>
                new
                {
                    x.RecurringJobId,
                    x.JobId,

                    // Live state while the Job row is there, otherwise the outcome stamped onto the
                    // audit row when it was swept — so a low-frequency definition keeps its result.
                    State = x.Job != null ? x.Job.CurrentState : x.FinalState,
                    CleanedUp = x.Job == null,
                })
            .ToListAsync();

        var byDefinition = runs.ToDictionary(x => x.RecurringJobId);

        foreach (var item in items)
        {
            if (!byDefinition.TryGetValue(item.Id, out var run))
            {
                continue;
            }

            item.HasLastRun = true;
            item.LastJobId = run.JobId;
            item.LastState = run.State;
            item.LastRunCleanedUp = run.CleanedUp;
        }
    }
}
