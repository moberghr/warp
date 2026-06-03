using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Events;
using Warp.Core.Notifications;

namespace Warp.Worker.Services;

/// <summary>
/// Flips jobs in <see cref="State.Scheduled"/> to <see cref="State.Enqueued"/> when their
/// <c>ScheduleTime</c> has elapsed. Time-driven, always polling — there is no event trigger
/// for "schedule time elapsed", so this task never participates in DB-push wake-up.
/// </summary>
public sealed class ScheduledJobActivation<TContext> : IServerTask
    where TContext : DbContext
{
    private readonly TContext _context;
    private readonly TimeProvider _time;
    private readonly IWarpNotificationTransport _transport;
    private readonly WarpWorkerConfiguration _configuration;
    private readonly IWarpSqlQueries<TContext> _sqlQueries;
    private readonly ServerTaskSignals<TContext> _signals;

    public ScheduledJobActivation(
        TContext context,
        TimeProvider time,
        IWarpNotificationTransport transport,
        IOptions<WarpWorkerConfiguration> configuration,
        IWarpSqlQueries<TContext> sqlQueries,
        ServerTaskSignals<TContext> signals)
    {
        _context = context;
        _time = time;
        _transport = transport;
        _configuration = configuration.Value;
        _sqlQueries = sqlQueries;
        _signals = signals;
    }

    public string Name => "ScheduledJobActivation";

    public string? LockKey => "warp:scheduled-activation";

    public TimeSpan? DefaultInterval => _configuration.ScheduledActivationInterval;

    public async Task<string?> ExecuteAsync(CancellationToken ct)
    {
        var result = await ActivateWithNotifyAsync(ct);

        return result.Activated > 0 ? $"Activated {result.Activated} scheduled jobs" : null;
    }

    internal async Task<(int Activated, List<string> Queues)> ActivateWithNotifyAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        // One round-trip: atomically flip every due Scheduled row to Enqueued AND stream back
        // (Id, Queue, ScheduleTime) per activated row.
        var activated = await _sqlQueries.ActivateScheduledJobsAsync(_context, now, ct);

        if (activated.Count == 0)
        {
            return (0, []);
        }

        // Per-row JobLog "Enqueued" entry — atomic with the UPDATE via the xact-lock
        // transaction that wraps ExecuteAsync (LocksWithTransaction defaults to true on
        // IServerTask; ServerTaskLoop.TryAcquireLockAndExecuteAsync calls
        // RunUnderTransactionLockAsync which commits both this SaveChanges and the UPDATE
        // above together). If this insert fails the outer commit also fails — operators
        // never see "state=Enqueued with no log row" in the dashboard.
        foreach (var entry in activated)
        {
            _context.Set<JobLog>().Add(new JobLog
            {
                JobId = entry.Id,
                EventType = "Enqueued",
                Timestamp = now,
                Level = "Information",
                Message = "Enqueued from Scheduled — was scheduled at "
                    + entry.ScheduleTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            });
        }

        await _context.SaveChangesAsync(ct);

        var distinctQueues = activated
            .Select(a => a.Queue)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var notifications = distinctQueues.ConvertAll(q => new Notification(NotificationKind.JobEnqueued, q));
        await NotificationDispatch.DispatchAsync(notifications, _signals, _transport, ct);

        return (activated.Count, distinctQueues);
    }
}
