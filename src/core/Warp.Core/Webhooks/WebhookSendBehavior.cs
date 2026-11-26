namespace Warp.Core.Webhooks;

// Per-process send-time switches from AddWebhooks (8.1), read by the dispatcher and stamped on each delivery.
internal sealed class WebhookSendBehavior
{
    public bool TrackEndpointHealth { get; set; }

    public bool DeduplicateByEventId { get; set; }
}
