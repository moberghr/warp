---
sidebar_position: 5
---

# Integrations

Northwind Goods talks to partners in both directions. It calls PayFast and ShipIt, it notifies
marketplace partners about orders, and PayFast calls it back with payment results. This page covers
the three Warp features for that: outbound adapters, durable webhooks and inbound HTTP endpoints.

| Feature | In the shop | Reference |
|---|---|---|
| [Outbound adapters](#outbound-adapters) | PayFast (HTTP) and ShipIt (Refit), grouped by region, with a shared ShipIt rate limit | [Outbound Adapters](../features/adapters.md) |
| [Durable webhooks](#durable-webhooks) | `order.paid` / `order.shipped` to marketplace partners, signed, with an exhausted handler | [Outbound Webhooks](../features/webhooks.md) |
| [Inbound HTTP endpoints](#inbound-http-endpoints) | The PayFast payment callback | [HTTP Endpoints](../features/http.md) |

`Warp.Adapters.Http` is outbound (calls the shop makes). `Warp.Http` is inbound (calls made to the
shop). The names are close; the directions are opposite.

## Outbound adapters

### In the shop

Payment authorisation and shipment booking run as jobs on `shop-worker`. Each call to PayFast and
ShipIt should be timed, counted and, when it fails, explain itself. Both partners have separate
endpoints per region, so the region is the adapter **group**: a region that is red across every
operation points at that regional endpoint, not at the shop's code. ShipIt allows the shop 100 calls a
minute in total, across all `shop-worker` replicas, so ShipIt gets a cluster-shared rate limit.

### Wiring

```csharp
// ShipIt is called through Refit; operation names come from the method names.
public interface IShipItApi
{
    [Post("/shipments")]
    Task<ShipmentBooked> CreateShipment([Body] CreateShipmentRequest request);

    [Get("/shipments/{trackingNumber}/tracking")]
    Task<TrackingState> GetTracking(string trackingNumber);
}

// shop-worker
builder.Services.AddWarpServer<ShopDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.AddAdapters();   // write AdapterCallLog rows; spans and meters are emitted either way

    opt.AddAdapter("payfast", a =>
    {
        a.BaseUrl = new Uri("https://api.payfast.example");
        a.Recording.GroupLabel = "Region";
        // PCI: CaptureRequestBodies / CaptureResponseBodies / CaptureHeaders stay at the default None.
    });

    opt.AddAdapter<IShipItApi>("shipit", a =>
    {
        a.BaseUrl = new Uri("https://api.shipit.example");
        a.Recording.GroupLabel = "Region";
        a.Recording.CaptureResponseBodies = CaptureMode.OnFailure;
        a.UseSharedRateLimit(limit: 100, perSeconds: 60, AdapterRateLimitOverflow.Wait, maxWait: TimeSpan.FromSeconds(30));
    });
});

// PayFast: a named HttpClient. Operation and group ride on the request.
public sealed class AuthorizePaymentHandler(IHttpClientFactory httpFactory, ShopDbContext db)
    : IJobHandler<AuthorizePayment>
{
    public async Task HandleAsync(AuthorizePayment job, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/authorisations")
            {
                Content = JsonContent.Create(new { job.PaymentToken, job.Amount, job.Currency }),
            }
            .WithWarpOperation("Authorize")
            .WithWarpGroup(job.Region)
            .WithWarpCorrelation(job.OrderReference);

        using var response = await httpFactory.CreateClient("payfast").SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        // Store the PayFast payment id; the result arrives later on the callback endpoint.
    }
}

// ShipIt: the Refit client, with the region as an ambient group.
using (WarpAdapterCall.Group(order.Region))
{
    var booked = await shipIt.CreateShipment(new CreateShipmentRequest(order.Reference, order.Address));
}
```

### Test scenario

- **Given** `shop-worker` runs with the wiring above, and a ShipIt sandbox you control.
- **When** you place orders in two regions and let them ship.
- **Then** `/warp/adapters` lists `payfast` and `shipit` with a health pill, call count, error rate and
  latency. `/warp/adapters/shipit` shows operations `CreateShipment` and `GetTracking`, and a **Region**
  table with one row per region.
- **When** you make the sandbox return `503` for the EU region only and ship more EU and UK orders.
- **Then** the EU row in the **Region** table turns red for both operations while UK stays green. The
  recent-calls list shows the failed calls; the call drawer shows status `503` and the captured response
  body. On `/warp/adapters/payfast` the drawer never shows a body.
- **When**, on a fresh test database, you register ShipIt with `limit: 5, perSeconds: 60`, run two
  `shop-worker` replicas and ship 10 orders.
- **Then** ShipIt sees at most 5 calls per minute in total, not 5 per replica. The extra calls wait, and
  any that wait longer than `maxWait` record a `Throttled` outcome.

Pitfalls:

- Leave capture at `None` for PayFast and never use `ForceCapture` on it. A failed authorisation
  response is where card data is echoed back.
- Do not add a global HTTP retry handler (for example Aspire `ServiceDefaults`). Add resilience per
  adapter with `a.ConfigureHttpClientBuilder(...)`.
- A shared limit is first-writer-wins. Redeploying with a new number does not change it; add a
  `RateLimitOverride` row or clear the persisted policy (see the adapters reference).

Covered by: `HttpAdapterRegistrationTests`, `RefitAdapterTests`, `AdapterGroupTests`,
`AdapterRecorderTestsBase`, `AdapterRateLimitTestsBase`, `RefitRateLimitOverflowTests`.

## Durable webhooks

### In the shop

Marketplace partners that list Northwind Goods products want to know when an order is paid and when
it ships. Each partner registers a callback URL and a signing secret; the shop stores these in its own
`MarketplaceSubscription` table. When `OrderPaid` or `OrderShipped` is published, a handler sends one
webhook per subscription. Warp stores each delivery, signs it with Standard Webhooks, retries on a
schedule, and calls the shop's exhausted handler when a partner stays down, so the shop can flag that
subscription.

### Wiring

```csharp
public sealed class NotifyMarketplacesOnOrderPaid(ShopDbContext db, IWebhookDispatcher webhooks)
    : IMessageHandler<OrderPaid>
{
    public async Task HandleAsync(OrderPaid message, CancellationToken ct)
    {
        var subscriptions = await db.MarketplaceSubscriptions
            .Where(x => x.Events.Contains("order.paid"))
            .Where(x => x.Enabled)
            .ToListAsync(ct);

        foreach (var sub in subscriptions)
        {
            await webhooks.SendAsync(
                new WebhookSend
                {
                    Url = sub.CallbackUrl,
                    EventType = "order.paid",
                    EventId = $"{message.OrderReference}:order.paid",   // webhook-id, same on every retry
                    Payload = JsonSerializer.Serialize(new { message.OrderReference, message.PaidAt }),
                    Group = sub.PartnerName,                            // per-partner stats
                    Reference = sub.Id.ToString(),                      // back to the subscription
                    Signing = WebhookSigning.StandardWebhooks,
                    Secret = sub.SigningSecret,                         // whsec_..., redacted on every read
                    RetrySchedule = sub.RetrySchedule,                  // null = built-in [1m, 10m, 1h, 6h]
                },
                ct);
        }
    }
}

public sealed class FlagMarketplaceSubscription(IServiceScopeFactory scopeFactory)
    : IWebhookDeliveryExhaustedHandler
{
    public async Task OnDeliveryExhaustedAsync(WebhookDeliveryExhausted delivery, CancellationToken ct)
    {
        // At-least-once: setting a flag is idempotent, so a repeat call is harmless.
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
        var sub = await db.MarketplaceSubscriptions.FindAsync([Guid.Parse(delivery.Reference!)], ct);
        if (sub is not null)
        {
            sub.NeedsAttention = true;
            await db.SaveChangesAsync(ct);
        }
    }
}

// shop-worker: exhaustion runs where the delivery executes.
builder.Services.AddWarpServer<ShopDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.AddAdapters();   // optional: fills the per-attempt timeline
    opt.AddWebhooks(w => w.OnDeliveryExhausted<FlagMarketplaceSubscription>());
});
```

`SendAsync` stores the delivery and its first attempt and saves the context, so call it once per
destination. `OrderShipped` gets the same handler with `order.shipped`.

### Test scenario

- **Given** a test subscription whose callback is a receiver you control, with `RetrySchedule` set to
  `[5s, 15s]`, and the receiver verifying the `webhook-signature` header with the subscription secret.
- **When** a test order is paid.
- **Then** `/warp/webhooks` shows an `order.paid` delivery with status `Delivered` and 1 attempt. The
  receiver sees `webhook-id`, `webhook-timestamp` and `webhook-signature` headers and the signature is
  valid. The delivery detail page shows the secret only as `***`.
- **When** you make the receiver return `500` and pay another order.
- **Then** the delivery stays `Pending` with a growing attempt count, then becomes `Exhausted` after 3
  attempts (within about a minute). The attempt timeline shows three `500` rows. The subscription's
  `NeedsAttention` flag is set, and no job appears as `Failed` on the Jobs pages.
- **When** you make the receiver return `200` again and press **Redeliver** on the exhausted delivery.
- **Then** it goes back to `Pending` and then `Delivered`, with the same `webhook-id` as before.
- **When** you change the secret on the receiver side only.
- **Then** the receiver rejects the signature, and each attempt is recorded as a failed attempt.

Pitfalls:

- Webhooks are executed by a Warp worker. `shop-worker` drains the `warp:webhooks` queue
  automatically; if no process runs `AddWarpServer` with a worker, deliveries stay `Pending`.
- Retry timing is a backoff, not a precise timer: each retry fires up to about 10 seconds after its
  delay.
- Do not put an HTTP retry handler on the `warp-webhooks` client. Each inner retry shows up as an
  extra attempt.

Covered by: `WebhookExecutionTestsBase`, `WebhookSigningTests`, `WebhookCommandServiceTestsBase`,
`WebhookDispatcherValidationTestsBase`, `WebhookStuckRecoveryTestsBase`, `WebhookQueueWiringTests`,
`WebhookEndpointTestsBase`.

## Inbound HTTP endpoints

### In the shop

After `shop-worker` asks PayFast to authorise a payment, PayFast posts the result to the shop. The
callback is a Warp HTTP endpoint on `shop-api`: a request handler tagged with `[WarpHttpPost]`. It
checks PayFast's signature, marks the order `Paid` and publishes `OrderPaid` in the same transaction,
then answers quickly. Everything slow happens in the `OrderPaid` handlers on `shop-worker`.

### Wiring

```csharp
public sealed class PayFastCallback : IRequest<IResult>
{
    [FromHeader(Name = "X-PayFast-Signature")]
    public string Signature { get; set; } = string.Empty;

    [FromBody]
    public PayFastResult Body { get; set; } = new(string.Empty, string.Empty, string.Empty);
}

public sealed record PayFastResult(string PaymentId, string OrderReference, string Status);

[AllowAnonymous]   // PayFast has no user; the signature is the authentication
[WarpHttpPost("/payments/payfast/callback")]
public sealed class PayFastCallbackHandler(PayFastSignatureVerifier verifier, ShopDbContext db, IPublisher publisher)
    : IRequestHandler<PayFastCallback, IResult>
{
    public async Task<IResult> HandleAsync(PayFastCallback request, CancellationToken ct)
    {
        if (!verifier.IsValid(request.Body, request.Signature))
        {
            return Results.Unauthorized();
        }

        var order = await db.Orders
            .Where(x => x.Reference == request.Body.OrderReference)
            .SingleOrDefaultAsync(ct);
        if (order is null)
        {
            return Results.NotFound();
        }

        // PayFast may send the same callback again; answer it without publishing twice.
        if (order.Status != OrderStatus.PaymentPending)
        {
            return Results.NoContent();
        }

        order.Status = string.Equals(request.Body.Status, "approved", StringComparison.Ordinal)
            ? OrderStatus.Paid
            : OrderStatus.PaymentFailed;
        if (order.Status == OrderStatus.Paid)
        {
            await publisher.Publish(new OrderPaid { OrderReference = order.Reference, PaidAt = order.UpdatedAt });
        }

        await db.SaveChangesAsync(ct);   // order state and OrderPaid commit together

        return Results.NoContent();
    }
}

// shop-api
builder.Services.AddWarpHttp();
// ... AddWarp<ShopDbContext>(...) as usual ...
app.MapWarpHttp();
```

`IRequest<IResult>` lets the handler choose the status code. A plain `IRequest<T>` always answers `200`
with JSON.

### Test scenario

- **Given** `shop-api` and `shop-worker` are running and an order is in `PaymentPending`.
- **When** you post a callback with a valid signature and `"Status": "approved"`.
- **Then** the response is `204`. The order is `Paid`. `/warp/messages` shows an `OrderPaid` message
  whose child jobs (including `NotifyMarketplacesOnOrderPaid`) complete on `shop-worker`.
- **When** you post the same callback again.
- **Then** the response is `204` and no second `OrderPaid` message appears.
- **When** you post a callback with a wrong signature.
- **Then** the response is `401`, the order does not change and no message is published.
- If `shop-api` also calls `AddEndpointObservability()` (see [Observability](./observability.md)), the
  route `POST /payments/payfast/callback` appears on `/warp/endpoints` with its calls and status codes.

Pitfalls:

- A request type that is an `IJob` or `IMessage` cannot be an HTTP endpoint (`WHTTP001`). The handler
  publishes the message instead.
- If you map the endpoints in a group with `RequireAuthorization()`, keep `[AllowAnonymous]` on this
  handler, or PayFast gets `401` for every callback.

Covered by: `RequestEndpointTests`, `BindingTests`, `AuthCompositionTests`, `SubmitJobPatternTests`,
`FileEndpointTests` (handlers returning `IResult`).
