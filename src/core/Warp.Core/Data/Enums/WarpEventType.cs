namespace Warp.Core.Enums;

/// <summary>
/// The kind of operational event Warp reports to host <c>IWarpNotifier</c> sinks. Values start at 1 (§8.11).
/// The taxonomy is fixed up front for stability; not every value is emitted in every release —
/// <see cref="JobDeadLettered"/> and <see cref="BacklogBreached"/> are reserved for later slices and are not
/// emitted yet (a host can switch on them safely; they simply never arrive until wired).
/// </summary>
public enum WarpEventType
{
    /// <summary>A job permanently failed (exhausted retries / non-retryable) — RESERVED, not emitted yet.</summary>
    JobDeadLettered = 1,

    /// <summary>A webhook delivery exhausted its retry schedule without success.</summary>
    WebhookDeliveryExhausted = 2,

    /// <summary>A saga was force-completed by an operator (dead-lettered).</summary>
    SagaForceCompleted = 3,

    /// <summary>An application instance (server or non-server) went away — sourced from the stale sweep.</summary>
    InstanceDown = 4,

    /// <summary>A queue backlog / wait-time threshold was breached — RESERVED, not emitted yet.</summary>
    BacklogBreached = 5,
}

/// <summary>
/// Severity hint carried on every <c>WarpOperationalEvent</c> so a host notifier can filter or route by
/// urgency without inspecting the concrete event type. Values start at 1 (§8.11).
/// </summary>
public enum WarpEventSeverity
{
    Info = 1,
    Warning = 2,
    Error = 3,
}
