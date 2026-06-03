---
sidebar_position: 1
---

# Outbox Pattern

Warp implements the **transactional outbox pattern** — jobs are created inside the same database transaction as your business data. This guarantees that if your `SaveChangesAsync()` succeeds, the jobs are committed too. If it fails, everything rolls back. No orphaned jobs, no lost work.

This is a core design principle of Warp, not an opt-in feature. Every call to `publisher.Enqueue()`, `publisher.Publish()`, or `batchPublisher.StartNew()` writes to the same DbContext your application code uses.

## How It Works

```csharp
public class OrderController : ControllerBase
{
    private readonly IPublisher _publisher;
    private readonly AppDbContext _context;

    public async Task<IActionResult> PlaceOrder(CreateOrderRequest request)
    {
        var order = new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = request.CustomerId,
            Total = request.Total,
        };
        _context.Orders.Add(order);

        // These jobs are written to the same DbContext — not yet committed
        await _publisher.Enqueue(new SendConfirmationEmail { OrderId = order.Id });
        await _publisher.Enqueue(new ReserveInventory { OrderId = order.Id });
        await _publisher.Publish(new OrderPlaced { OrderId = order.Id });

        // Single SaveChangesAsync commits the order AND all jobs atomically
        await _context.SaveChangesAsync();

        return Ok(order.Id);
    }
}
```

If `SaveChangesAsync()` throws (constraint violation, connection failure, etc.), both the order and the jobs are rolled back. There is no window where a job exists without its corresponding business data.

:::warning Database-generated ids
The example assigns `order.Id` in application code (`Guid.NewGuid()`) so its value is known before the job is published. If your entity uses a **database-generated** id (identity column, `serial`), `order.Id` is still `0` when `Enqueue` serializes the message — the job payload will silently capture the wrong value. See [Referencing New Entities in Jobs](./db-generated-ids.md) for the three patterns that avoid this.
:::

## Why This Matters

Without the outbox pattern, distributed systems face two classic failure modes:

1. **Business data saved, job lost** — The order is committed but the background job fails to enqueue (message broker down, network error). The customer never gets a confirmation email.

2. **Job enqueued, business data lost** — The job is dispatched but the database transaction rolls back. The worker processes a job for an order that doesn't exist.

Warp eliminates both by using a single database transaction. There is no separate message broker — the database **is** the queue.

## DbContext Must Be Scoped

For the outbox pattern to work, the publisher and your application code must share the same `DbContext` instance. This is why Warp requires your DbContext to be registered as **Scoped** (the EF Core default):

```csharp
// Correct — Scoped (default)
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

// Wrong — Transient creates a new instance per resolution
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString), ServiceLifetime.Transient);
```

When the publisher calls `Enqueue()`, it writes a `Job` entity to the same `DbContext` your controller/service is using. A single `SaveChangesAsync()` commits everything.

## Multiple Jobs in One Transaction

You can create any number of jobs within a single scope. They all share the same transaction:

```csharp
// All of these are batched into one SaveChangesAsync
await publisher.Enqueue(new SendEmail { To = "user@example.com" });
await publisher.Enqueue(new UpdateAnalytics { Event = "order_placed" });
await publisher.Schedule(new SendFollowUp { OrderId = id }, DateTime.UtcNow.AddDays(3));
await publisher.Publish(new OrderNotification { OrderId = id }); // Message → multiple handlers

await context.SaveChangesAsync(); // Commits all jobs + your business data
```

## With EF Core Transactions

If you use explicit transactions, jobs participate automatically:

```csharp
await using var transaction = await context.Database.BeginTransactionAsync();

context.Orders.Add(order);
await publisher.Enqueue(new ProcessOrder { OrderId = order.Id });

await context.SaveChangesAsync();
await transaction.CommitAsync(); // Jobs become visible to workers here
```

Jobs are only visible to workers after the transaction commits. This prevents workers from picking up jobs for data that hasn't been committed yet.

## No Separate Message Broker

Warp uses the same database for business data and job storage. This means:

- **No infrastructure dependency** — no Redis, RabbitMQ, or Kafka needed
- **Atomic guarantees** — impossible for jobs and data to get out of sync
- **Simpler deployment** — one database connection string, one backup strategy
- **ACID transactions** — full database transaction semantics

The trade-off is throughput at extreme scale. For most applications, the database handles the queue load easily. Warp is optimized for this with row-level locking and efficient polling.
