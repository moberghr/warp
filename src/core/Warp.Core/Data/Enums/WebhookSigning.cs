namespace Warp.Core.Enums;

/// <summary>
/// How the executor signs a webhook attempt at send time. <c>None</c> adds no signing headers (the
/// migration path for hosts with existing body-embedded signatures); <c>StandardWebhooks</c> emits the
/// <c>webhook-id</c>/<c>webhook-timestamp</c>/<c>webhook-signature</c> HMAC-SHA256 headers; <c>Custom</c>
/// resolves a registered <c>IWebhookSigner</c>.
/// </summary>
public enum WebhookSigning
{
    None = 1,
    StandardWebhooks = 2,
    Custom = 3,
}
