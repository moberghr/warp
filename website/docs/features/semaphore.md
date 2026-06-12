---
sidebar_position: 4.5
---

# Semaphore

`[Semaphore]` is the limit-greater-than-1 form of Warp's concurrency primitive. It's an alias of `[Mutex]` over the same `IWarpSemaphoreProvider` — the only differences from `[Mutex]` are that you supply a slot count and the default mode is `Wait` (queue surplus) instead of `Skip` (drop surplus).

Use `[Semaphore]` when you want to **cap** N concurrent jobs per key. Use `[Mutex]` when you want **at most one**.

## Setup

Same addon as Mutex — `opt.AddConcurrency()` registers both:

```csharp
builder.Services.AddWarpServer<AppDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.AddConcurrency();
});
```

## Usage

Static slot count via attribute:

```csharp
[Semaphore("payment-api", limit: 5)]
public class CallPaymentApi : IJob { }
```

Or set it dynamically per-enqueue:

```csharp
await publisher.Enqueue(
    new CallPaymentApi(),
    new JobParameters().WithSemaphore("payment-api", limit: 5));
```

## Default mode is `Wait`

`[Semaphore]` defaults to `ConcurrencyMode.Wait` — surplus jobs are requeued (`State = Enqueued`, `ScheduleTime = now`) and re-attempt the slot on the next pickup. This matches the standard semaphore semantic ("queue, don't drop").

Override to `Skip` if you want surplus jobs cancelled instead:

```csharp
[Semaphore("payment-api", limit: 5, Mode = ConcurrencyMode.Skip)]
public class DropOnFull : IJob { }
```

## Related

For full details on modes, the admin-override layer, the `Mutex` vs `Semaphore` namespace split on shared keys, dashboard integration, and edge cases, see [Concurrency control](./mutex.md). That page is the canonical reference for both attributes.
