namespace Warp.Core.Events;

/// <summary>
/// Push-event channels for the in-process signal bus. Producers (workers, the notification
/// listener) call the matching <c>SignalXxx</c> method on <c>ServerTaskSignals&lt;TContext&gt;</c>;
/// consumers (server-task loops, the dashboard broadcaster) subscribe to a channel via
/// its <c>Subscribe</c> method.
/// </summary>
public enum ServerTaskSignal
{
    /// <summary>
    /// A job reached a terminal state — wake consumers that act on finalization (the
    /// orchestrator finalizes parents and activates continuations; the dashboard broadcaster
    /// fans the event out to connected clients).
    /// </summary>
    JobFinalized = 1,

    /// <summary>
    /// A <c>Kind=Message</c> row was enqueued — wake consumers that act on message arrival
    /// (the message router fans it out into per-handler jobs; the dashboard broadcaster fans
    /// the event out to connected clients).
    /// </summary>
    MessageEnqueued = 2,

    /// <summary>
    /// A <c>Kind=Job</c> row was enqueued (or activated from <c>Scheduled</c>) — wake worker
    /// pools so they bypass their exponential-backoff sleep and immediately try to claim. Fires
    /// for any local in-process enqueue and (when DB push is enabled) for cross-server pushes
    /// received via <c>NotificationListenerTask</c>.
    /// </summary>
    JobEnqueued = 3,
}
