using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Warp.Core.Events;

namespace Warp.Core.Notifications;

/// <summary>
/// Announces jobs committed by ANY save of the application's <typeparamref name="TContext"/>, not
/// only by <c>IPublisher.SaveChangesAsync</c> (#322). The common outbox shape —
/// <c>await publisher.Enqueue(job); await db.SaveChangesAsync();</c> — used to commit the rows
/// without waking anyone, so a worker found them on its next backoff poll: up to
/// <c>MaxPollingInterval</c>, which <c>UseDatabasePush()</c> raises to 5 minutes.
/// <para>
/// Captures in <c>SavingChanges</c> (the Added rows are Unchanged afterwards) and dispatches after
/// the save. Inside an explicit transaction the dispatch waits for the commit: the PostgreSQL
/// NOTIFY goes out on a separate connection, so sending it earlier wakes a worker onto rows it
/// cannot see yet. Rollback, a failed save and a transaction disposed without commit send nothing.
/// </para>
/// <para>
/// Registered on <typeparamref name="TContext"/> only, never on the server context: server tasks
/// buffer their own notifications and dispatch them from <c>OnCommittedAsync</c>.
/// </para>
/// </summary>
internal sealed class EnqueueNotificationInterceptor<TContext> : ISaveChangesInterceptor, IDbTransactionInterceptor
    where TContext : DbContext
{
    private readonly ServerTaskSignals<TContext> _signals;
    private readonly IWarpNotificationTransport _transport;

    public EnqueueNotificationInterceptor(ServerTaskSignals<TContext> signals, IWarpNotificationTransport transport)
    {
        _signals = signals;
        _transport = transport;
    }

    public InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Capture(eventData.Context);

        return result;
    }

    public ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Capture(eventData.Context);

        return ValueTask.FromResult(result);
    }

    public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        var ready = TakeReady(eventData.Context);
        if (ready != null)
        {
            DispatchWithoutWaiting(ready);
        }

        return result;
    }

    public async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        var ready = TakeReady(eventData.Context);
        if (ready != null)
        {
            // The rows are committed; a cancelled caller must not lose the wake-up.
            await NotificationDispatch.DispatchAsync(ready, _signals, _transport, CancellationToken.None);
        }

        return result;
    }

    public void SaveChangesFailed(DbContextErrorEventData eventData) => ClearCaptured(eventData.Context);

    public Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        ClearCaptured(eventData.Context);

        return Task.CompletedTask;
    }

    public void SaveChangesCanceled(DbContextEventData eventData) => ClearCaptured(eventData.Context);

    public Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        ClearCaptured(eventData.Context);

        return Task.CompletedTask;
    }

    // A transaction disposed without Commit or Rollback raises neither event, so anything still
    // deferred belongs to a transaction that never committed. Drop it when the next one starts.
    public DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        ClearDeferred(eventData.Context);

        return result;
    }

    public ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        ClearDeferred(eventData.Context);

        return ValueTask.FromResult(result);
    }

    public DbTransaction TransactionUsed(DbConnection connection, TransactionEventData eventData, DbTransaction result)
    {
        ClearDeferred(eventData.Context);

        return result;
    }

    public ValueTask<DbTransaction> TransactionUsedAsync(DbConnection connection, TransactionEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        ClearDeferred(eventData.Context);

        return ValueTask.FromResult(result);
    }

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        var deferred = TakeDeferred(eventData.Context);
        if (deferred != null)
        {
            DispatchWithoutWaiting(deferred);
        }
    }

    public async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        var deferred = TakeDeferred(eventData.Context);
        if (deferred != null)
        {
            await NotificationDispatch.DispatchAsync(deferred, _signals, _transport, CancellationToken.None);
        }
    }

    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => ClearDeferred(eventData.Context);

    public Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        ClearDeferred(eventData.Context);

        return Task.CompletedTask;
    }

    public void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) => ClearDeferred(eventData.Context);

    public Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        ClearDeferred(eventData.Context);

        return Task.CompletedTask;
    }

    private static void Capture(DbContext? context)
    {
        if (context == null)
        {
            return;
        }

        var state = EnqueueNotificationState.For(context);
        state.Observed = true;

        if (state.Suppressed)
        {
            state.Captured = null;

            return;
        }

        var pending = NotificationDispatch.CapturePending(context);
        state.Captured = pending.Count > 0 ? pending : null;
    }

    // Returns what to send now, or null. Saves inside an explicit transaction are held back.
    private static List<Notification>? TakeReady(DbContext? context)
    {
        if (context == null)
        {
            return null;
        }

        var state = EnqueueNotificationState.For(context);
        var captured = state.Captured;
        state.Captured = null;

        if (captured == null)
        {
            return null;
        }

        if (context.Database.CurrentTransaction != null)
        {
            state.Deferred ??= [];
            state.Deferred.AddRange(captured);

            return null;
        }

        return captured;
    }

    private static List<Notification>? TakeDeferred(DbContext? context)
    {
        if (context == null)
        {
            return null;
        }

        var state = EnqueueNotificationState.For(context);
        var deferred = state.Deferred;
        state.Deferred = null;

        // Several saves in one transaction can capture the same queue; one wake per queue is enough.
        return deferred?.Distinct().ToList();
    }

    private static void ClearCaptured(DbContext? context)
    {
        if (context != null)
        {
            EnqueueNotificationState.For(context).Captured = null;
        }
    }

    private static void ClearDeferred(DbContext? context)
    {
        if (context != null)
        {
            EnqueueNotificationState.For(context).Deferred = null;
        }
    }

    // Synchronous SaveChanges/Commit: the local signals fire inline; the transport publish is not
    // awaited, since blocking on it here risks a sync-over-async deadlock. FireAsync swallows
    // transport failures, so the discarded task cannot fault unobserved.
    private void DispatchWithoutWaiting(List<Notification> notifications)
    {
        _ = NotificationDispatch.DispatchAsync(notifications, _signals, _transport, CancellationToken.None);
    }
}
