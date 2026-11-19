---
sidebar_position: 2
---

# Orders and checkout

This page follows one Northwind Goods order from the checkout button to a paid order. It covers how
`shop-api` hands work to `shop-worker`, how the order's state is tracked across PayFast and Stockroom
replies, and how jobs carry context. Each section links to the reference page for the full detail.

| Feature | Reference |
|---|---|
| [Messages](#messages) | [Messages](../patterns/messages.md) |
| [Jobs](#jobs) | [Jobs](../patterns/jobs.md) |
| [Requests and streams](#requests-and-streams) | [Requests](../patterns/requests.md) |
| [Sagas](#sagas) | [Sagas](../features/sagas.md) |
| [Outbox](#outbox) | [Outbox pattern](../features/outbox-pattern.md) |
| [Metadata](#metadata) | [Job metadata](../features/metadata.md) |
| [DB-generated ids](#db-generated-ids) | [Referencing new entities in jobs](../features/db-generated-ids.md) |
| [Cancellation](#cancellation) | [Job cancellation](../features/cancellation.md) |

## Messages

**In the shop.** When checkout succeeds, three things must happen: reserve the items in Stockroom, authorise
the card with PayFast, and send the confirmation email through Mailer. They are independent, and one failing
must not block the others. `shop-api` publishes one `OrderPlaced` message. Warp creates one child job per
handler, so each step runs, fails and retries on its own.

**Wiring.**

```csharp
public class OrderPlaced : IMessage
{
    public Guid OrderId { get; set; }
}

public class ReserveStock : IMessageHandler<OrderPlaced>
{
    public Task HandleAsync(OrderPlaced message, CancellationToken ct) => /* call Stockroom */ Task.CompletedTask;
}

public class AuthorisePayment : IMessageHandler<OrderPlaced>
{
    public Task HandleAsync(OrderPlaced message, CancellationToken ct) => /* call PayFast */ Task.CompletedTask;
}

public class SendConfirmationEmail : IMessageHandler<OrderPlaced>
{
    public Task HandleAsync(OrderPlaced message, CancellationToken ct) => /* call Mailer */ Task.CompletedTask;
}

// shop-api, CheckoutController
await publisher.Publish(new OrderPlaced { OrderId = order.Id });
await publisher.SaveChangesAsync(ct);   // commits, then sends the push and the local wake
```

**Test scenario.**

- *Given* `shop-worker` is running with the three handlers above.
- *When* you place an order on `shop-web`, then make `SendConfirmationEmail` throw (for example, point
  Mailer at a closed port) and place a second order.
- *Then* the dashboard **Messages** pages, one per state (Enqueued, Processing, Completed, Failed), list
  the two `OrderPlaced` messages. Each message's detail page shows a progress line such as `3/3 (100%)` and
  a **Jobs** section headed `Jobs (3)`, one job per handler (`Jobs (4)` once the [saga](#sagas) below is
  registered, because the saga is another handler). The section lists one state at a time: pick a state in
  its sidebar, which shows the count for each. For the first order all three jobs are under `Completed`.
  For the second, `ReserveStock` and `AuthorisePayment` are under `Completed` and `SendConfirmationEmail`
  under `Failed`; the message itself still reaches a terminal state.
- `Covered by:` `MessageIntegrationTests`, `MessagePolicyTests`
  (`HandlerDeclaredRetry_MessageChildRetriesPerChild_SiblingUnaffected`), `MessageRoutingTaskTests`.

Pitfall: the child jobs are created by the `MessageRouter` server task, so a message published from
`shop-api` does nothing until a `shop-worker` is running.

## Jobs

**In the shop.** Some work has exactly one handler. Once an order is paid, `shop-worker` books the ShipIt
shipment and, only after that succeeds, prints the label. Separately, a cart left at checkout gets an
abandoned-cart email 24 hours later. Booking and printing are a continuation; the email is a scheduled job.

The guide shows two designs for the abandoned-cart email. This page schedules one job per cart.
[Scheduling](./scheduling.md#recurring-jobs) shows the alternative: a recurring sweep that finds old carts.
They are alternatives. Pick one; running both sends two emails.

**Wiring.**

```csharp
public class BookShipment : IJob { public Guid OrderId { get; set; } }
public class PrintShippingLabel : IJob { public Guid OrderId { get; set; } }
public class SendAbandonedCartEmail : IJob { public Guid CartId { get; set; } }

public class BookShipmentHandler : IJobHandler<BookShipment>
{
    public Task HandleAsync(BookShipment job, CancellationToken ct) => /* call ShipIt */ Task.CompletedTask;
}

// After payment is confirmed
var bookingId = await publisher.Enqueue(new BookShipment { OrderId = orderId });
await publisher.Enqueue(new PrintShippingLabel { OrderId = orderId }, bookingId); // runs after BookShipment

// When a cart is abandoned
await publisher.Schedule(new SendAbandonedCartEmail { CartId = cartId }, DateTime.UtcNow.AddHours(24));
await publisher.SaveChangesAsync(ct);
```

**Test scenario.**

- *Given* `BookShipmentHandler` waits on a breakpoint (or a ShipIt sandbox call that you hold open).
- *When* you pay for an order so both jobs are enqueued.
- *Then* on the dashboard **Jobs** page `PrintShippingLabel` is in **Awaiting** and `BookShipment` is in
  **Processing**. The `BookShipment` detail page lists `PrintShippingLabel` under **Continuations** in its
  **Flow** card. Release the breakpoint: `BookShipment` moves to `Completed` and `PrintShippingLabel` moves to `Enqueued`, then
  `Completed`. Abandon a cart and the `SendAbandonedCartEmail` job sits in **Scheduled** with its
  `ScheduleTime` 24 hours out.
- `Covered by:` `PublisherTests` (`Enqueue_WithParentId_SetsParentAndAwaitingState`,
  `Schedule_SetsScheduleTime`), `ContinuationIntegrationTests`, `WorkerTests`.

Pitfall: if `BookShipment` ends `Failed`, `PrintShippingLabel` stays `Awaiting`. Requeue the failed parent
from the dashboard to release it.

## Requests and streams

**In the shop.** The order-status page on `shop-web` asks `shop-api` "where is order X?". That needs an
answer now, not a background job, so it is an in-process `IRequest<T>`. The back-office CSV export of a
day's orders can be large, so it is an `IStreamRequest<T>` that yields rows as the response is written.
Neither is stored in the database.

**Wiring.**

```csharp
public class GetOrderStatus : IRequest<OrderStatusDto> { public Guid OrderId { get; set; } }

public class GetOrderStatusHandler(ShopDbContext db) : IRequestHandler<GetOrderStatus, OrderStatusDto>
{
    public async Task<OrderStatusDto> HandleAsync(GetOrderStatus request, CancellationToken ct) =>
        await db.Orders.AsNoTracking()
            .Where(x => x.Id == request.OrderId)
            .Select(x => new OrderStatusDto(x.Id, x.Status))
            .SingleAsync(ct);
}

public class ExportOrders : IStreamRequest<OrderCsvRow> { public DateOnly Day { get; set; } }

public class ExportOrdersHandler(ShopDbContext db) : IStreamRequestHandler<ExportOrders, OrderCsvRow>
{
    public async IAsyncEnumerable<OrderCsvRow> HandleAsync(
        ExportOrders request, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var order in db.Orders.AsNoTracking().Where(x => x.Day == request.Day)
            .AsAsyncEnumerable().WithCancellation(ct))
        {
            yield return new OrderCsvRow(order.Id, order.Total);
        }
    }
}

// shop-api
var status = await mediator.Send(new GetOrderStatus { OrderId = id }, ct);
await foreach (var row in mediator.CreateStream(new ExportOrders { Day = day }, ct)) { /* write CSV line */ }
```

**Test scenario.**

- *Given* `shop-api` is running and has at least one order.
- *When* you open the order-status page for that order, then download the CSV export for today.
- *Then* both return data, and the count of rows in the Warp job table is unchanged before and after.
  Nothing appears on the dashboard **Jobs** or **Messages** pages. Cancel the CSV download halfway and the
  handler's enumeration stops (the request's token is cancelled).
- `Covered by:` `MediatorTests` (`Send_WithRequestHandler_ReturnsResponse`,
  `CreateStream_WithStreamHandler_ReturnsItems`, `CreateStream_WithCancellation_StopsEnumeration`).

Pitfall: requests have no retries and no dashboard row. Policy attributes such as `[Retry]` or `[Timeout]`
on an in-memory request handler fail the build.

## Sagas

**In the shop.** An order can ship only when both the payment is confirmed and the stock is reserved. The
PayFast callback and the Stockroom reply arrive in any order, minutes apart, possibly on different workers.
An `OrderWorkflow` saga keyed on the order id remembers what has arrived. If payment has not arrived 30
minutes after checkout, a timeout cancels the order.

**Wiring.**

```csharp
// shop-worker: runs the saga handlers
builder.Services.AddWarpServer<ShopDbContext>(opt =>
{
    opt.UsePostgreSql();   // the saga lock provider; must come before AddSagas()
    opt.AddSagas();
});
builder.Services.AddSagaHandler<OrderWorkflow>();

// shop-api: hosts the dashboard, so it registers the addon too
builder.Services.AddWarp<ShopDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.AddSagas();        // registers the query service behind the Sagas page
});

public class OrderSaga : Saga<Guid>
{
    public bool PaymentConfirmed { get; set; }
    public bool StockReserved { get; set; }
}

[StartsSaga]
public class OrderPlaced : IMessage { [Correlate] public Guid OrderId { get; set; } }

public class PaymentConfirmed : IMessage { [Correlate] public Guid OrderId { get; set; } }

public class StockReserved : IMessage { [Correlate] public Guid OrderId { get; set; } }

public class UnpaidOrderTimeout : ITimeoutMessage
{
    [Correlate] public Guid OrderId { get; set; }
    public TimeSpan Delay => TimeSpan.FromMinutes(30);
}

public class OrderWorkflow(IPublisher publisher) :
    ISagaHandler<OrderSaga, OrderPlaced>,
    ISagaHandler<OrderSaga, PaymentConfirmed>,
    ISagaHandler<OrderSaga, StockReserved>,
    ISagaHandler<OrderSaga, UnpaidOrderTimeout>
{
    public Task HandleAsync(OrderSaga saga, OrderPlaced m, CancellationToken ct) =>
        publisher.Publish(new UnpaidOrderTimeout { OrderId = m.OrderId });

    public Task HandleAsync(OrderSaga saga, PaymentConfirmed m, CancellationToken ct)
    {
        saga.PaymentConfirmed = true;
        return MaybeShipAsync(saga);
    }

    public Task HandleAsync(OrderSaga saga, StockReserved m, CancellationToken ct)
    {
        saga.StockReserved = true;
        return MaybeShipAsync(saga);
    }

    public async Task HandleAsync(OrderSaga saga, UnpaidOrderTimeout m, CancellationToken ct)
    {
        await publisher.Enqueue(new CancelOrder { OrderId = saga.Key });
        saga.MarkCompleted();
    }

    private async Task MaybeShipAsync(OrderSaga saga)
    {
        if (saga is { PaymentConfirmed: true, StockReserved: true })
        {
            await publisher.Enqueue(new BookShipment { OrderId = saga.Key });
            saga.MarkCompleted();
        }
    }
}
```

`ReserveStock` publishes `StockReserved` when Stockroom confirms. The PayFast callback, an inline HTTP
endpoint on `shop-api` (see [Inbound HTTP endpoints](./integrations.md#inbound-http-endpoints)), publishes
`PaymentConfirmed`.

The dashboard runs in `shop-api`. Its **Sagas** page reads through a service that only `AddSagas()`
registers, so without `AddSagas()` in `shop-api` the page is hidden there, even though `shop-worker` runs
the sagas.

**Test scenario.**

- *Given* `shop-worker` with `AddSagas()` and `OrderWorkflow`, `shop-api` with `AddSagas()`, and `Delay`
  temporarily set to 1 minute.
- *When* you place order A and let PayFast confirm it. Then place order B and never complete the payment.
- *Then* the dashboard **Sagas** page (`/warp/sagas`) shows a live `OrderSaga` for each order while it
  waits; the detail page shows the state JSON (`StockReserved: true`) and an activity log of the jobs that
  touched it. Order A's saga disappears once payment arrives and a `BookShipment` job is enqueued. About a
  minute later order A's `UnpaidOrderTimeout` job ends `Deleted`, not `Failed`, because its saga is gone.
  "About a minute" adds two waits: up to one `ScheduledActivationInterval` (10 s) to activate the timeout
  message, then up to one `MessageRoutingInterval` (10 s, or 30 s with DB push) to route it to the saga. Order B's timeout fires against a live saga:
  a `CancelOrder` job is enqueued and the saga row is removed from `saga_state`.
- `Covered by:` `SagaIntegrationTests` (`ThreeMessageFlow_OutOfOrder_SagaCompletes`,
  `TimeoutMessage_FiresAfterDelay_WhenSagaStillLive`,
  `TimeoutMessage_FiresAfterSagaCompleted_SilentlyDeletesJob`), `SagaHarnessTests`,
  `SagaHandlerProxyTests`.

Pitfalls: do not call `publisher.SaveChangesAsync` inside a saga handler; the saga proxy saves for you.
Policy attributes (`[Retry]`, `[Timeout]`, `[Mutex]` and the rest) have no effect on saga handlers; put them
on the jobs the saga enqueues, such as `BookShipment`.

## Outbox

**In the shop.** The `Order` row and the `OrderPlaced` message must be saved together. If the order is
saved but the message is lost, the customer is never charged and never gets an email. If the message is
saved but the order insert fails, the worker processes an order that does not exist. Warp writes the job
row into the same `ShopDbContext`, so one `SaveChangesAsync` commits both or neither.

**Wiring.**

```csharp
// shop-api, CheckoutController
var order = new Order { Id = Guid.CreateVersion7(), CustomerId = customerId, Total = cart.Total };
db.Orders.Add(order);

await publisher.Publish(new OrderPlaced { OrderId = order.Id });

await publisher.SaveChangesAsync(ct);   // one transaction: the order row and the message row
```

`ShopDbContext` must be registered as scoped (the EF Core default) so the publisher and the controller share
it.

**Test scenario.**

- *Given* `Order.OrderNumber` has a unique index.
- *When* you submit a checkout whose order number collides with an existing order, so `SaveChangesAsync`
  throws.
- *Then* there is no new row in `orders`, and no new `OrderPlaced` row in the Warp job table; the
  **Messages** page shows nothing for that attempt. Submit a valid checkout and both the order and its
  `OrderPlaced` message appear.
- `Covered by:` `PublisherFactoryTests` (`Create_BusinessRowAndJob_OneSaveCommitsBoth`,
  `Create_UnsavedContext_JobNotCommitted`), `UnsavedOutboxWarningTests`.

Pitfall: commit through `publisher.SaveChangesAsync`, not through a different context instance. The
context's own `db.SaveChangesAsync` also commits both rows, but it sends nothing: no DB push notification
and no local wake, so the message router only finds the message on its next poll (`MessageRoutingInterval`,
10 s by default, or 30 s with `UseDatabasePush()`). A publish that is never saved is
lost, and Warp logs a warning when the scope ends with unsaved staged jobs.

## Metadata

**In the shop.** Orders come from the web storefront and from marketplace partners. Support wants to see the
sales channel on every job an order creates, including the jobs created by handlers further down. A publish
pipeline behaviour stamps the channel on every publish in `shop-api`. The jobs the message router creates for
`OrderPlaced` copy the message's metadata. A job that a handler or saga enqueues inherits nothing, so the code
that enqueues it passes the channel on itself.

**Wiring.**

```csharp
// shop-api
public class SalesChannelBehavior<T>(ISalesChannelAccessor channel) : IPublishPipelineBehavior<T>
{
    public async Task PublishAsync(PublishContext<T> context, PublishDelegate next, CancellationToken ct)
    {
        context.Metadata["channel"] = channel.Current;   // "web" or "marketplace"
        await next();
    }
}

builder.Services.AddTransient(typeof(IPublishPipelineBehavior<>), typeof(SalesChannelBehavior<>));

// shop-worker, inside any handler
public class AuthorisePayment(IJobContext job) : IMessageHandler<OrderPlaced>
{
    public Task HandleAsync(OrderPlaced message, CancellationToken ct)
    {
        var channel = job.Metadata["channel"];
        return Task.CompletedTask;
    }
}

// shop-worker, wherever a handler or saga enqueues follow-up work: pass the channel on explicitly
await publisher.Enqueue(
    new BookShipment { OrderId = orderId },
    new JobParameters { Metadata = new() { ["channel"] = channel } });
```

**Test scenario.**

- *Given* `SalesChannelBehavior` is registered in `shop-api`.
- *When* you place one order on `shop-web` and import one marketplace order.
- *Then* on the dashboard the `OrderPlaced` job detail shows a metadata section with `"channel": "web"`
  (or `"marketplace"`). The routed `ReserveStock`, `AuthorisePayment` and `SendConfirmationEmail` jobs carry
  the same value. A `BookShipment` job carries it only because the code that enqueued it passed it in
  `JobParameters.Metadata`; drop that and the job's metadata has no `channel`.
- `Covered by:` `MetadataIntegrationTests` (`PublishMessage_MetadataInheritedByRoutedChildJobs`),
  `MetadataPublishPipelineTests`, `MetadataPropagationIntegrationTests`
  (`GivenChildJobSpawnedByHandler_WhenCompleted_ThenChildDoesNotInheritParentMetadata`),
  `MetadataInheritanceTests`.

Pitfall: metadata is shown on the dashboard and stored on the job row. Do not put customer emails or other
personal data in it.

## DB-generated ids

**In the shop.** New orders use client-assigned `Guid` ids, so the id is known before `Publish` serializes
the message. The older `Customer` table uses an identity column. When a guest creates an account at
checkout, `SendWelcomeEmail { CustomerId = customer.Id }` would capture `0`, because the id is only filled in
by `SaveChangesAsync`. The shop saves the customer first inside one transaction.

**Wiring.**

```csharp
await using var tx = await db.Database.BeginTransactionAsync(ct);

var customer = new Customer { DisplayName = request.DisplayName };
db.Customers.Add(customer);
await db.SaveChangesAsync(ct);                       // customer.Id is set here

await publisher.Enqueue(new SendWelcomeEmail { CustomerId = customer.Id });
await publisher.SaveChangesAsync(ct);

await tx.CommitAsync(ct);                            // workers see the job only after this
```

**Test scenario.**

- *Given* the code above, and a deliberately broken copy that calls `Enqueue` before the first
  `SaveChangesAsync`.
- *When* you register a new account at checkout with each version.
- *Then* with the correct version the `SendWelcomeEmail` job's **Payload** on the job detail page shows the
  real `CustomerId`, matching the new row in `customers`. With the broken version the payload shows
  `"CustomerId": 0` and the handler fails with "customer not found".
- `Covered by:` no dedicated automated test.

Pitfall: this compiles and runs without any error. Prefer client-assigned `Guid` ids or EF Core `UseHiLo()`
for new tables so the id exists before the publish.

## Cancellation

**In the shop.** A merchandiser starts a `GenerateSalesReport` job for the whole year by mistake. It will
run for twenty minutes. Cancelling it from the dashboard must stop the handler cleanly, not leave a
half-written report. Job cancellation is cooperative: the handler's `CancellationToken` is cancelled and the
handler must honour it. This is separate from cancelling a customer's order, which is ordinary business
logic such as the saga's `CancelOrder` job.

**Wiring.**

```csharp
public class GenerateSalesReportHandler : IJobHandler<GenerateSalesReport>
{
    public async Task HandleAsync(GenerateSalesReport job, CancellationToken ct)
    {
        foreach (var day in job.Days)
        {
            ct.ThrowIfCancellationRequested();
            await AppendDayAsync(day, ct);
        }
    }
}

// From code (the dashboard Cancel button calls the same service)
await jobCommandService.DeleteJob(jobId);
```

**Test scenario.**

- *Given* a `GenerateSalesReport` job is `Processing`.
- *When* you click **Cancel** on its job detail page. A `Processing` job shows **Cancel**, not **Delete**.
- *Then* the job keeps the `Processing` state with an orange **Cancelling...** badge. Within
  `CancellationCheckInterval` (5 s by default) the handler stops, and the job moves to `Deleted` with a
  `Cancelled` entry in its history. Repeat with a handler that ignores `ct`: the job runs to the end and
  ends `Completed`.
- `Covered by:` `CancellationIntegrationTests`, `CancellationModeTests`, `JobCommandServiceTests`
  (`CancelBatch_CancelsNonTerminalChildren_LeavesTerminalUntouched` for batches).

Pitfall: a handler that never checks `ct` cannot be cancelled. Pass `ct` to every database and HTTP call.
