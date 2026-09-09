using Microsoft.Extensions.Logging;

namespace Warp.Core;

/// <summary>
/// Wraps a distributed-lock handle so that a failure to <b>release</b> the lock is logged instead
/// of thrown. Every DB-backed <see cref="IWarpLockProvider"/> / <see cref="IWarpSemaphoreProvider"/>
/// returns handles wrapped in this type, so the Warp-wide contract is: <b>acquiring a lock can
/// fail, releasing one never throws</b>.
/// </summary>
/// <remarks>
/// <para>
/// Release runs in a <c>finally</c> / <c>await using</c> at every call site, after the guarded work
/// has already committed. A throw there cannot un-commit anything — it can only turn a completed
/// operation into a reported failure (<c>AddOrUpdateRecurringJob</c> taking down host startup) or
/// <em>replace</em> the real exception the guarded body threw (<c>SagaHandlerProxy</c>,
/// <c>ServerTaskLoop</c>), which is strictly worse than the release failure itself.
/// </para>
/// <para>
/// <b>Swallowing does not change what happens to the lock.</b> Whether the release throws or not,
/// nothing further is released — the exception is a report, not a remediation, and there is no
/// recovery action available to the caller. Two outcomes are possible and Warp cannot tell them
/// apart. If the lock session simply died (Neon autosuspend, a dropped connection, a killed
/// backend), Postgres already released every session-scoped advisory lock it held and nothing
/// leaked. If instead a transaction-mode pooler routed the release to a <em>different</em> backend,
/// the lock is still held on the original one, which is back in the pooler's server pool: it stays
/// stranded until that server connection is recycled, and no client can address it to unlock it.
/// A stranded lock is a real availability problem — see the operations docs — but it is caused by
/// the pooler, not by this class, and throwing would not shorten it by one millisecond.
/// </para>
/// <para>
/// The failure is still a real signal and is logged at Warning with the lock name. The usual cause
/// is a connection pooler in <b>transaction mode</b> between Warp and Postgres — PgBouncer, Neon's
/// <c>-pooler</c> endpoint, Supabase's port 6543, Azure's built-in pooler. Session-scoped advisory
/// locks (<c>pg_advisory_lock</c>) do not survive it: acquire and release land on different backend
/// sessions, so the release reports "lock was not held" — and the acquire never provided mutual
/// exclusion in the first place. Warp needs a direct/session-mode connection string.
/// </para>
/// </remarks>
public sealed class SafeReleaseLockHandle : IAsyncDisposable
{
    private const string ReleaseFailedMessage =
        "Failed to release Warp distributed lock '{LockName}'. The guarded work already completed, so this " +
        "is not propagated. A repeated 'lock was not held' here means the lock session was lost while the " +
        "lock was held — most often a connection pooler in transaction mode (PgBouncer, Neon's '-pooler' " +
        "endpoint, Supabase port 6543, Azure's built-in pooler), which silently breaks session-scoped " +
        "advisory locks. Point Warp at the direct/session-mode connection string.";

    private readonly IAsyncDisposable _inner;
    private readonly string _name;
    private readonly ILogger _logger;

    private SafeReleaseLockHandle(IAsyncDisposable inner, string name, ILogger logger)
    {
        _inner = inner;
        _name = name;
        _logger = logger;
    }

    /// <summary>
    /// Wraps <paramref name="handle"/> so its release cannot throw. A <c>null</c> handle (the lock
    /// was not acquired) passes through as <c>null</c>, so callers keep their "acquired?" check.
    /// </summary>
    public static IAsyncDisposable? Wrap(IAsyncDisposable? handle, string name, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(logger);

        return handle == null ? null : new SafeReleaseLockHandle(handle, name, logger);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Deliberately broad: release is best-effort by construction (see the type remarks).
            // Rethrowing would release nothing extra — it would only fail work that already
            // committed, or mask the guarded body's own exception.
            _logger.LogWarning(e, ReleaseFailedMessage, _name);
        }
    }
}
