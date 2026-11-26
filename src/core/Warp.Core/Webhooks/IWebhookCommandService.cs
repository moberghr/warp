namespace Warp.Core.Webhooks;

/// <summary>
/// Write-side webhook operations exposed to the dashboard. Lives in Core (like
/// <see cref="IWebhookQueryService"/>) so dashboard processes resolve it without referencing the executor
/// addon.
/// </summary>
public interface IWebhookCommandService
{
    /// <summary>
    /// Requeues a settled delivery: a <c>Delivered</c> or <c>Exhausted</c> delivery is reset to
    /// <c>Pending</c> (fresh attempt budget, refreshed <c>ExpireAt</c>) and an executor job is enqueued to
    /// carry out the fresh attempt. The transition is atomic, so two concurrent redelivers on one settled
    /// delivery enqueue exactly one job. Redelivering a <c>Pending</c> delivery (which already owns a live
    /// executor job) returns <see cref="WebhookRedeliveryResult.Rejected"/>; an unknown id returns
    /// <see cref="WebhookRedeliveryResult.NotFound"/>. In a process with no
    /// <c>IWebhookRedeliveryEnqueuer</c> (dashboard-only / publisher-only — no worker, and nothing scans
    /// <c>NextAttemptAt</c>), the reset is <b>not</b> applied and the call returns
    /// <see cref="WebhookRedeliveryResult.Unavailable"/> — mutating there would strand the delivery
    /// <c>Pending</c> with no job to run it.
    /// </summary>
    Task<WebhookRedeliveryResult> Redeliver(Guid deliveryId, CancellationToken ct = default);

    /// <summary>
    /// <see cref="Redeliver(Guid, CancellationToken)"/> with options. With
    /// <see cref="WebhookRedeliveryOptions.Secret"/> set, the delivery's stamped signing secret is replaced
    /// in the same atomic settled→<c>Pending</c> flip, so the redelivered attempts are signed with the new
    /// secret — use it after rotating an endpoint's key. Without it the delivery keeps the secret stamped at
    /// send, exactly like the parameterless overload. The secret is validated like
    /// <c>WebhookSend.Secret</c> (length; base64 for <c>StandardWebhooks</c>) and a delivery signed with
    /// <c>WebhookSigning.None</c> rejects one (<see cref="ArgumentException"/>). It is never returned by
    /// any read surface.
    /// <para>
    /// This member has a default implementation so existing implementers keep compiling: it forwards to
    /// <see cref="Redeliver(Guid, CancellationToken)"/> when no secret is given and throws
    /// <see cref="NotSupportedException"/> when one is, rather than silently signing with the old key.
    /// Warp's own implementation supports both.
    /// </para>
    /// </summary>
    Task<WebhookRedeliveryResult> Redeliver(Guid deliveryId, WebhookRedeliveryOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Secret is null)
        {
            return Redeliver(deliveryId, ct);
        }

        throw new NotSupportedException(
            $"{GetType().Name} does not support redelivering with a new secret. Implement "
            + "IWebhookCommandService.Redeliver(Guid, WebhookRedeliveryOptions, CancellationToken).");
    }

    /// <summary>
    /// Cancels a <c>Pending</c> or <c>Parked</c> delivery (8.1): it becomes <c>Cancelled</c> and is never attempted
    /// again unless redelivered. A live executor job finds it no longer <c>Pending</c> and completes without a request;
    /// an attempt already in flight still reaches the receiver, but its outcome is not recorded and no retry follows.
    /// A settled delivery (<c>Delivered</c>, <c>Exhausted</c>, <c>Cancelled</c>) returns
    /// <see cref="WebhookCancelResult.Rejected"/>; an unknown id <see cref="WebhookCancelResult.NotFound"/>.
    /// Default implementation (for implementers written before 8.1): throws <see cref="NotSupportedException"/>.
    /// </summary>
    Task<WebhookCancelResult> Cancel(Guid deliveryId, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not support cancelling deliveries.");

    /// <summary>
    /// Cancels every <c>Pending</c> and <c>Parked</c> delivery of a group (8.1) and returns how many were cancelled.
    /// <paramref name="groupName"/> is the key the endpoint groups view shows: the delivery's group, or its URL when it
    /// was sent without one.
    /// Same per-delivery semantics as <see cref="Cancel(Guid, CancellationToken)"/>. The endpoint's health is left as
    /// it is. Default implementation: throws <see cref="NotSupportedException"/>.
    /// </summary>
    Task<int> CancelGroup(string groupName, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not support cancelling a group.");

    /// <summary>
    /// "Retry now" for a failing endpoint (8.1): probes it at once by re-sending its oldest parked delivery once,
    /// instead of waiting for <c>WebhookEndpointProbeInterval</c>. Success recovers the endpoint and releases the rest
    /// of its parked deliveries; failure parks the delivery again and the next scheduled probe is one interval later.
    /// Default implementation: throws <see cref="NotSupportedException"/>.
    /// </summary>
    Task<WebhookProbeResult> ProbeEndpoint(string groupName, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not support probing endpoints.");
}

/// <summary>Outcome of <see cref="IWebhookCommandService.Cancel(Guid, CancellationToken)"/>. Values start at 1 (§8.11).</summary>
public enum WebhookCancelResult
{
    /// <summary>The delivery was <c>Pending</c> or <c>Parked</c> and is now <c>Cancelled</c>.</summary>
    Cancelled = 1,

    /// <summary>No delivery exists for the given id.</summary>
    NotFound = 2,

    /// <summary>The delivery is already settled (<c>Delivered</c>, <c>Exhausted</c> or <c>Cancelled</c>) — nothing changed.</summary>
    Rejected = 3,
}

/// <summary>Outcome of <see cref="IWebhookCommandService.ProbeEndpoint(string, CancellationToken)"/>. Values start at 1 (§8.11).</summary>
public enum WebhookProbeResult
{
    /// <summary>The endpoint's oldest parked delivery was released for one attempt.</summary>
    Probing = 1,

    /// <summary>No endpoint health is recorded for this group (no tracked delivery to it ever ran out of retries).</summary>
    NotFound = 2,

    /// <summary>The endpoint is healthy; there is nothing to probe.</summary>
    NotFailing = 3,

    /// <summary>The endpoint is failing but has no parked delivery to re-send; it recovers on its next successful delivery.</summary>
    NothingParked = 4,

    /// <summary>No redelivery enqueuer is registered in this process; nothing was changed.</summary>
    Unavailable = 5,
}

/// <summary>
/// Options for <see cref="IWebhookCommandService.Redeliver(Guid, WebhookRedeliveryOptions, CancellationToken)"/>.
/// </summary>
public sealed class WebhookRedeliveryOptions
{
    /// <summary>
    /// The signing secret the redelivered attempts use, replacing the one stamped at send (for example
    /// after a key rotation). <see langword="null"/> keeps the stamped secret.
    /// </summary>
    public string? Secret { get; init; }
}

/// <summary>
/// Outcome of <see cref="IWebhookCommandService.Redeliver(Guid, CancellationToken)"/>. Values start at 1 (§8.11) so the endpoint can
/// map each case to a distinct HTTP status.
/// </summary>
public enum WebhookRedeliveryResult
{
    /// <summary>The settled delivery was reset to <c>Pending</c> and an executor job was enqueued.</summary>
    Enqueued = 1,

    /// <summary>No delivery exists for the given id.</summary>
    NotFound = 2,

    /// <summary>The delivery is already <c>Pending</c> (it owns a live executor job) — nothing changed.</summary>
    Rejected = 3,

    /// <summary>
    /// No redelivery enqueuer is registered in this process (dashboard-only / publisher-only). The delivery
    /// was left untouched; redeliver from a server host that has <c>AddWebhooks()</c> wired.
    /// </summary>
    Unavailable = 4,
}
