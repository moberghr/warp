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
    /// <para>
    /// With a <paramref name="watermark"/> (the dispatcher's), each queue's claim starts where the
    /// watermark says and records what it took (rule 6.16); without one it reads from the head.
    /// </para>
    /// </summary>
    Task<List<Job>> ClaimEnqueuedJobsAsync(
        DbContext context,
        string[] queues,
        Guid workerId,
        DateTime now,
        int limit,
        ClaimWatermark? watermark,
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
    Task<List<Job>> ClaimEnqueuedMessagesAsync(DbContext context, int limit, CancellationToken ct);

    /// <summary>
    /// Locks <c>Kind=Job</c> rows in <c>State=Processing</c> whose <c>LastKeepAlive</c> is older
    /// than <paramref name="cutoff"/> with SKIP LOCKED semantics. Returns tracked entities for
    /// the caller to transition to Enqueued / Failed / Deleted per its recovery policy.
    /// </summary>
    Task<List<Job>> LockStaleProcessingJobsAsync(
        DbContext context,
        DateTime cutoff,
        CancellationToken ct);

    /// <summary>
    /// Locks a single job by ID with a blocking lock (WAIT — no SKIP LOCKED). Used by user-initiated
    /// commands (DeleteJob, RequeueJob) that must find the row and serialize against the worker's
    /// brief keep-alive UPDATEs; SKIP LOCKED would race those updates and falsely report "not found".
    /// </summary>
    Task<Job?> LockJobByIdWaitAsync(DbContext context, Guid jobId, CancellationToken ct);

    /// <summary>
    /// Locks every <see cref="Server"/> row with a blocking lock (WAIT — no SKIP LOCKED).
    /// Used by server cleanup to freeze the set while it decides which servers are stale.
    /// The blocking lock serializes against <c>Heartbeat</c>'s per-row update so we can't
    /// delete a server that just sent a fresh heartbeat.
    /// </summary>
    Task<List<Server>> LockAllServersAsync(DbContext context, CancellationToken ct);

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
        DbContext context,
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
        DbContext context,
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
        DbContext context,
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
        DbContext context,
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
        DbContext context,
        string lockKey,
        Func<DbContext, CancellationToken, Task<T>> work,
        CancellationToken ct);

    /// <summary>
    /// Acquires a row-level write lock on the <see cref="RateLimitBucket"/> row for
    /// <paramref name="key"/> within the caller's open transaction, returning it as a tracked entity
    /// (or <c>null</c> when no row exists yet). Uses <c>FOR NO KEY UPDATE</c> (PG) /
    /// <c>WITH (UPDLOCK, ROWLOCK)</c> (SQL Server) so concurrent adapter rate-limit lease acquisitions
    /// serialise without a TOCTOU window between the SELECT and the caller's INSERT/UPDATE (§1.4). Used
    /// by the shared adapter rate limiter's token-leasing check-and-increment. Caller must have started a
    /// transaction before calling this method.
    /// </summary>
    Task<RateLimitBucket?> LockRateLimitBucketByKeyAsync(DbContext context, string key, CancellationToken ct);

    /// <summary>
    /// Writes a batch of finished jobs in one set-based statement (rule 6.13): the columns finalization
    /// assigns (<c>CurrentState</c>, <c>CancellationMode</c>, <c>CurrentWorkerId</c>,
    /// <c>LastKeepAlive</c>, <c>ExpireAt</c>, <c>HandlerType</c>, <c>ScheduleTime</c>, <c>Metadata</c>),
    /// taken from each <paramref name="jobs"/> entry, and the <paramref name="logs"/> rows of the jobs
    /// that were updated. A job whose row no longer exists is skipped together with its log rows.
    /// Returns the ids of the jobs it wrote, so the caller can tell which were skipped.
    /// <para>
    /// A job is written only while its row is still <c>Processing</c> and still held by the run in
    /// <paramref name="owners"/> (the entry at the same index; see <see cref="JobRunOwner"/>): a run whose job
    /// <c>StaleJobRecovery</c> took back, and another claim then took, is skipped like a deleted row, so it
    /// cannot finish the new run's row.
    /// </para>
    /// <para>
    /// Through the change tracker the same flush is one UPDATE and one INSERT per row. Runs on the
    /// context's connection and joins its current transaction; the caller owns the transaction.
    /// </para>
    /// </summary>
    Task<IReadOnlyCollection<Guid>> CompleteJobsAsync(DbContext context, IReadOnlyList<Job> jobs, IReadOnlyList<JobRunOwner> owners, IReadOnlyList<JobLog> logs, CancellationToken ct);

    /// <summary>
    /// Renews the keep-alive of a worker group's running jobs in one statement (rule 6.14): sets
    /// <c>LastKeepAlive</c> to <paramref name="now"/> and <c>CurrentWorkerId</c> to the run's worker on
    /// each job of <paramref name="runs"/> that is still <c>Processing</c> and still held by that run
    /// (<see cref="JobRunOwner"/>), and returns those whose <c>CancellationMode</c> asks for cancellation.
    /// </summary>
    Task<IReadOnlyCollection<Guid>> RenewRunningJobsAsync(
        DbContext context,
        IReadOnlyList<JobRunOwner> runs,
        DateTime now,
        CancellationToken ct);

    /// <summary>
    /// Whether this provider implements the cluster-wide task lease (rule 6.20). Without it the server
    /// keeps a lock-taking loop per task on every server, as before 8.0.
    /// </summary>
    bool SupportsClusterTaskLeases => false;

    /// <summary>
    /// Claims, in one statement, every task of <paramref name="names"/> that is due (or asked for by a
    /// signal) and whose lease is free: sets its lease to one interval, stamps this server, and moves its
    /// next run one interval on, doubled for each consecutive idle run up to its backoff limit. Returns
    /// every named task with whether this call claimed it and when it is next due, and whether a live
    /// server from before the lease is still running (<paramref name="liveSince"/>: the oldest heartbeat
    /// still counted as alive).
    /// </summary>
    Task<ClusterTaskClaims> ClaimClusterTasksAsync(
        DbContext context,
        IReadOnlyList<string> names,
        Guid serverId,
        DateTime now,
        DateTime liveSince,
        CancellationToken ct)
        => throw new NotSupportedException("This provider does not implement cluster task leases.");

    /// <summary>
    /// A signal asked for <paramref name="name"/> to run now: claims it if its lease is free (returns
    /// true; the caller runs it), otherwise marks a run request on the row, which the next claim honours.
    /// </summary>
    Task<bool> RequestClusterTaskRunAsync(DbContext context, string name, Guid serverId, DateTime now, CancellationToken ct)
        => throw new NotSupportedException("This provider does not implement cluster task leases.");

    /// <summary>Extends this server's lease on <paramref name="name"/> by one interval; false if it no longer holds it.</summary>
    Task<bool> RenewClusterTaskLeaseAsync(DbContext context, string name, Guid serverId, DateTime now, CancellationToken ct)
        => throw new NotSupportedException("This provider does not implement cluster task leases.");

    /// <summary>
    /// Ends this server's run of a task: frees the lease; resets the idle backoff when the run did work;
    /// makes the task due at once when <see cref="ClusterTaskCompletion.Rerun"/> is set or a run request
    /// arrived meanwhile; and records the run when <see cref="ClusterTaskCompletion.Record"/> is set.
    /// </summary>
    Task CompleteClusterTaskAsync(DbContext context, ClusterTaskCompletion completion, CancellationToken ct)
        => throw new NotSupportedException("This provider does not implement cluster task leases.");
}
