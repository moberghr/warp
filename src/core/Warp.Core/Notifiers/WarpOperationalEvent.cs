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
