using Microsoft.EntityFrameworkCore;
using Warp.Core.Events;

namespace Warp.Core.Notifications;

/// <summary>
/// The save used by Warp's own outbox call sites (<c>IPublisher</c>, <c>IBatchPublisher</c>,
/// saga store, recurring trigger). When <see cref="EnqueueNotificationInterceptor{TContext}"/> is on
/// the context it owns the wake-up, including holding it until an explicit transaction commits.
/// A context without it (one built outside <c>AddWarp</c>'s registration) falls back to the
/// capture-save-dispatch this code did before the interceptor existed, so no caller loses a wake-up.
/// </summary>
internal static class EnqueueNotifyingSave
{
    public static async Task SaveChangesAsync<TContext>(
        DbContext context,
        ServerTaskSignals<TContext> signals,
        IWarpNotificationTransport transport,
        CancellationToken cancellationToken)
        where TContext : DbContext
    {
        // Captured before the save either way: afterwards the Added rows are Unchanged.
        var pending = NotificationDispatch.CapturePending(context);
        var state = EnqueueNotificationState.For(context);
        state.Observed = false;

        await context.SaveChangesAsync(cancellationToken);

        if (!state.Observed)
        {
            await NotificationDispatch.DispatchAsync(pending, signals, transport, cancellationToken);
        }
    }
}
