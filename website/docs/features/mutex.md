---
sidebar_position: 4
---

# Concurrency control: Mutex and Semaphore

Warp ships a single concurrency primitive under `Warp.Core.Concurrency` exposed as two attributes: `[Mutex]` (limit fixed at 1) and `[Semaphore]` (limit > 1). Both go through the same pipeline behavior, the same metadata, and the same admin-override layer. A mutex is a semaphore with one slot — the split exists only to keep intent honest in code.

If a worker picks up a job whose slot is full, the job is either cancelled or requeued depending on the configured `ConcurrencyMode`.

## Guarantees and limits

What concurrency control **does** guarantee:

- **At most N jobs per key processing at any moment**, across all workers and servers (enforced by the distributed semaphore primitive — `IWarpSemaphoreProvider`). For `[Mutex]` that's 1; for `[Semaphore("k", N)]` it's N.
- **Zero overhead** for jobs that don't set a key — the pipeline behavior short-circuits before touching the semaphore provider.

What concurrency control **does not** guarantee:

- **No execution order across jobs sharing a key.** Neither mode preserves submission order. In `Skip` mode the loser is dropped, so order is moot. In `Wait` mode multiple workers race on the requeue write, so the order in which queued jobs eventually run can drift from submission order under contention. For light, bursty traffic the requeue timestamps usually keep things roughly in order, but this is best-effort and **not part of the contract**.
- **No fairness or starvation prevention.** A constantly re-arriving stream of jobs for the same key can starve a long-blocked one indefinitely (whichever job a worker happens to pick wins).

If you need strict FIFO per key, this primitive isn't the right one — that requires fetch-time filtering, which Warp doesn't currently expose.

## Setup

Concurrency control is an opt-in addon. Register it inside the `AddWarpServer` lambda:

```csharp
builder.Services.AddWarpServer<AppDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.AddConcurrency();
});
```

`AddConcurrency()` registers the pipeline behavior, the publish behavior, the `IConcurrencyLimitManager` admin layer, and the `ConcurrencyLimit` entity (picked up by `WarpModelCustomizer` — run a fresh `dotnet ef migrations add` to apply the schema change).

## Usage — Mutex (limit = 1)

Set the key at publish time using the `.WithMutex()` extension:

```csharp
await publisher.Enqueue(
    new ProcessPayment { CustomerId = 123 },
    new JobParameters().WithMutex("payment:123"));
```

Or use the `[Mutex]` attribute for a static key on the job class:

```csharp
[Mutex("payment-processing")]
public class ProcessPayment : IJob
{
    public int CustomerId { get; set; }
}

// Enqueue normally — key comes from the attribute
await publisher.Enqueue(new ProcessPayment { CustomerId = 123 });
```

You can also set the key via typed metadata:

```csharp
await publisher.Enqueue(new ProcessPayment { CustomerId = 123 },
    new JobParameters().Configure<IConcurrencyMetadata>(m =>
    {
        m.ConcurrencyKey = "payment:123";
        m.Limit = 1;
    }));
```

## Limit > 1: semaphore mode

Use `[Semaphore]` (or `WithSemaphore`) when you want **N concurrent slots** for a key — the canonical "rate-limit concurrent calls to an external API to N" pattern.

```csharp
// Up to 5 concurrent calls to the payment API across all workers and servers.
[Semaphore("payment-api", limit: 5)]
public class CallPaymentApi : IJob { }

// Or set it dynamically per-enqueue:
await publisher.Enqueue(
    new CallPaymentApi(),
    new JobParameters().WithSemaphore("payment-api", limit: 5));
```

Default mode for `[Semaphore]` is `Wait` — the unambiguous semaphore semantic ("queue surplus, don't drop"). Default mode for `[Mutex]` is `Skip` — duplicate detection is the most common Mutex use case.

```csharp
// Drop on saturation instead of requeuing
[Semaphore("payment-api", limit: 5, Mode = ConcurrencyMode.Skip)]
public class DropOnFull : IJob { }
```

`[Semaphore]` requires `limit >= 1`. The `limit = 1` case is allowed for symmetry but `[Mutex]` is the more honest expression of intent.

## Modes: Skip vs Wait

`ConcurrencyMode` controls what happens when a job is picked up while the slot is full:

- **`ConcurrencyMode.Skip`** (Mutex default) — the surplus job is short-circuited to `Deleted`. Useful for deduplication patterns where running the same work twice is wasteful or unsafe.
- **`ConcurrencyMode.Wait`** (Semaphore default) — the surplus job is requeued (`State = Enqueued`, `ScheduleTime = now`) and the audit log records a `Requeued` entry. The job will be picked up again on a later fetch and re-attempts the slot. This gives you concurrency capping without losing work.

```csharp
// Wait mode via fluent API
await publisher.Enqueue(
    new HandleTelegramUpdate { UserId = 123 },
    new JobParameters().WithMutex("user:123", ConcurrencyMode.Wait));

// Wait mode via attribute
[Mutex("user-handler", Mode = ConcurrencyMode.Wait)]
public class HandleTelegramUpdate : IJob
{
    public int UserId { get; set; }
}

// Skip mode on a Semaphore
[Semaphore("payment-api", limit: 5, Mode = ConcurrencyMode.Skip)]
public class DropWhenFull : IJob { }
```

## How it works

`ConcurrencyPipelineBehavior` wraps handler execution:

1. **Enqueue** always succeeds — the slot is not checked at publish time.
2. **Worker picks up** the job and marks it as `Processing`.
3. **Pipeline runs**: the behavior resolves the effective limit (admin row > attribute/extension limit > 1) and asks `IWarpSemaphoreProvider.TryAcquireAsync($"warp:concurrency:{key}", limit, TimeSpan.Zero, ct)` for a slot.
4. **If full**: the behavior sets `IJobContext.Outcome` according to the configured `ConcurrencyMode`. `Skip` → `Deleted` with a log entry `Cancelled — '{key}' full ({N} slots)`. `Wait` → `Enqueued` with `ScheduleTime = now` and a log entry `Requeued — '{key}' full ({N} slots)`.
5. **If a slot is free**: the slot is acquired, the handler executes, and the slot is released when the handler completes (or fails).

Internally the semaphore provider uses Medallion.Threading's distributed locks. At `limit = 1` the call passes through to a single named lock — byte-identical to the pre-rename Mutex behavior. At `limit > 1` the provider uses the N-distinct-named-locks trick: it iterates `{key}:0..{key}:{N-1}` (starting at a random offset) and acquires the first free slot.

### Race-condition safety

The distributed semaphore ensures slot exclusivity across all workers and servers. If two workers fetch two jobs with the same key simultaneously and only one slot is free, the first to win the acquire holds it; the second sees the slot as full and falls into Skip / Wait per its mode.

There is one subtle window: at `limit > 1`, the provider scans slots linearly. If a slot frees during the scan **after** the scanner has passed it, the scan returns `null` even though a slot was technically free at one point during the call. `Wait` mode requeues immediately and the next pickup succeeds — eventual liveness is preserved. `Skip` mode drops the job, but `Skip`'s semantics are already "drop on contention" so this is consistent.

### Zero overhead for regular jobs

Jobs without a concurrency key skip the slot check entirely. The behavior reads the metadata, finds no key, and calls the next behavior immediately.

## `[Mutex]` and `[Semaphore]` on the same key — backend-specific behavior

If you put both `[Mutex("k")]` and `[Semaphore("k", N)]` against the same key, the resulting concurrency cap depends on which database backend you're using.

### PostgreSQL: independent caps

PG uses **disjoint lock names** for the two attributes:

- `[Mutex("k")]` acquires the lock `warp:concurrency:k`.
- `[Semaphore("k", 5)]` acquires one of `warp:concurrency:k:0`..`warp:concurrency:k:4`.

Combined concurrency for the same key is `mutex_limit + semaphore_limit` (so 1 + 5 = up to **6** concurrent jobs).

### SQL Server: shared slot pool

SQL Server delegates to `Medallion.Threading`'s `SqlDistributedSemaphore`, which uses lock names `k0`, `k1`, ..., `k{N-1}` *regardless* of `maxCount`:

- `[Mutex("k")]` acquires `k0`.
- `[Semaphore("k", 5)]` acquires one of `k0`..`k4`.

The two attributes **share the slot pool**. Combined concurrency is `max(mutex_limit, semaphore_limit)` — effectively just `semaphore_limit` since Mutex is always 1 (so up to **5** concurrent jobs, including the Mutex one).

### Why the asymmetry

`Medallion.Threading.Postgres` doesn't expose a counted-semaphore primitive (Postgres advisory locks are exclusive-only), so Warp implements the slot trick from scratch on PG. SQL Server reuses Medallion's pre-existing `SqlSemaphore`, which made a different naming choice. Aligning the two would require either reworking the PG fast path (breaks Mutex behavioral parity) or replacing the SQL Server delegation with a custom implementation. Both are deferred.

### Practical guidance

**Pick one or the other for a given key.** Don't put both attributes on the same class — if you do, `[Mutex]` wins by registration order and the `[Semaphore]` is silently ignored. If you set both via different jobs sharing a key, the resulting cap will surprise you on at least one backend.

## Admin overrides

Concurrency limits can be edited at runtime through `IConcurrencyLimitManager`, without redeploying:

```csharp
public class ScalingService(IConcurrencyLimitManager limits)
{
    public Task ScaleUp(string key, int slots) =>
        limits.AddOrUpdateLimit(key, slots);

    public Task ScaleDown(string key) =>
        limits.RemoveLimit(key);
}
```

The runtime limit is resolved on every job pickup with the precedence:

1. **Admin row** in the `ConcurrencyLimit` table (set by `AddOrUpdateLimit`)
2. **Attribute / extension limit** from `[Mutex]`, `[Semaphore]`, `WithMutex`, or `WithSemaphore`
3. **Default** of 1 (mutual exclusion)

Admin rows are sticky across redeploys — they live in your application's database, not in source. Once an operator has set `AddOrUpdateLimit("payment-api", 10)`, a future deploy that ships `[Semaphore("payment-api", 5)]` will still run with 10 slots until someone calls `RemoveLimit("payment-api")` or overwrites it.

`ConcurrencyLimitResolver` is scoped — admin-row lookups are cached for the lifetime of one job execution scope. Cross-job staleness is intentional; admin updates take effect at the next pickup.

## Use cases

**`[Mutex]` (limit = 1, default `Skip`) — deduplication:**
- **Report generation**: don't generate the same report twice simultaneously
- **External API calls**: prevent duplicate calls to an idempotent endpoint
- **Cache refresh**: drop concurrent refresh requests for the same key

**`[Mutex]` with `Wait` — per-key serialization:**
- **Per-user message handling**: process updates from the same user one at a time, while different users run in parallel
- **Per-aggregate state machines**: avoid two writers stomping on the same aggregate row
- **Payment processing**: serialize payments per customer rather than dropping duplicates

**`[Semaphore]` (limit > 1, default `Wait`) — concurrency capping:**
- **External API rate limits**: cap concurrent calls to an upstream that's capped at N concurrent requests
- **Queue length protection**: bound the number of in-flight jobs that share a downstream resource (DB connection pool, scarce file handle, GPU)
- **Downstream protection during incidents**: temporarily throttle a noisy job class via `IConcurrencyLimitManager` while the downstream recovers

## Saturation and observability

`Wait`-mode requeues are emitted to the global `stats:requeued` counter — the same one Retry uses. If you see `requeued` outpacing `succeeded` for a given handler over time, the slot count is too low; increase the limit (via attribute, extension, or admin override) until the rate equalizes. The [Counters page](/docs/ui/counters) has the chart.

## Dashboard

Jobs cancelled by Skip-mode appear as `Deleted` with a log entry `Cancelled — '{key}' full ({N} slots)`. Jobs requeued by Wait-mode appear in the audit trail as `Requeued` with a similar message and continue retrying until a slot is free. The concurrency key is visible in the job's metadata section on the detail page.

The [Concurrency limits page](/docs/ui/concurrency-limits) at `/warp/concurrency` lists every admin-managed limit with inline editing, deletion, and creation. The page is hidden from the nav when `opt.AddConcurrency()` is not registered.

## Related

- [Semaphore](./semaphore.md) — short reference for the `[Semaphore]` attribute and `WithSemaphore` extension. Cross-links back here for full details.
- [Concurrency limits page](/docs/ui/concurrency-limits) — dashboard docs for runtime overrides.
