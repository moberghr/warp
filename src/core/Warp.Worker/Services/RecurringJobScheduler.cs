using Cronos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Events;
using Warp.Core.Helper;
using Warp.Core.Notifications;

namespace Warp.Worker.Services;

/// <summary>
/// Polls recurring jobs and creates the next occurrence when due. Decouples scheduling
/// from execution — recurring jobs fire regardless of whether the previous execution
/// succeeded or failed. The dedup check uses the latest RecurringJobLog entry rather
/// than the oldest to catch the correct outstanding job.
/// <para>
/// A firing lands directly in <see cref="State.Enqueued"/>, so it must announce itself like every
/// other enqueue site (Publisher, MessageRouter, ScheduledJobActivation, the worker outbox): without
/// the <c>JobEnqueued</c> wake, an idle worker only finds the row on its next backoff poll — up to
/// <c>MaxPollingInterval</c> later, which <c>UseDatabasePush()</c> raises to 5 minutes precisely
/// because push is assumed to do the waking. The notifications are captured pre-save and dispatched
/// from <see cref="OnCommittedAsync"/> (§8.25): <see cref="ExecuteAsync"/> runs inside the server-task
/// host's lock transaction, so a dispatch there would wake workers onto rows that are not committed yet.
/// </para>
/// </summary>
public sealed class RecurringJobScheduler<TContext> : IServerTask
    where TContext : DbContext
{
    private readonly DbContext _context;
    private readonly TimeProvider _time;
    private readonly IWarpNotificationTransport _notificationTransport;
    private readonly ServerTaskSignals<TContext> _signals;
    private readonly WarpServerConfiguration _configuration;
    private List<Notification> _pendingNotifications = [];

    public RecurringJobScheduler(
        IWarpServerContext serverContext,
        TimeProvider time,
        IWarpNotificationTransport notificationTransport,
        ServerTaskSignals<TContext> signals,
        IOptions<WarpServerConfiguration> configuration)
    {
        _context = serverContext.Context;
        _time = time;
        _notificationTransport = notificationTransport;
        _signals = signals;
        _configuration = configuration.Value;
    }

    public string Name => "RecurringJobScheduler";

    public string? LockKey => "warp:recurring-scheduler";

    public TimeSpan? DefaultInterval => _configuration.RecurringJobSchedulerInterval;

    public bool RerunImmediately => false;

    public async Task<string?> ExecuteAsync(CancellationToken ct)
    {
        var result = await ScheduleRecurringJobsAsync(ct);

        // Both numbers, always, once anything happened: a skip is not a scheduled job, and reporting
        // it as one made a purely disabled definition read as "Scheduled 1 recurring jobs" in the
        // server-task history — the same false signal the dashboard's Last Execution column had.
        if (result.Scheduled == 0 && result.Skipped == 0)
        {
            return null;
        }

        return $"Scheduled {result.Scheduled} recurring jobs, skipped {result.Skipped} disabled";
    }

    // Post-commit (§8.25): the firings buffered by ScheduleRecurringJobsAsync are durable by the time the
    // host calls this, so the wake can't race ahead of the rows it announces. CancellationToken.None because
    // a shutdown mid-iteration still leaves the jobs committed — another server's dispatcher should hear
    // about them rather than wait out its own backoff.
    public async Task OnCommittedAsync(CancellationToken ct)
    {
        if (_pendingNotifications.Count == 0)
        {
            return;
        }

        var notifications = _pendingNotifications;
        _pendingNotifications = [];

        await NotificationDispatch.DispatchAsync(notifications, _signals, _notificationTransport, CancellationToken.None);
    }

    internal async Task<(int Scheduled, int Skipped)> ScheduleRecurringJobsAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var scheduled = 0;
        var skipped = 0;

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

                // LastExecution is deliberately NOT advanced here: it names the last occurrence that
                // actually ran, and a skip ran nothing. Advancing it made a disabled definition read as
                // if it were still firing on the dashboard (the reported bug) while AttachLastRun
                // (which filters !Skipped) correctly showed no last run beside it.
                //
                // NextExecution IS advanced, so the skip cadence stays cron-paced. Freezing it would
                // leave the row permanently due and write one skip log per scheduler tick instead of
                // one per occurrence. The dashboard hides it while disabled rather than relying on
                // this column standing still.
                recurringJob.NextExecution = nextExecution;
                skipped++;

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

            scheduled++;
        }

        // A skip still mutates rows (the Skipped log plus the advanced NextExecution), so it has to
        // open the save even though it scheduled nothing.
        if (scheduled + skipped > 0)
        {
            // Capture before the save — CapturePending reads Added Job entries off the change tracker, and
            // SaveChanges flips them to Unchanged. Same helper the other enqueue sites use, so queue
            // normalisation (null/empty → "default") and per-queue deduplication are identical here.
            _pendingNotifications = NotificationDispatch.CapturePending(_context);

            await _context.SaveChangesAsync(ct);
        }

        return (scheduled, skipped);
    }
}
