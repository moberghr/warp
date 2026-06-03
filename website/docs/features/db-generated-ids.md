---
sidebar_position: 1.5
---

# Referencing New Entities in Jobs

A common pattern is to insert a business entity and enqueue a job that carries the new entity's id in its payload — for example, persist an `Order` and enqueue `SendConfirmationEmail { OrderId = order.Id }` in the same unit of work.

If `Order.Id` is **database-generated** (identity column, `serial`, `IDENTITY(1,1)`), the naive code silently captures the wrong value:

```csharp
var order = new Order { CustomerId = 5 };
_context.Orders.Add(order);                                   // order.Id is still 0

await publisher.Enqueue(new SendConfirmationEmail
{
    OrderId = order.Id,                                       // captures 0
});

await context.SaveChangesAsync();                             // order.Id becomes 42 now
                                                              // — too late, the job's
                                                              //   payload already has 0
```

Why this happens: `publisher.Enqueue` serializes the message to JSON and writes a `Job` row into the change tracker **immediately**. The serializer captures whatever value `order.Id` holds at that moment — `default(int)` if EF hasn't populated it yet. `SaveChangesAsync` later fills in `order.Id` from the database, but the `Job.Message` column is already frozen with the stale value.

EF Core solves the equivalent problem for relational foreign keys via navigation-property fixup (`new OrderLine { Order = order }` works without an explicit id). That mechanism doesn't apply here — `Job.Message` is an opaque JSON blob, not an FK Warp can rewrite at save time.

Pick one of the three patterns below.

## Option 1 — Client-assigned `Guid` ids (recommended for new schemas)

Assign the id in application code before `Add`. The id is real the moment the entity exists.

```csharp
public class Order
{
    public Guid Id { get; set; }
    public int CustomerId { get; set; }
    // ...
}

var order = new Order { Id = Guid.NewGuid(), CustomerId = 5 };
_context.Orders.Add(order);

await publisher.Enqueue(new SendConfirmationEmail { OrderId = order.Id });
await context.SaveChangesAsync();                              // one save, correct id
```

Pros: simplest mental model, no DB infrastructure beyond a `uuid` / `uniqueidentifier` column, works on every provider.

Cons: 16-byte keys vs 4/8 bytes for integers; not sortable in a human-friendly way (use `Guid v7` if insertion order matters).

## Option 2 — EF Core Hi/Lo sequences (recommended for integer PKs)

`UseHiLo()` reserves a block of ids from a database sequence in advance. EF hands them out client-side, so `entity.Id` is populated as soon as the entity is tracked.

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.Entity<Order>().Property(x => x.Id).UseHiLo();
}

var order = new Order { CustomerId = 5 };
_context.Orders.Add(order);                                   // order.Id is real (e.g. 1003)

await publisher.Enqueue(new SendConfirmationEmail { OrderId = order.Id });
await context.SaveChangesAsync();
```

Supported by both Npgsql and `Microsoft.EntityFrameworkCore.SqlServer`.

Pros: keeps integer keys, one save.

Cons: needs a sequence object in the schema; ids are not strictly monotonic across processes — each app instance burns a block on startup, so you see gaps after restarts. Block size is tunable (default 10).

## Option 3 — Two `SaveChanges` calls in one transaction

If you can't change the key strategy, open an explicit transaction, save the entity to populate its id, then publish.

```csharp
await using var tx = await context.Database.BeginTransactionAsync();

var order = new Order { CustomerId = 5 };
_context.Orders.Add(order);
await context.SaveChangesAsync();                              // order.Id populated here

await publisher.Enqueue(new SendConfirmationEmail { OrderId = order.Id });
await publisher.SaveChangesAsync();

await tx.CommitAsync();
```

The job is still atomic with the entity — workers cannot see the `Job` row until the transaction commits, so the [outbox guarantee](./outbox-pattern.md) holds.

Pros: works with any existing schema, no model changes.

Cons: an extra round-trip; more boilerplate at every call site that enqueues against a new entity.

## Comparison

| Approach            | Key change        | Round-trips | Notes                                 |
| ------------------- | ----------------- | ----------- | ------------------------------------- |
| Client-assigned Guid | New `Guid` PK     | 1           | Simplest; sortable with `Guid v7`     |
| Hi/Lo               | Add sequence      | 1           | Keeps `int`/`long` PKs; gaps on restart |
| Two saves + txn     | None              | 2           | Works on legacy schemas               |

All three preserve Warp's outbox guarantee: if anything fails, both the business data and the job are rolled back together.

## What does **not** work

```csharp
// ❌ identity column — order.Id is 0 here
_context.Orders.Add(order);
await publisher.Enqueue(new SendConfirmationEmail { OrderId = order.Id });
await context.SaveChangesAsync();
```

```csharp
// ❌ same shape with Publish — the Message JSON is serialized at publish time
_context.Orders.Add(order);
await publisher.Publish(new OrderPlaced { OrderId = order.Id });
await context.SaveChangesAsync();
```

Both compile and run without errors. The job executes against `OrderId = 0`, which either silently no-ops or fails inside the handler with a "not found" error well after the original request returned.
