namespace Warp.Core.Webhooks;

/// <summary>
/// Host callback invoked when a delivery exhausts its retry schedule without success. This is the
/// <c>OnDeliveryExhausted</c> signal from the integration boundary: Warp reports the dead-lettered delivery;
/// the host decides what to do (disable the endpoint, alert, record against its own subscription record).
/// Registered via <c>AddWebhooks(w =&gt; w.OnDeliveryExhausted&lt;T&gt;())</c> or directly in DI. Exceptions
/// thrown here are logged at Warning and never propagate to the executor job — delivery stays
/// <c>Exhausted</c> and the job still completes.
/// <para>
/// <b>Delivery guarantee: at-least-once.</b> The callback fires <em>after</em> the <c>Exhausted</c>
/// transition is committed, once per exhaustion transition on the happy path. If the process crashes
/// between that commit and the callback, the executor job is retried and re-invokes the callback for the
/// same (already <c>Exhausted</c>) delivery — so implement handlers idempotently (e.g. key any side effect
/// on the delivery id). It is never invoked ahead of a persisted <c>Exhausted</c> row, so a rollback can
/// never re-fire it.
/// </para>
/// </summary>
public interface IWebhookDeliveryExhaustedHandler
{
    Task OnDeliveryExhaustedAsync(WebhookDeliveryExhausted delivery, CancellationToken cancellationToken);
}

/// <summary>
/// The redaction-safe snapshot of an exhausted delivery passed to
/// <see cref="IWebhookDeliveryExhaustedHandler"/>. Carries the host's own linkage (<see cref="Reference"/>,
/// <see cref="EventId"/>) so the callback can act against its subscription record; deliberately omits
/// the payload, headers, and signing secret.
/// </summary>
public sealed record WebhookDeliveryExhausted
{
    public required Guid DeliveryId { get; init; }

    public required string EventType { get; init; }

    public required string EventId { get; init; }

    public required string Url { get; init; }

    public string? GroupName { get; init; }

    public string? Reference { get; init; }

    public int AttemptCount { get; init; }
}
