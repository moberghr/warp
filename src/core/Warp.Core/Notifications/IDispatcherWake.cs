namespace Warp.Core.Notifications;

/// <summary>
/// Core seam for "wake the local dispatcher(s) that drain the queue of a <see cref="NotificationKind.JobEnqueued"/> push".
/// <see cref="NotificationListenerTask{TContext}"/> (Warp.Core) injects
/// <c>IEnumerable&lt;IDispatcherWake&gt;</c> — <b>empty</b> in a non-server (<c>AddWarp</c>-only)
/// process, where there are no dispatchers to wake; <b>one</b> entry in a server process, where
/// <c>Warp.Worker</c> registers its <c>DispatcherRegistry</c> as the implementation. This mirrors
/// the existing <see cref="IWarpServerPresence"/> optional-marker pattern and keeps the listener in
/// Core decoupled from the worker package (no <c>InternalsVisibleTo</c>, §0.5) while preserving
/// dispatcher-mode wake-up exactly as before in a server process.
/// </summary>
public interface IDispatcherWake
{
    /// <summary>
    /// Wakes every locally-registered dispatcher so a cross-process <c>JobEnqueued</c> push
    /// shortcuts the current exponential-backoff sleep.
    /// </summary>
    void SignalAll();

    /// <summary>
    /// Wakes the locally-registered dispatchers that drain <paramref name="queue"/> (exact, ordinal match).
    /// A null <paramref name="queue"/> wakes every dispatcher. The default implementation wakes every
    /// dispatcher, so an implementation that does not know its dispatchers' queues stays correct.
    /// </summary>
    void Signal(string? queue) => SignalAll();
}
