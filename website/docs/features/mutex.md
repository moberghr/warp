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

`AddConcurrency()` registers the pipeline behavior, the `IConcurrencyLimitManager` admin layer, and the `ConcurrencyLimit` entity (picked up by `WarpModelCustomizer` — run a fresh `dotnet ef migrations add` to apply the schema change).

## Where do I declare the policy? Contract vs handler

`[Mutex]` and `[Semaphore]` (like `[RateLimit]`, `[Timeout(Scope = PerAttempt)]`, `[Retry]` and `[CircuitBreaker]`) can be declared on **either** of two axes:

- **On the contract** — the job or message type. This is the *default* for everything that runs it. On a **message**, every routed child that declares nothing of its own resolves the contract, so **all those handlers contend on the one declared key** — use this when the *event itself* must be processed under a shared constraint.
- **On the handler** — a job or message handler class. The natural home for most concurrency constraints: the handler is the code touching the resource ("this handler talks to a single-connection legacy endpoint"), and the contract — every publisher of it — shouldn't need to know that one consumer has such a constraint. On a message, a handler-declared policy applies to **that handler's children only**.

**Both at once is fine, and the handler wins.** `[Mutex]` and `[Semaphore]` count as one family — they fill the same metadata slot — so a handler `[Semaphore("x", 3)]` overrides a contract `[Mutex("y")]` outright rather than merging with it.

### One resolution, written on the row

The policy is resolved **once**, the first time the job runs, in this order:

```
explicit metadata passed at enqueue (WithMutex / WithSemaphore / WithRateLimit / WithTimeout / WithRetry)
  → the handler class          ([Mutex] on IJobHandler<T> / IMessageHandler<T>)
    → the contract type        ([Mutex] on the IJob / IMessage)
      → global options         (opt.AddRetry / opt.AddTimeout — Retry and Timeout only)
```

This is the same chain for every policy family, and the four rungs are the same four everywhere: what the
caller passed at enqueue, then the handler, then the contract, then the process-wide default. Two families
have fewer rungs, because they have nothing to put there: **concurrency and rate limit have no global
default** (a keyless policy is not a policy), and **the circuit breaker has no enqueue rung** (no
`WithCircuitBreaker` — its threshold describes a shared dependency group, not one job). Each addon page
repeats its own chain in a **Precedence** section.

The winner is then **written into `Job.Metadata`**, and from that moment the row is the authority: later attempts read it and never re-resolve, so a requeued or retried job cannot quietly change policy mid-flight, and a redeploy never reshapes jobs already running. If a job did not do what you expected, open it and read what it says it will follow — that is the whole point of stamping.

Two consequences worth knowing:

- **A job shows its policy once its first attempt finishes.** Nothing is stamped at publish except metadata you passed explicitly (at publish there is no handler to ask), and the stamp is persisted by the attempt's finalizing write — so a `Scheduled`/`Enqueued` row is blank, and a row still inside its handler has resolved its policy but not yet written it down. This is the price of letting the handler win; Warp does not add a database round-trip to the worker's hot path to close it.
- **Global defaults are never stamped.** They are process configuration, read live, and identical for every job; an absent value on the row means "the global default applies", not "no policy".

Still rejected — placements no execution path can honour, so they fail the **build** (and, for handlers the generator cannot see, fail the **job** at runtime rather than the process; there is no startup validation):

- A policy attribute on a **stream** handler or on a handler of a plain in-memory `IRequest<T>` (#242).
- `[Timeout(Scope = Total)]` on a handler — its deadline is wall-clock from enqueue, so it must be stamped before any handler is known.

A handler attribute covers **every** message/job type that handler class handles, and attributes are not inherited by derived handler classes.

Two exemptions to know about: **recurring jobs** honour contract-declared policy (firings bypass the publish pipeline, and resolution happens at execution anyway — see the recurring-jobs page), and **saga handlers are policy-exempt** — the saga proxy serializes on its own per-correlation mutex and manages its own reschedules, so no outer policy (declared or global default) applies to it.

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

:::note[Request or handler — both work]
`[Mutex]` / `[Semaphore]` (like `[Timeout]`, `[RateLimit]`, `[Retry]` and `[CircuitBreaker]`) is read off the **handler class first, then the request/job type**. Put it wherever the constraint actually belongs; if both carry one, the handler wins. See [Where do I declare the policy?](#where-do-i-declare-the-policy-contract-vs-handler).
:::

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

`Wait`-mode requeues are emitted to the global `stats:requeued` counter — the same one Retry uses. If you see `requeued` outpacing `succeeded` for a given handler over time, the slot count is too low; increase the limit (via attribute, extension, or admin override) until the rate equalizes. The [Counters page](/docs/dashboard/health/counters) has the chart.

## Dashboard

Jobs cancelled by Skip-mode appear as `Deleted` with a log entry `Cancelled — '{key}' full ({N} slots)`. Jobs requeued by Wait-mode appear in the audit trail as `Requeued` with a similar message and continue retrying until a slot is free. The concurrency key is visible in the job's metadata section on the detail page.

The [Concurrency limits page](/docs/dashboard/runtime/concurrency-limits) at `/warp/concurrency` lists every admin-managed limit with inline editing, deletion, and creation. The page is hidden from the nav when `opt.AddConcurrency()` is not registered.

## Related

- [Semaphore](./semaphore.md) — short reference for the `[Semaphore]` attribute and `WithSemaphore` extension. Cross-links back here for full details.
- [Concurrency limits page](/docs/dashboard/runtime/concurrency-limits) — dashboard docs for runtime overrides.
