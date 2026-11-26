namespace Warp.Core.Webhooks;

/// <summary>
/// Host callback for webhook endpoint health (8.1): an endpoint (a delivery group sent with endpoint health
/// tracking) started failing, recovered, or has been failing for <c>WebhookEndpointStillFailingAfter</c>. Warp
/// never disables a subscription itself — the host owns subscriptions and decides. Register with
/// <c>AddWebhooks(w =&gt; w.OnEndpointHealthChanged&lt;T&gt;())</c>. Every member has a default no-op, so implement
/// only what you need. Invoked after the transition commits; an exception is logged at Warning and never propagated.
/// <para>
/// <b>Best effort, like the operational notifications</b> (<c>WebhookEndpointFailing</c>, <c>WebhookEndpointRecovered</c>,
/// <c>WebhookEndpointStillFailing</c> through <c>IWarpNotifier</c>, which fire at the same points): a crash between the
/// commit and the call loses that call. The endpoint's state on the dashboard and in <c>IWebhookQueryService</c> is
/// the record.
/// </para>
/// </summary>
public interface IWebhookEndpointHealthHandler
{
    /// <summary>A delivery to the endpoint exhausted its retries; the endpoint is now <c>Failing</c>.</summary>
    Task OnEndpointFailingAsync(WebhookEndpointHealthChange change, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>A probe or another delivery succeeded; the endpoint is <c>Healthy</c> again and its parked deliveries are being released.</summary>
    Task OnEndpointRecoveredAsync(WebhookEndpointHealthChange change, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The endpoint has been failing for <c>WebhookEndpointStillFailingAfter</c> (30 days by default). Once per failing period.</summary>
    Task OnEndpointStillFailingAsync(WebhookEndpointHealthChange change, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Snapshot of an endpoint health change passed to <see cref="IWebhookEndpointHealthHandler"/>.</summary>
public sealed record WebhookEndpointHealthChange
{
    /// <summary>The delivery group name that identifies the endpoint.</summary>
    public required string GroupName { get; init; }

    /// <summary>When the failing period began (for a recovery: the period that just ended).</summary>
    public DateTime? FailingSince { get; init; }

    /// <summary>When the change happened.</summary>
    public required DateTime OccurredAt { get; init; }
}
