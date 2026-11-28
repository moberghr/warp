using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Events;
using Warp.Core.Handlers;
using Warp.Core.Helper;
using Warp.Core.Logging;
using Warp.Core.NoRestart;
using Warp.Core.Notifications;
using Warp.Core.Services;
using Warp.Core.Webhooks;

namespace Warp.Worker.Services;

/// <summary>
/// Crash recovery for stalled in-flight work, in two sweeps. Jobs: finds rows in
/// <see cref="State.Processing"/> whose worker stopped refreshing <c>LastKeepAlive</c> past
/// <see cref="WarpServerConfiguration.InvisibilityTimeout"/> and either requeues them, fails them, or
/// honors a pending cancellation. Webhook deliveries: finds <c>Pending</c> rows whose executor job was
/// lost to a faulted outcome commit (<c>NextAttemptAt</c> more than
/// <c>WarpConfiguration.WebhookStuckDeliveryGrace</c> past) and stages a fresh executor job for each.
/// <para>
/// Both sweeps run on the server context (§2.14), so both share ONE connection: the guarded
/// <c>NextAttemptAt</c> claim and the executor job it stages commit together with the task host's lock
/// transaction, and the live-job probe cannot block on Job row locks the stale-job sweep is still holding.
/// </para>
/// </summary>
public sealed class StaleJobRecovery<TContext> : IServerTask
    where TContext : DbContext
{
    private const int StuckDeliveryBatchSize = 100;

    // REMOVE in 9.0 (issue #367): the queue 8.1 and earlier put every webhook executor job on. Only the catch-up in
    // MoveLegacyWebhookJobsAsync and the stuck sweep's live-job check for pre-8.2 deliveries read it.
    private const string LegacyWebhookQueue = "warp:webhooks";

    private readonly DbContext _context;
    private readonly TimeProvider _time;
    private readonly IWarpSqlQueries<TContext> _sqlQueries;
    private readonly WarpServerConfiguration _configuration;
    private readonly WebhookSendBehavior _webhookBehavior;
    private readonly IWarpNotificationTransport _notificationTransport;
    private readonly ServerTaskSignals<TContext> _signals;

    // Wake-ups buffered until the sweep's transaction COMMITS, for the same reason as the meters below and
    // per §8.9/§8.25 — a wake fired from ExecuteAsync points workers at rows the lock transaction has not
    // committed yet, and they go back to sleep having found nothing. A set because both sweeps contribute
    // and a queue only needs announcing once (Notification is a record struct).
    private readonly HashSet<Notification> _pendingNotifications = [];

    // Requeue meter emissions buffered until the sweep's transaction COMMITS. Under the task host this
    // method runs inside the lock transaction (LocksWithTransaction, §8.25) — SaveChangesAsync only
    // flushes, and an inline emission would record requeues a rollback then undoes, which the next sweep
    // re-records: the meter drifts permanently above the DB counter it mirrors. The task is scoped (one
    // instance per iteration), so the buffer never outlives its run.
    private readonly List<(string? Type, string Queue)> _pendingRequeueMeters = [];

    public StaleJobRecovery(
        IWarpServerContext serverContext,
        [FromKeyedServices(DatabaseClock.Key)] TimeProvider time,
        IWarpSqlQueries<TContext> sqlQueries,
        IOptions<WarpServerConfiguration> configuration,
        IOptions<WebhookSendBehavior> webhookBehavior,
        IWarpNotificationTransport notificationTransport,
        ServerTaskSignals<TContext> signals)
    {
        _context = serverContext.Context;
        _time = time;
        _sqlQueries = sqlQueries;
        _configuration = configuration.Value;
        _webhookBehavior = webhookBehavior.Value;
        _notificationTransport = notificationTransport;
        _signals = signals;
    }

    public string Name => "StaleJobRecovery";

    public string? LockKey => "warp:stale-job-recovery";

    public TimeSpan? DefaultInterval => _configuration.StaleJobRecoveryInterval;

    public bool RerunImmediately => false;

    public async Task<string?> ExecuteAsync(CancellationToken ct)
    {
        var result = await RecoverStaleJobsAsync(ct);

        // Before the stuck sweep: a pre-8.2 delivery's live executor still on warp:webhooks (possibly just requeued
        // by the sweep above) must already be on the webhook queue when the sweep looks for it there, or the delivery
        // gets a duplicate executor. Same connection and transaction, so the sweep sees the move.
        var movedLegacy = await MoveLegacyWebhookJobsAsync(ct);
        var stuckDeliveries = await RecoverStuckWebhookDeliveriesAsync(ct);

        if (result.Total == 0 && stuckDeliveries == 0 && movedLegacy == 0)
        {
            return null;
        }

        var message = $"Recovered {result.Total} stale jobs ({result.Requeued} requeued, {result.Failed} failed, {result.Deleted} deleted)";
        message = stuckDeliveries == 0 ? message : $"{message}; re-enqueued {stuckDeliveries} stuck webhook deliveries";

        return movedLegacy == 0 ? message : $"{message}; moved {movedLegacy} webhook jobs off {LegacyWebhookQueue}";
    }

    internal async Task<StaleJobRecoveryResult> RecoverStaleJobsAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var cutoff = now - _configuration.InvisibilityTimeout;
        var restartByDefault = _configuration.RestartStaleJobsByDefault;

        // FOR NO KEY UPDATE SKIP LOCKED requires a wrapping transaction to keep the row
        // lock alive past the SELECT statement. The task host's transaction (ServerTaskRunner) provides
        // that wrap for the production hot path, but direct callers (tests, admin triggers
        // through DI) don't get it. Detect and open one only when needed — opening a nested
        // tx under the task host's transaction would throw InvalidOperationException.
        var hasOuterTx = _context.Database.CurrentTransaction != null;
        await using var ownedTx = hasOuterTx
            ? null
            : await _context.Database.BeginTransactionAsync(ct);

        var staleJobs = await _sqlQueries.LockStaleProcessingJobsAsync(_context, cutoff, ct);

        var requeued = 0;
        var failed = 0;
        var deleted = 0;

        foreach (var job in staleJobs)
        {
            job.CurrentWorkerId = null;
            job.LastKeepAlive = null;
            job.RunId = null;

            if (job.CancellationMode != CancellationMode.None)
            {
                job.CurrentState = State.Deleted;
                job.CancellationMode = CancellationMode.None;
                job.ExpireAt = now.AddDays(1);
                _context.Set<JobLog>().Add(new JobLog
                {
                    JobId = job.Id,
                    EventType = "Deleted",
                    Timestamp = now,
                    Level = "Warning",
                    Message = "Cancelled by crash recovery — cancellation was pending when worker stopped",
                });
                deleted++;
                AnnounceFinalized(job);

                continue;
            }

            var canRestart = ReadCanBeRestarted(job.Metadata) ?? restartByDefault;

            if (canRestart)
            {
                job.CurrentState = State.Enqueued;
                _context.Set<JobLog>().Add(new JobLog
                {
                    JobId = job.Id,
                    EventType = "Requeued",
                    Timestamp = now,
                    Level = "Warning",
                    Message = "Requeued by crash recovery — worker stopped responding",
                });
                requeued++;

                var queue = string.IsNullOrEmpty(job.Queue) ? "default" : job.Queue;

                // Buffered, not emitted — see _pendingRequeueMeters. The always-on meter moves with the DB
                // counter, per requeue: recovery requeues are one of the two reasons an Otel-only deployment
                // (JobMetricsSink = Otel, no stats: rows) most needs on a dashboard after an incident.
                _pendingRequeueMeters.Add((job.Type, queue));

                // A requeue is an enqueue site and must announce itself like every other one (§6.3/§2.9).
                // CapturePending cannot see it — that walks the change tracker for ADDED Job rows, and a
                // recovered job is Modified — so the wake is built from the flip itself, as
                // ScheduledJobActivation does for the other Modified→Enqueued site. Without it a recovered
                // job waits out a worker's backoff poll, up to MaxPollingInterval (5 minutes under
                // UseDatabasePush) after a crash, which is exactly when latency matters most.
                _pendingNotifications.Add(new Notification(NotificationKind.JobEnqueued, queue));
            }
            else
            {
                job.CurrentState = State.Failed;
                job.ExpireAt = null;
                _context.Set<JobLog>().Add(new JobLog
                {
                    JobId = job.Id,
                    EventType = "Failed",
                    Timestamp = now,
                    Level = "Error",
                    Message = "Failed by crash recovery — job opted out of restart",
                });
                failed++;
                AnnounceFinalized(job);
            }
        }

        // One row per key carrying the batch count, not one per job — CounterAggregator sums either way and
        // this is a sweep, not a hot path. Counts the rows actually flipped, so each key agrees exactly with
        // the log rows written above. Append-only: a recovery outcome is an event that happened.
        var hourSuffix = now.ToString("yyyy-MM-dd-HH", CultureInfo.InvariantCulture);
        var reason = OutcomeReasonTokens.For(OutcomeReason.Recovery);

        AddStatCounters("stats:requeued", requeued, hourSuffix);
        AddStatCounters($"stats:requeued-{reason}", requeued, hourSuffix);
        AddStatCounters("stats:deleted", deleted, hourSuffix);
        AddStatCounters("stats:failed", failed, hourSuffix);

        await _context.SaveChangesAsync(ct);
        if (ownedTx != null)
        {
            // Direct caller (test, admin trigger): we own the transaction, so this commit is the durable
            // point — drain here. Under the task host ownedTx is null and the host's lock transaction
            // commits after ExecuteAsync returns; the host then invokes OnCommittedAsync, which drains.
            await ownedTx.CommitAsync(ct);
            EmitPendingRequeueMeters();
            await DispatchPendingNotificationsAsync();
        }

        return new StaleJobRecoveryResult(requeued, failed, deleted);
    }

    // Post-commit hook (§8.25): the task host calls this only after its lock transaction has committed —
    // and never on a throw/rollback, which leaves the buffers to die with this scoped instance.
    public async Task OnCommittedAsync(CancellationToken ct)
    {
        EmitPendingRequeueMeters();

        await DispatchPendingNotificationsAsync();
    }

    private void EmitPendingRequeueMeters()
    {
        foreach (var (type, queue) in _pendingRequeueMeters)
        {
            WarpTelemetry.RecordJobRequeued(
                type,
                queue,
                OutcomeReasonTokens.For(OutcomeReason.Recovery),
                _configuration.ApplicationName);
        }

        _pendingRequeueMeters.Clear();
    }

    // A Pending WebhookDelivery whose NextAttemptAt is more than WebhookStuckDeliveryGrace past has lost
    // its executor job: the executor's outcome commit faulted after the attempt claim, and the retry job
    // was staged in that same failed transaction. Nothing scans NextAttemptAt by design (§8.20) and
    // Redeliver rejects Pending rows, so this sweep is the only path back.
    //
    // Runs on the server context like the stale-job sweep above, and stages the executor job directly
    // (JobHelper + a Created log — the RecurringJobScheduler / MessageRouter pattern) instead of going
    // through IPublisher, which is bound to the user's TContext. Routing it there put the claim bump and
    // the job on a SECOND connection: they could no longer commit with the task host's lock transaction,
    // and the live-job probe below would wait on the Job row locks RecoverStaleJobsAsync still holds on the
    // first one — under read-committed locking (SQL Server without RCSI) that is a wait on a transaction
    // which cannot commit until this method returns.
    internal async Task<int> RecoverStuckWebhookDeliveriesAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var threshold = now - _configuration.WebhookStuckDeliveryGrace;

        var stuck = await _context.Set<WebhookDelivery>()
            .Where(x => x.Status == WebhookDeliveryStatus.Pending)
            .Where(x => x.NextAttemptAt != null)
            .Where(x => x.NextAttemptAt < threshold)
            .OrderBy(x => x.NextAttemptAt)
            .Take(StuckDeliveryBatchSize)
            .Select(x =>
                new
                {
                    x.Id,
                    x.NextAttemptAt,
                    x.Queue,
                })
            .ToListAsync(ct);

        if (stuck.Count == 0)
        {
            return 0;
        }

        // Same outer-transaction detection as RecoverStaleJobsAsync: the claim bump executes immediately
        // while the staged job waits for SaveChanges, so the two only land together inside a transaction.
        var hasOuterTx = _context.Database.CurrentTransaction != null;
        await using var ownedTx = hasOuterTx
            ? null
            : await _context.Database.BeginTransactionAsync(ct);

        // Executor-job states that mean a stuck-looking delivery still has live work in flight.
        State[] liveJobStates = [State.Enqueued, State.Scheduled, State.Awaiting, State.Processing];

        var recovered = 0;
        foreach (var row in stuck)
        {
            // A stuck row is one whose executor job was LOST. If a live job still exists — workers merely
            // backlogged past the grace, or the stale-job sweep above just requeued it — enqueueing another
            // would cost a duplicate (at-least-once) attempt. The payload-substring match is a heuristic
            // (the serialized job carries the delivery id); the executor's attempt claim remains the
            // correctness backstop should it ever miss.
            var idText = row.Id.ToString();
            var queue = _webhookBehavior.ResolveQueue(row.Queue, _configuration);

            // A pre-8.2 delivery (no queue) can still have its live executor on warp:webhooks where the bridge does not
            // move it (Scheduled, not yet activated). REMOVE the legacy arm in 9.0 with the bridge (#367).
            var legacyQueue = row.Queue == null ? LegacyWebhookQueue : queue;
            var hasLiveJob = await _context.Set<Job>()
                .Where(x => x.Queue == queue || x.Queue == legacyQueue)
                .Where(x => liveJobStates.Contains(x.CurrentState))
                .Where(x => x.Message != null)
                .Where(x => x.Message!.Contains(idText))
                .AnyAsync(ct);

            if (hasLiveJob)
            {
                continue;
            }

            // The NextAttemptAt equality check makes the bump a claim — it matches zero rows when another
            // sweep got there first, which is the signal to leave this delivery alone.
            var claimed = await _context.Set<WebhookDelivery>()
                .Where(x => x.Id == row.Id)
                .Where(x => x.Status == WebhookDeliveryStatus.Pending)
                .Where(x => x.NextAttemptAt == row.NextAttemptAt)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.NextAttemptAt, now + _configuration.WebhookStuckDeliveryGrace),
                    ct);

            if (claimed != 1)
            {
                continue;
            }

            StageExecutorJob(row.Id, queue, now);
            recovered++;
        }

        if (recovered > 0)
        {
            // Captured before the save: CapturePending reads Added Job entries off the change tracker and
            // SaveChanges flips them to Unchanged. Unioned, never assigned — the job sweep above may have
            // buffered its own wakes into the same set.
            _pendingNotifications.UnionWith(NotificationDispatch.CapturePending(_context));

            await _context.SaveChangesAsync(ct);
        }

        if (ownedTx != null)
        {
            // Direct caller (test, admin trigger): we own the transaction, so this commit is the durable
            // point — drain here. Under the task host ownedTx is null and OnCommittedAsync drains instead.
            await ownedTx.CommitAsync(ct);
            await DispatchPendingNotificationsAsync();
        }

        return recovered;
    }

    // REMOVE in 9.0 (issue #367). Until 8.2 every webhook executor job ran on the special warp:webhooks queue, which
    // every server drained on its own. Nothing drains it now, so Enqueued jobs a deployment still has there are moved
    // to this server's webhook queue, where its workers or the host's AddWorkerGroup pick them up. Only Enqueued:
    // a legacy retry still Scheduled there is flipped to Enqueued by ScheduledJobActivation when due and moved by the
    // next run, so it never has to be found among every Scheduled job. Idempotent: a moved row no longer matches. An
    // 8.1 process still running in a rolling deploy can put new ones there; the next run moves those too.
    internal async Task<int> MoveLegacyWebhookJobsAsync(CancellationToken ct)
    {
        var queue = _webhookBehavior.ResolveQueue(null, _configuration);

        // Kind and CurrentState as literals so the statement reads the claim partial index (§6.15).
        var moved = await _context.Set<Job>()
            .Where(x => x.Kind == JobKind.Job)
            .Where(x => x.CurrentState == State.Enqueued)
            .Where(x => x.Queue == LegacyWebhookQueue)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Queue, queue), ct);

        if (moved == 0)
        {
            return 0;
        }

        // ExecuteUpdate modifies rows CapturePending never sees, so the wake is built here (§6.3). Under the task host
        // it is sent from OnCommittedAsync; a direct caller has no outer transaction and the update is already
        // committed, so it is sent now.
        _pendingNotifications.Add(new Notification(NotificationKind.JobEnqueued, queue));

        if (_context.Database.CurrentTransaction == null)
        {
            await DispatchPendingNotificationsAsync();
        }

        return moved;
    }

    /// <summary>
    /// Writes a <c>stats:</c> lifetime row and its hourly bucket sibling, or nothing when the sweep flipped
    /// no rows for that key. Never one without the other — a lifetime row with no bucket makes the lifetime
    /// total disagree with the sum of its own buckets, which is what the Counters chart plots against.
    /// </summary>
    private void AddStatCounters(string key, int count, string hourSuffix)
    {
        if (count <= 0)
        {
            return;
        }

        _context.Set<Counter>().Add(new Counter { Key = key, Value = count });
        _context.Set<Counter>().Add(new Counter { Key = $"{key}:{hourSuffix}", Value = count });
    }

    // Immediate (not scheduled) so signal-driven pickup applies — mirrors the first attempt in SendAsync.
    private void StageExecutorJob(Guid deliveryId, string queue, DateTime now)
        => WebhookExecutorJobs.Stage(_context, deliveryId, queue, now, now, _configuration.ApplicationName, "crash recovery for stuck delivery");

    // A job recovery ends (deleted or failed) is settled work: it can be what completes its batch or
    // message, and a continuation may be waiting on it. The Orchestrator checks what a wake names and
    // sweeps the rest only once per OrchestrationInterval (rule 6.11), so recovery names the job and its
    // parent like a worker does. Buffered with the other wakes and sent after the commit.
    private void AnnounceFinalized(Job job) =>
        _pendingNotifications.Add(new Notification(NotificationKind.JobFinalized, FinalizedParentsPayload.Encode(job.ParentJobId, job.Id)));

    private async Task DispatchPendingNotificationsAsync()
    {
        if (_pendingNotifications.Count == 0)
        {
            return;
        }

        var notifications = _pendingNotifications.ToList();
        _pendingNotifications.Clear();

        // CancellationToken.None: a shutdown mid-iteration still leaves the jobs committed, and another
        // server's workers should hear about them rather than wait out their own backoff.
        await NotificationDispatch.DispatchAsync(notifications, _signals, _notificationTransport, CancellationToken.None);
    }

    private static bool? ReadCanBeRestarted(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson))
        {
            return null;
        }

        var dict = MetadataSerializer.Deserialize(metadataJson);
        var meta = MetadataFactory.Create<ICanBeRestartedMetadata>(dict);

        return meta.CanBeRestarted;
    }
}
