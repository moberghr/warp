using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Warp.Adapters.Http;
using Warp.Adapters.Webhooks;
using Warp.Core;
using Warp.Core.Handlers;
using Warp.Test.Shared.Entities;

namespace Warp.Test.Shared.Shop;

/// <summary>
/// The shop's order-fulfillment job — the demo's centrepiece. One job exercises the outbound adapters
/// and both durable webhooks: charge the card through the order's payment-provider adapter
/// (stripe/paypal/adyen), on success ship it through the order's shipping-carrier Refit adapter
/// (ups/fedex/dhl), and notify the subscriber with <c>order.paid</c> then <c>order.shipped</c> webhooks.
/// Every call is tagged with the storefront <c>Channel</c> as the adapter group. A declined/failed charge
/// marks the order <c>Failed</c> and completes the job cleanly — the recurring
/// <see cref="RetryPendingPaymentsRequest"/> job sweeps those back in later (no failed jobs in the UI).
/// </summary>
public sealed class PlaceOrderRequest : IJob
{
    public Guid OrderId { get; set; }
}

public sealed class PlaceOrderHandler : IJobHandler<PlaceOrderRequest>
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IUpsShipping _ups;
    private readonly IFedExShipping _fedex;
    private readonly IDhlShipping _dhl;
    private readonly IWebhookDispatcher _webhooks;
    private readonly TestContext _context;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PlaceOrderHandler> _logger;

    public PlaceOrderHandler(
        IHttpClientFactory httpClientFactory,
        IUpsShipping ups,
        IFedExShipping fedex,
        IDhlShipping dhl,
        IWebhookDispatcher webhooks,
        TestContext context,
        IConfiguration configuration,
        ILogger<PlaceOrderHandler> logger)
    {
        _httpClientFactory = httpClientFactory;
        _ups = ups;
        _fedex = fedex;
        _dhl = dhl;
        _webhooks = webhooks;
        _context = context;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task HandleAsync(PlaceOrderRequest message, CancellationToken cancellationToken)
    {
        var order = await _context.Orders.FirstOrDefaultAsync(x => x.Id == message.OrderId, cancellationToken);
        if (order is null || string.Equals(order.Status, "Shipped", StringComparison.Ordinal))
        {
            return;
        }

        var orderRef = order.Id.ToString();

        // 1) Charge through the order's payment-provider adapter (named HttpClient). Channel is the group.
        var paid = await ChargeAsync(order, orderRef, cancellationToken);
        if (!paid)
        {
            order.Status = "Failed";
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogWarning("Order {OrderRef} payment failed via {Provider}; left for retry sweep.", orderRef, order.Provider);

            return;
        }

        order.Status = "Paid";
        await _context.SaveChangesAsync(cancellationToken);
        await _webhooks.SendAsync(ShopWebhooks.Build(_configuration, "order.paid", ShopProviders.ReliableSubscriber, orderRef), cancellationToken);

        // 2) Ship through the order's carrier adapter (Refit). The storefront channel rides as the group
        // (ambient scope), so per-Channel stats accrue on the carrier adapter too.
        var carrier = SelectCarrier(order.Carrier);
        ShipmentResult shipment;
        using (WarpAdapterCall.Group(order.Channel))
        {
            shipment = await carrier.CreateShipment(new ShipmentRequest(orderRef, order.Carrier, order.Sku, order.Channel), cancellationToken);
        }

        order.Status = "Shipped";
        order.TrackingNumber = shipment.TrackingNumber;

        var product = await _context.Products.FirstOrDefaultAsync(x => x.Sku == order.Sku, cancellationToken);
        if (product is { Stock: > 0 })
        {
            product.Stock--;
        }

        await _context.SaveChangesAsync(cancellationToken);
        await _webhooks.SendAsync(ShopWebhooks.Build(_configuration, "order.shipped", ShopProviders.ReliableSubscriber, orderRef), cancellationToken);

        _logger.LogInformation("Order {OrderRef} shipped via {Carrier} ({Tracking}).", orderRef, order.Carrier, shipment.TrackingNumber);
    }

    private async Task<bool> ChargeAsync(ShopOrder order, string orderRef, CancellationToken cancellationToken)
    {
        // The named client is the payment-provider adapter (stripe/paypal/adyen); the storefront channel
        // rides as the group.
        var client = _httpClientFactory.CreateClient(order.Provider);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/gateway/charge")
        {
            Content = JsonContent.Create(new ChargeRequest(order.Provider, orderRef, order.Amount, order.Channel)),
        };
        request
            .WithWarpOperation("ChargePayment")
            .WithWarpGroup(order.Channel)
            .WithWarpCorrelation(orderRef);

        using var response = await client.SendAsync(request, cancellationToken);

        return response.IsSuccessStatusCode;
    }

    private IShippingApi SelectCarrier(string carrier)
    {
        return carrier switch
        {
            "fedex" => _fedex,
            "dhl" => _dhl,
            _ => _ups,
        };
    }
}
