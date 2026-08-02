namespace Warp.Core.Enums;

/// <summary>
/// The kind of operational event Warp reports to host <c>IWarpNotifier</c> sinks. Values start at 1 (§8.11).
/// The taxonomy is fixed up front for stability; not every value is emitted in every release —
/// <see cref="JobDeadLettered"/> is reserved for a later slice (retry-exhaustion, 3.11) and is not emitted yet
/// (a host can switch on it safely; it simply never arrives until wired).
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

    /// <summary>A backlog-depth SLO objective was breached — emitted by the <c>SloEvaluator</c> for <c>BacklogDepth</c> objectives (§8.30).</summary>
    BacklogBreached = 5,

    /// <summary>A resolved error group recurred (regression) — a bug thought fixed came back (§8.29).</summary>
    IssueRegressed = 6,

    /// <summary>An SLO objective's error budget is burning (or exhausted) — emitted by the <c>SloEvaluator</c> on a healthy→breaching edge (§8.30).</summary>
    SloBreached = 7,

    /// <summary>A lossy recording pipeline (adapter/endpoint/client) is dropping records because its channel is saturated — emitted per-process by the <c>DroppedRecordReporter</c>, throttled.</summary>
    RecordsDropped = 8,
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
