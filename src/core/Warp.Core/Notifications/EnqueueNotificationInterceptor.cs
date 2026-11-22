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
/// Captures in <c>SavingChanges</c> (the Added rows are Unchanged afterwards) and announces after
/// the save: this process's workers are woken inline through <see cref="ServerTaskSignals{TContext}"/>,
/// and other processes are told through <see cref="EnqueueAnnouncements"/>, which
/// <see cref="EnqueueNotificationRelay"/> publishes off the saving thread. Inside an explicit
/// transaction both wait for the commit — the cross-process notification goes out on a separate
/// connection and would otherwise wake a worker onto rows it cannot see yet. Rollback, a failed save
/// and a transaction disposed without commit announce nothing.
/// </para>
/// <para>
/// Deliberately holds no transport. The push transport's factory reads TContext's connection, and an
/// interceptor that needed the transport to be built made the two wait on each other at startup.
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
    private readonly EnqueueAnnouncements _announcements;

    public EnqueueNotificationInterceptor(ServerTaskSignals<TContext> signals, EnqueueAnnouncements announcements)
    {
        _signals = signals;
        _announcements = announcements;
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
        Announce(TakeReady(eventData.Context));

        return result;
    }

    public ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Announce(TakeReady(eventData.Context));

        return ValueTask.FromResult(result);
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

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => Announce(TakeDeferred(eventData.Context));

    public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Announce(TakeDeferred(eventData.Context));

        return Task.CompletedTask;
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

        var pending = NotificationDispatch.CapturePending(context);
        state.Captured = pending.Count > 0 ? pending : null;
    }

    // Returns what to announce now, or null. Saves inside an explicit transaction are held back.
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

    // Both halves are synchronous: the local signal releases waiters, the announcement is a channel
    // write. Nothing here can block the save on the transport.
    private void Announce(List<Notification>? notifications)
    {
        if (notifications == null)
        {
            return;
        }

        NotificationDispatch.SignalLocally(notifications, _signals);
        _announcements.Announce(notifications);
    }
}
