using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Logging;
using Warp.Core.NoRestart;
using Warp.Core.Services;
using Warp.Core.Webhooks;

namespace Warp.Worker.Services;

/// <summary>
/// Crash recovery for stalled in-flight work, in two sweeps. Jobs: finds rows in
/// <see cref="State.Processing"/> whose worker stopped refreshing <c>LastKeepAlive</c> past
/// <see cref="WarpServerConfiguration.InvisibilityTimeout"/> and either requeues them, fails them, or
/// honors a pending cancellation. Webhook deliveries: finds <c>Pending</c> rows whose executor job was
/// lost to a faulted outcome commit (<c>NextAttemptAt</c> more than
/// <c>WarpConfiguration.WebhookStuckDeliveryGrace</c> past) and re-enqueues one atomically via the
/// addon-registered <see cref="IWebhookRedeliveryEnqueuer"/> seam — a no-op in processes without
/// <c>AddWebhooks()</c>.
/// </summary>
public sealed class StaleJobRecovery<TContext> : IServerTask
    where TContext : DbContext
{
    private const int StuckDeliveryBatchSize = 100;

    private readonly DbContext _context;
    private readonly TContext _userContext;
    private readonly TimeProvider _time;
    private readonly IWarpSqlQueries<TContext> _sqlQueries;
    private readonly WarpServerConfiguration _configuration;
    private readonly IEnumerable<IWebhookRedeliveryEnqueuer> _webhookEnqueuers;

    public StaleJobRecovery(
        IWarpServerContext serverContext,
        TContext userContext,
        TimeProvider time,
        IWarpSqlQueries<TContext> sqlQueries,
        IOptions<WarpServerConfiguration> configuration,
        IEnumerable<IWebhookRedeliveryEnqueuer> webhookEnqueuers)
    {
        _context = serverContext.Context;
        _userContext = userContext;
        _time = time;
        _sqlQueries = sqlQueries;
        _configuration = configuration.Value;
        _webhookEnqueuers = webhookEnqueuers;
    }

    public string Name => "StaleJobRecovery";

    public string? LockKey => "warp:stale-job-recovery";

    public TimeSpan? DefaultInterval => _configuration.StaleJobRecoveryInterval;

    public bool RerunImmediately => false;

    public async Task<string?> ExecuteAsync(CancellationToken ct)
    {
        var result = await RecoverStaleJobsAsync(ct);
        var stuckDeliveries = await RecoverStuckWebhookDeliveriesAsync(ct);

        if (result.Total == 0 && stuckDeliveries == 0)
        {
            return null;
        }

        var message = $"Recovered {result.Total} stale jobs ({result.Requeued} requeued, {result.Failed} failed, {result.Deleted} deleted)";

        return stuckDeliveries == 0 ? message : $"{message}; re-enqueued {stuckDeliveries} stuck webhook deliveries";
    }

    internal async Task<StaleJobRecoveryResult> RecoverStaleJobsAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var cutoff = now - _configuration.InvisibilityTimeout;
        var restartByDefault = _configuration.RestartStaleJobsByDefault;

        // FOR NO KEY UPDATE SKIP LOCKED requires a wrapping transaction to keep the row
        // lock alive past the SELECT statement. ServerTaskLoop's xact-lock path provides
        // that wrap for the production hot path, but direct callers (tests, admin triggers
        // through DI) don't get it. Detect and open one only when needed — opening a nested
        // tx under ServerTaskLoop's xact-lock would throw InvalidOperationException.
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

                // The always-on meter moves with the DB counter, per requeue — recovery requeues are one of
                // the two reasons an Otel-only deployment (JobMetricsSink = Otel, no stats: rows) most needs
                // on a dashboard after an incident. Same fire-at-decision stance as the worker finalization
                // sites: a rolled-back sweep may over-count the meter by a tick, which telemetry tolerates.
                WarpTelemetry.RecordJobRequeued(
                    job.Type,
                    string.IsNullOrEmpty(job.Queue) ? "default" : job.Queue,
                    OutcomeReasonTokens.For(OutcomeReason.Recovery),
                    _configuration.ApplicationName);
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
            await ownedTx.CommitAsync(ct);
        }

        return new StaleJobRecoveryResult(requeued, failed, deleted);
    }

    // A Pending WebhookDelivery whose NextAttemptAt is more than WebhookStuckDeliveryGrace past has lost
    // its executor job: the executor's outcome commit faulted after the attempt claim, and the retry job
    // was staged in that same failed transaction. Nothing scans NextAttemptAt by design (§8.20) and
    // Redeliver rejects Pending rows, so this sweep is the only path back.
    //
    // The whole recovery runs on the USER context — unlike the stale-job work above — because the
    // enqueuer's publisher stages the executor job on the same scoped TContext (outbox): the guarded
    // NextAttemptAt bump and the job then commit in ONE explicit transaction, so the row is never bumped
    // without its job nor the job created without the bump (mirrors WebhookCommandService.Redeliver). The
    // server context's ambient xact-lock transaction is a different connection and is not involved.
    internal async Task<int> RecoverStuckWebhookDeliveriesAsync(CancellationToken ct)
    {
        var enqueuer = _webhookEnqueuers.FirstOrDefault();
        if (enqueuer is null)
        {
            return 0;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var threshold = now - _configuration.WebhookStuckDeliveryGrace;

        var stuck = await _userContext.Set<WebhookDelivery>()
            .AsNoTracking()
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
                })
            .ToListAsync(ct);

        // Executor-job states that mean a stuck-looking delivery still has live work in flight.
        State[] liveJobStates = [State.Enqueued, State.Scheduled, State.Awaiting, State.Processing];

        var recovered = 0;
        foreach (var row in stuck)
        {
            // A stuck row is one whose executor job was LOST. If a live job still exists — workers merely
            // backlogged past the grace — enqueueing another would cost a duplicate (at-least-once) attempt.
            // The payload-substring match is a heuristic (the serialized job carries the delivery id); the
            // executor's attempt claim remains the correctness backstop should it ever miss.
            var idText = row.Id.ToString();
            var hasLiveJob = await _userContext.Set<Job>()
                .Where(x => x.Queue == WebhookConstants.Queue)
                .Where(x => liveJobStates.Contains(x.CurrentState))
                .Where(x => x.Message != null)
                .Where(x => x.Message!.Contains(idText))
                .AnyAsync(ct);

            if (hasLiveJob)
            {
                continue;
            }

            // Bump + enqueue commit atomically: the NextAttemptAt equality check makes the bump a claim
            // (two sweeps never double-recover), and a rollback undoes both — no bumped-without-job or
            // job-without-bump state can exist.
            await using var tx = await _userContext.Database.BeginTransactionAsync(ct);

            var claimed = await _userContext.Set<WebhookDelivery>()
                .Where(x => x.Id == row.Id)
                .Where(x => x.Status == WebhookDeliveryStatus.Pending)
                .Where(x => x.NextAttemptAt == row.NextAttemptAt)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.NextAttemptAt, now + _configuration.WebhookStuckDeliveryGrace),
                    ct);

            if (claimed != 1)
            {
                await tx.RollbackAsync(ct);

                continue;
            }

            await enqueuer.EnqueueAsync(row.Id, ct);
            await tx.CommitAsync(ct);
            recovered++;
        }

        return recovered;
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
