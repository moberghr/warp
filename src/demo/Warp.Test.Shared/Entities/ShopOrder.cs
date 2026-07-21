namespace Warp.Test.Shared.Entities;

/// <summary>
/// A customer order as it moves through the fulfillment flow: <c>Pending</c> → <c>Paid</c> (payment
/// gateway charged) → <c>Shipped</c> (carrier label created), or <c>Failed</c> if payment exhausts its
/// retries. Provider/Carrier select which vendor adapter handles the call; Channel (the storefront the
/// order came through) is the outbound-adapter group the order's calls are tagged with.
/// </summary>
public class ShopOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Sku { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public string Carrier { get; set; } = string.Empty;

    public string Channel { get; set; } = "web";

    public decimal Amount { get; set; }

    public string Status { get; set; } = "Pending";

    public string? TrackingNumber { get; set; }

    public DateTime CreatedAt { get; set; }
}
