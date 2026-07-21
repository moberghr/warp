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
}

/// <summary>
/// Outcome of <see cref="IWebhookCommandService.Redeliver"/>. Values start at 1 (§8.11) so the endpoint can
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
