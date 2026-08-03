using Warp.Core.Enums;

namespace Warp.Core.Notifiers;

/// <summary>
/// A redaction-safe snapshot of one operational event handed to <see cref="IWarpNotifier"/>. Carries
/// identity and metadata only — <b>never a payload body</b> (job message, webhook body, saga state), per
/// §1.2. The host switches on the concrete subtype for typed detail, or on <see cref="Type"/> for a quick
/// classification.
/// </summary>
public abstract record WarpOperationalEvent
{
    /// <summary>The kind of event — also the discriminator for the concrete subtype.</summary>
    public required WarpEventType Type { get; init; }

    /// <summary>Severity hint so a host can route/filter without inspecting the concrete type.</summary>
    public required WarpEventSeverity Severity { get; init; }

    /// <summary>When the event was raised (UTC).</summary>
    public required DateTime TimestampUtc { get; init; }

    /// <summary>The machine that raised it.</summary>
    public required string MachineName { get; init; }

    /// <summary>The originating application (<c>WarpConfiguration.ApplicationName</c>) when set; else null.</summary>
    public string? Application { get; init; }

    /// <summary>A human-readable, non-PII one-line summary suitable for a chat message subject.</summary>
    public required string Message { get; init; }
}

/// <summary>
/// A webhook delivery exhausted its retry schedule without success. Mirrors the fields of the webhook
/// exhaustion callback snapshot — identity/linkage only, no body/headers/secret.
/// </summary>
public sealed record WebhookDeliveryExhaustedEvent : WarpOperationalEvent
{
    public required Guid DeliveryId { get; init; }

    public required string EventType { get; init; }

    public required string EventId { get; init; }

    public required string Url { get; init; }

    public string? GroupName { get; init; }

    public string? Reference { get; init; }

    public required int AttemptCount { get; init; }
}

/// <summary>
/// A saga was force-completed by an operator. Mirrors the existing <c>ForceComplete</c> audit-log fields.
/// </summary>
public sealed record SagaForceCompletedEvent : WarpOperationalEvent
{
    public required Guid SagaId { get; init; }

    public required string SagaType { get; init; }

    public required string CorrelationKey { get; init; }

    public required int LinkCount { get; init; }
}

/// <summary>
/// A lossy recording pipeline is dropping records because its bounded channel is saturated (§8.19/§8.21/§8.27).
/// Raised per-process by the <c>DroppedRecordReporter</c>, throttled per pipeline, so a saturated recording path
/// is alertable in-box (Slack/email) rather than visible only on the OTel meter. Diagnostics only — no payload.
/// </summary>
public sealed record RecordsDroppedEvent : WarpOperationalEvent
{
    /// <summary>The pipeline that dropped records: <c>adapter</c>, <c>endpoint</c>, or <c>client</c>.</summary>
    public required string Pipeline { get; init; }

    /// <summary>How many records were dropped in the reporting interval that triggered this event.</summary>
    public required long Count { get; init; }
}

/// <summary>
/// An application instance (server or non-server) went away — raised from the stale sweep that reaps a
/// heartbeat-lapsed instance.
/// </summary>
public sealed record InstanceDownEvent : WarpOperationalEvent
{
    public required Guid InstanceId { get; init; }

    public required string ApplicationName { get; init; }

    public DateTime? LastSeenAt { get; init; }

    /// <summary>True if the instance was a worker server; false for a non-server (publisher/dashboard) process.</summary>
    public required bool IsServer { get; init; }
}

/// <summary>
/// A resolved error group recurred — a bug thought fixed came back (§8.29). The one genuinely-actionable issue
/// signal; new issues are surfaced on the dashboard only, not alerted.
/// </summary>
public sealed record IssueRegressedEvent : WarpOperationalEvent
{
    public required string Fingerprint { get; init; }

    public required ErrorSource Source { get; init; }

    /// <summary>The exception type or status label of the regressed group.</summary>
    public required string ExceptionType { get; init; }

    /// <summary>Where it happens — handler / route / operation / file.</summary>
    public required string Culprit { get; init; }
}

/// <summary>
/// An SLO objective's error budget is burning (or exhausted) — raised by the <c>SloEvaluator</c> on a
/// healthy→breaching edge (§8.30). Carries the objective identity and the computed budget/burn so a host
/// notifier can route by severity (slow vs fast burn) and format an actionable message. <see cref="Type"/> is
/// <c>SloBreached</c> for latency/rate/deadline objectives and <c>BacklogBreached</c> for a backlog-depth
/// objective (the previously-reserved value, now wired). Never carries a payload body (§1.2).
/// </summary>
public sealed record SloBreachedEvent : WarpOperationalEvent
{
    /// <summary>The objective's human label.</summary>
    public required string Name { get; init; }

    public required SloKind Kind { get; init; }

    /// <summary>Queue / job-type the objective is scoped to (or <c>*</c> for all).</summary>
    public required string Dimension { get; init; }

    /// <summary>Measured value: a ratio for rate/attainment kinds, observed ms / depth for threshold kinds.</summary>
    public required double Attainment { get; init; }

    /// <summary>The objective target (ratio, ms, or depth).</summary>
    public required double TargetValue { get; init; }

    /// <summary>Fraction of error budget remaining; negative when the objective is being missed.</summary>
    public required double BudgetRemaining { get; init; }

    /// <summary>Burn over the short (recent-hour) window — the fast-burn signal.</summary>
    public required double BurnRateShort { get; init; }

    /// <summary>Burn over the full evaluation window — the slow-burn signal.</summary>
    public required double BurnRateLong { get; init; }

    /// <summary>The objective's rolling window in seconds.</summary>
    public required int WindowSeconds { get; init; }
}
