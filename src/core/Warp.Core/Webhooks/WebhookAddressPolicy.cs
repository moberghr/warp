namespace Warp.Core.Webhooks;

// Whether this process's warp-webhooks client connects only to public addresses (and SendAsync rejects non-public
// IP-literal and localhost URLs). On by default since 8.1; only an explicit AddWebhooks(w => w.PublicAddressesOnly =
// false) turns it off.
internal sealed class WebhookAddressPolicy
{
    public bool PublicAddressesOnly { get; set; } = true;
}
