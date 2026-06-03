using Microsoft.EntityFrameworkCore;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;

namespace Warp.Core.Data.Queries;

/// <summary>
/// Provider-specific hand-written SQL for row-locking operations against <see cref="Job"/>.
/// Replaces the legacy <c>RowLockInterceptor</c> — each method returns tracked entities (or in
/// the atomic-claim case, the newly-claimed rows) so callers can continue to compose EF Core
/// state changes + <c>SaveChangesAsync</c> around them.
/// <para>
/// The full-fetch paths (worker + dispatcher) use atomic <c>UPDATE ... RETURNING/OUTPUT</c> so
/// there is no SELECT→UPDATE window. The cold paths (stale recovery, server cleanup, user
/// commands) use <c>SELECT ... FOR UPDATE [SKIP LOCKED]</c> inside the caller's transaction,
/// followed by the caller's own EF-tracked updates.
/// </para>
/// </summary>
public interface IWarpSqlQueries<TContext>
    where TContext : DbContext
{
    /// <summary>
    /// Atomically claims up to <paramref name="limit"/> rows in <c>State=Enqueued</c> matching
    /// any of <paramref name="queues"/>, flipping them to <c>State=Processing</c> and stamping
    /// the worker / timestamp. Uses <c>FOR UPDATE SKIP LOCKED</c> (PG) or
    /// <c>WITH (ROWLOCK, UPDLOCK, READPAST)</c> (SQL Server) so concurrent workers get distinct
    /// rows without contention. Returns the post-update row as a tracked entity.
    /// </summary>
    Task<List<Job>> ClaimEnqueuedJobsAsync(
        TContext context,
        string[] queues,
        Guid workerId,
        DateTime now,
        int limit,
        CancellationToken ct);

    /// <summary>
    /// Atomically claims up to <paramref name="limit"/> rows of <c>Kind=Message</c> in
    /// <c>State=Enqueued</c>, flipping them to <c>State=Processing</c> in one round-trip and
    /// returning the post-update rows as tracked entities. Uses <c>FOR NO KEY UPDATE SKIP LOCKED</c>
    /// (PG) / <c>WITH (ROWLOCK, UPDLOCK, READPAST)</c> (SQL Server) so concurrent routers across
    /// servers — when not serialised by the routing advisory lock — would still get distinct rows.
    /// Routing decisions are then made in-memory; child jobs and any state corrections
    /// (no-handler → Failed) commit together in a single <c>SaveChanges</c> at the end of the
    /// caller's batch.
    /// </summary>
    Task<List<Job>> ClaimEnqueuedMessagesAsync(TContext context, int limit, CancellationToken ct);

    /// <summary>
    /// Locks <c>Kind=Job</c> rows in <c>State=Processing</c> whose <c>LastKeepAlive</c> is older
    /// than <paramref name="cutoff"/> with SKIP LOCKED semantics. Returns tracked entities for
    /// the caller to transition to Enqueued / Failed / Deleted per its recovery policy.
    /// </summary>
    Task<List<Job>> LockStaleProcessingJobsAsync(
        TContext context,
        DateTime cutoff,
        CancellationToken ct);

    /// <summary>
    /// Locks a single job by ID with a blocking lock (WAIT — no SKIP LOCKED). Used by user-initiated
    /// commands (DeleteJob, RequeueJob) that must find the row and serialize against the worker's
    /// brief keep-alive UPDATEs; SKIP LOCKED would race those updates and falsely report "not found".
    /// </summary>
    Task<Job?> LockJobByIdWaitAsync(TContext context, Guid jobId, CancellationToken ct);

    /// <summary>
    /// Locks every <see cref="Server"/> row with a blocking lock (WAIT — no SKIP LOCKED).
    /// Used by server cleanup to freeze the set while it decides which servers are stale.
    /// The blocking lock serializes against <c>Heartbeat</c>'s per-row update so we can't
    /// delete a server that just sent a fresh heartbeat.
    /// </summary>
    Task<List<Server>> LockAllServersAsync(TContext context, CancellationToken ct);

    /// <summary>
    /// Atomic heartbeat: updates <c>last_heartbeat_time</c> / memory / CPU on the server row
    /// AND returns the server's <c>paused_at</c> alongside every worker group's
    /// <c>(id, paused_at)</c> for this server — all in a single round-trip. Replaces three
    /// separate queries (UPDATE + paused_at SELECT + worker_group SELECT) and saves two DB
    /// hops per heartbeat tick (every 3s by default).
    /// <para>
    /// <paramref name="memoryBytes"/> / <paramref name="cpuPercent"/> are nullable — the SQL keeps the
    /// existing column value when either is null (COALESCE / ISNULL). Returns <c>null</c> when
    /// the server row doesn't exist; caller treats this as the "server was cleaned up" signal.
    /// </para>
    /// </summary>
    Task<HeartbeatResult?> HeartbeatAsync(
        TContext context,
        Guid serverId,
        DateTime now,
        long? memoryBytes,
        double? cpuPercent,
        CancellationToken ct);

    /// <summary>
    /// Atomic activation of due scheduled jobs: flips <c>State.Scheduled</c> rows whose
    /// <c>ScheduleTime</c> has elapsed to <c>State.Enqueued</c> and RETURNS one row per activated
    /// job carrying <c>(Id, Queue, ScheduleTime)</c>. <c>ScheduleTime</c> is the row's existing
    /// value — the UPDATE does not change it — so it documents when the job had been scheduled to
    /// run. Caller deduplicates queues to fire one <c>JobEnqueued</c> notification per distinct
    /// queue, and writes one <c>Activated</c> <see cref="JobLog"/> per Id so operators have a
    /// per-row trail of the Scheduled→Enqueued transition.
    /// </summary>
    Task<List<(Guid Id, string Queue, DateTime ScheduleTime)>> ActivateScheduledJobsAsync(
        TContext context,
        DateTime now,
        CancellationToken ct);

    /// <summary>
    /// Acquires a row-level write lock on the <c>BackgroundServiceLease</c> row for
    /// <paramref name="serviceName"/> within the caller's open transaction, and returns it as
    /// a tracked entity (or <c>null</c> when no row exists yet). Uses <c>FOR UPDATE</c> (PG) /
    /// <c>WITH (UPDLOCK, ROWLOCK)</c> (SQL Server) so concurrent callers serialise without
    /// a TOCTOU window between the SELECT and the caller's subsequent INSERT/UPDATE.
    /// <para>
    /// Caller must have started a transaction before calling this method.
    /// </para>
    /// </summary>
    Task<BackgroundServiceLease?> LockLeaseByServiceNameAsync(
        TContext context,
        string serviceName,
        CancellationToken ct);

    /// <summary>
    /// Acquires a row-level write lock on the <c>BackgroundServiceDefinition</c> row for
    /// <paramref name="serviceName"/> within the caller's open transaction, and returns it as
    /// a tracked entity (or <c>null</c> when no row exists yet). Uses <c>FOR UPDATE</c> (PG) /
    /// <c>WITH (UPDLOCK, ROWLOCK)</c> (SQL Server) so concurrent callers serialise without
    /// a TOCTOU window between the SELECT and the caller's subsequent INSERT/UPDATE.
    /// <para>
    /// Caller must have started a transaction before calling this method.
    /// </para>
    /// </summary>
    Task<BackgroundServiceDefinition?> LockDefinitionByServiceNameAsync(
        TContext context,
        string serviceName,
        CancellationToken ct);

    /// <summary>
    /// Runs <paramref name="work"/> inside a transaction under a transaction-scoped advisory
    /// lock keyed by <paramref name="lockKey"/>. PG uses <c>pg_try_advisory_xact_lock</c>; SQL
    /// Server uses <c>sp_getapplock</c> with <c>@LockOwner='Transaction'</c>. The lock auto-
    /// releases on COMMIT/ROLLBACK so the implementation doesn't need a separate release call —
    /// 3 round-trips collapse into 1 transaction.
    /// <para>
    /// Returns <c>(true, result)</c> when the lock was acquired and the work committed.
    /// Returns <c>(false, default)</c> when another caller already holds the lock — in that
    /// case the transaction is rolled back and no side effects persist. If the work delegate
    /// throws, the transaction is rolled back and the exception propagates.
    /// </para>
    /// <para>
    /// The work delegate receives the same <paramref name="context"/> — its
    /// <c>SaveChangesAsync</c> calls join the outer transaction transparently. Do not call
    /// <c>BeginTransactionAsync</c> from inside the delegate.
    /// </para>
    /// </summary>
    Task<(bool LockHeld, T? Result)> RunUnderTransactionLockAsync<T>(
        TContext context,
        string lockKey,
        Func<TContext, CancellationToken, Task<T>> work,
        CancellationToken ct);
}
