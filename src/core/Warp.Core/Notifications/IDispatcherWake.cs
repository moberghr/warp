namespace Warp.Core.Notifications;

/// <summary>
/// Core seam for "wake local dispatcher(s) on a <see cref="NotificationKind.JobEnqueued"/> push".
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
}
