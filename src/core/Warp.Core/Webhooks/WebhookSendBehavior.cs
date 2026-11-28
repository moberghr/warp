namespace Warp.Core.Webhooks;

/// <summary>
/// The webhook settings this process resolved from its <c>AddWebhooks</c> calls. Read by Warp's own components (the
/// dispatcher and the server tasks that enqueue executor jobs); set only through <see cref="WarpWebhookOptions"/>.
/// </summary>
public sealed class WebhookSendBehavior
{
    /// <summary>
    /// This process's webhook queue (<see cref="WarpWebhookOptions.Queue"/>); <c>null</c> means <c>DefaultQueue</c>.
    /// </summary>
    public string? Queue { get; internal set; }

    /// <summary>Whether this process's deliveries with a group track their endpoint's health.</summary>
    public bool TrackEndpointHealth { get; internal set; }

    /// <summary>Whether this process's <c>SendAsync</c> deduplicates on event id and URL.</summary>
    public bool DeduplicateByEventId { get; internal set; }

    // The queue a delivery's executor jobs go to: the one stamped on the row, else this process's default. A row
    // written before 8.2 has no queue and takes the default of whichever process enqueues its job.
    internal string ResolveQueue(string? deliveryQueue, WarpConfiguration configuration) => deliveryQueue ?? Queue ?? configuration.DefaultQueue;
}
