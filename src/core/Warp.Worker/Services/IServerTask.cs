using Warp.Core.Events;

namespace Warp.Worker.Services;

/// <summary>
/// Contract for a background server task. A task is a plain DI-registered unit of work;
/// <see cref="ServerTaskHost{TContext}"/> drives it: takes the distributed lock (when
/// <see cref="LockKey"/> is set), opens a fresh scope per iteration, calls
/// <see cref="ExecuteAsync"/>, and writes the resulting <c>ServerTask</c> / <c>ServerLog</c>
/// rows.
/// </summary>
/// <remarks>
/// Implementers MUST call <c>SaveChangesAsync</c> before returning from
/// <see cref="ExecuteAsync"/>. The host opens a new scope for bookkeeping, so any tracker
/// state left behind inside the task's own scope is discarded — but the task still has to
/// commit its own work.
/// </remarks>
public interface IServerTask
{
    /// <summary>
    /// Display name shown on the dashboard and used as the <c>ServerTask</c> row key for
    /// this server.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Distributed-lock key, or <c>null</c> if this task may run on every server
    /// independently (e.g. heartbeat).
    /// </summary>
    string? LockKey { get; }

    /// <summary>
    /// Auto-run interval. Returning <c>null</c> disables the auto-run loop for this task;
    /// the host will not schedule it. The task stays resolvable via DI for manual triggers.
    /// </summary>
    TimeSpan? DefaultInterval { get; }

    /// <summary>
    /// Do the work. Return a non-null status message when work was performed (drives the
    /// re-run and log-on-success decisions in the host loop); return <c>null</c> when
    /// there was nothing to do. Must call <c>SaveChangesAsync</c> before returning.
    /// </summary>
    Task<string?> ExecuteAsync(CancellationToken ct);

    /// <summary>
    /// When <c>true</c> (default), the host re-runs the task immediately if the last call
    /// returned non-null. Override to <c>false</c> for tasks that should always wait for
    /// their configured interval.
    /// </summary>
    bool RerunImmediately => true;

    /// <summary>
    /// When <c>true</c> (default), the host writes a <c>ServerLog</c> row on each
    /// successful run. Override to <c>false</c> for high-frequency tasks like heartbeat.
    /// </summary>
    bool LogOnSuccess => true;

    /// <summary>
    /// Push-event channels that should wake this task's loop. Default: none (pure polling).
    /// The host subscribes the loop's <c>Signal</c> method to each declared channel on
    /// <see cref="ServerTaskSignals{TContext}"/> at startup and unsubscribes on shutdown.
    /// </summary>
    IEnumerable<ServerTaskSignal> Signals => [];

    /// <summary>
    /// When <c>true</c> (default), the host runs this task's iteration inside a transaction
    /// that holds a transaction-scoped advisory lock (PG: <c>pg_try_advisory_xact_lock</c>;
    /// MSSQL: <c>sp_getapplock</c> with <c>@LockOwner='Transaction'</c>). The lock is released
    /// automatically when the transaction commits or rolls back — saving two DB round-trips
    /// per iteration vs. the Medallion session-scoped <c>IWarpLockProvider</c> path.
    /// <para>
    /// Override to <c>false</c> only when <see cref="ExecuteAsync"/> needs to commit and
    /// release its work in multiple distinct transactions per iteration (so each commit's
    /// state is visible to other servers before the next), or when the task opens explicit
    /// transactions with <c>BeginTransactionAsync</c>. Both patterns are rare — built-in
    /// Warp server tasks all fit the single-transaction model.
    /// </para>
    /// <para>
    /// <see cref="LockKey"/> must be non-null for this flag to take effect; tasks without a
    /// lock key bypass the lock primitive entirely.
    /// </para>
    /// </summary>
    bool LocksWithTransaction => true;

    /// <summary>
    /// Post-commit hook invoked by the host <b>after</b> the iteration's work has committed — after the
    /// lock transaction commits on the <see cref="LocksWithTransaction"/> path, or after
    /// <see cref="ExecuteAsync"/> returns on the session-lock path. Not called when the lock was not
    /// acquired, nor when <see cref="ExecuteAsync"/> throws (the transaction rolled back).
    /// <para>
    /// The default is a no-op. Override it for work that must observe committed state — e.g. dispatching an
    /// operational notification for a change the iteration just made durable. <see cref="ExecuteAsync"/>
    /// runs inside the lock transaction, so it must <b>not</b> fire such side effects itself (they would be
    /// pre-commit and a rollback could undo the underlying change); buffer them in the task and act here.
    /// </para>
    /// </summary>
    Task OnCommittedAsync(CancellationToken ct) => Task.CompletedTask;
}
