using Refit;

namespace Warp.Test.Shared.Shop;

// DTOs exchanged with the external shop providers (payment gateways + shipping carriers).
// Channel is the storefront the order came through (web/mobile/marketplace) — it rides as the adapter
// GROUP so the mock can vary behaviour per channel (e.g. marketplace declines more from fraud checks).
public sealed record ChargeRequest(string Provider, string OrderId, decimal Amount, string Channel);

public sealed record ChargeResult(string PaymentId, string OrderId, decimal Amount, string Status);

public sealed record ShipmentRequest(string OrderId, string Carrier, string Sku, string Channel);

public sealed record ShipmentResult(string TrackingNumber, string OrderId, string Carrier, string Status);

public sealed record RateQuote(string Sku, string Carrier, decimal Price);

/// <summary>
/// The shipping-carrier API contract, described as a Refit interface — each method name becomes the
/// recorded operation (<c>CreateShipment</c>, <c>GetRate</c>). Each carrier is registered as its OWN
/// adapter (ups/fedex/dhl) via a per-carrier marker interface below, because a carrier is a genuinely
/// different dependency (its own health + latency). Calls are tagged with the storefront channel as the
/// adapter group via an ambient <c>WarpAdapterCall.Group(channel)</c> scope at the call site.
/// (Real carriers would each have a distinct base URL; the demo points them all at one mock partner and
///  lets the adapter identity — the interface/name — do the modelling.)
/// </summary>
public interface IShippingApi
{
    [Post("/carrier/shipments")]
    Task<ShipmentResult> CreateShipment([Body] ShipmentRequest request, CancellationToken cancellationToken);

    [Get("/carrier/rates/{sku}")]
    Task<RateQuote> GetRate(string sku, [Query] string carrier, [Query] string channel, CancellationToken cancellationToken);
}

// One marker interface per carrier so each registers as a distinct Refit-backed adapter (a Refit typed
// client binds one interface → one named client; the same interface can't map to three names). Methods
// are inherited from IShippingApi.
public interface IUpsShipping : IShippingApi;

public interface IFedExShipping : IShippingApi;

public interface IDhlShipping : IShippingApi;

/// <summary>Shared identifiers and the vendor/channel/subscriber sets the shop demo rotates through.</summary>
public static class ShopProviders
{
    // Adapter names (cluster-wide identities) — one adapter per external VENDOR. Each is a genuinely
    // different dependency with its own health + rate-limit boundary. Payment vendors use named
    // HttpClient adapters; shipping carriers use the Refit marker interfaces above.
    public static readonly string[] Payment = ["stripe", "paypal", "adyen"];
    public static readonly string[] Carriers = ["ups", "fedex", "dhl"];

    // Adapter GROUP values (the "who/where" axis) — the storefront channel the order came through. Same
    // vendor, sliced by who the call is on behalf of; the mock declines more on marketplace (fraud), so
    // the per-Channel table reads meaningfully. Weighted so web dominates and marketplace is the minority.
    public static readonly string[] Channels = ["web", "web", "web", "mobile", "mobile", "marketplace"];

    // Webhook subscriber endpoints (delivery destinations) → reliable / retry-then-settle / exhaust.
    public const string ReliableSubscriber = "reliable";
    public const string FlakySubscriber = "flaky";
    public const string DownSubscriber = "down";
}
