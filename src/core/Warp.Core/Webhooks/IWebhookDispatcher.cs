namespace Warp.Core.Webhooks;

/// <summary>
/// Entry point for the webhooks feature: hand a fully-described <see cref="WebhookSend"/> and Warp
/// takes ownership of delivery. <see cref="SendAsync"/> persists a self-contained <c>Pending</c>
/// delivery row and enqueues the executor job on the <c>warp:webhooks</c> queue — both in the caller's
/// transaction (outbox), so the delivery becomes visible atomically with the caller's own writes.
/// Resolved as a scoped service; inject <c>IWebhookDispatcher</c>.
/// </summary>
public interface IWebhookDispatcher
{
    /// <summary>
    /// Persists the delivery and enqueues its first attempt. Returns the new delivery id (the value
    /// used as the adapter <c>CorrelationId</c> that links every attempt's <c>AdapterCallLog</c> row).
    /// </summary>
    Task<Guid> SendAsync(WebhookSend send, CancellationToken cancellationToken = default);
}
