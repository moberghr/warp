---
sidebar_position: 5.7
---

# Rate Limit

Throttle jobs sharing a key to N starts per window. When the bucket is full, the surplus is either dropped (`Skip`) or rescheduled (`Wait`). Style picks between a wall-clock floor (`Fixed`), a rolling tail (`Sliding`), and a steady-rate `TokenBucket` that paces instead of caps.

Opt-in addon — register with `opt.AddRateLimit()` on the builder.

## Quick start

```csharp
builder.Services.AddWarpServer<AppDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.AddConcurrency();   // optional — register BEFORE AddRateLimit if both apply
    opt.AddRateLimit();
});

// On the job/request type — never the handler (default: Mode = Skip, Style = Fixed)
[RateLimit("sendgrid", count: 10, perSeconds: 60)]
public class SendEmail : IJob { }

// Per-publish extension (wins over the attribute)
await publisher.Enqueue(
    new SendEmail(),
    new JobParameters().WithRateLimit("sendgrid", 10, TimeSpan.FromSeconds(60)));
```

## Modes

`RateLimitMode` controls what happens when the bucket is full:

| Mode | Outcome | Use when |
|---|---|---|
| `Skip` (default) | Job ends `Deleted` with a `RateLimited` log entry. | "Drop the duplicate" — telemetry pings, opportunistic refreshes. |
| `Wait` | Job is rescheduled via `JobOutcome.RescheduledState` for the next available window slot. Lock contention adds 100–500 ms of jitter. | "Don't drop — defer" — customer-visible work that must eventually run. |

```csharp
[RateLimit("crm-sync", count: 100, perSeconds: 60, Mode = RateLimitMode.Wait)]
public class SyncCrm : IJob { }
```

## Styles

`RateLimitStyle` controls window shape:

| Style | Behaviour | Storage |
|---|---|---|
| `Fixed` (default) | Wall-clock window floor-aligned to global UTC ticks. Bucket resets at the boundary. Cheap, predictable boundary bursts (up to `2 × count` across two adjacent windows). | One row per `(key, windowStart)`. |
| `Sliding` | Rolling window over the last N start timestamps within `perSeconds`. Defensively trimmed each check. Smoother distribution; no boundary burst. | Slightly more churn — one row per `(key, start)` within the window. |
| `TokenBucket` | **Paces** rather than caps: tokens refill continuously at `count / perSeconds` per second up to a burst capacity of `count`; each start consumes one. A fresh key starts full. When empty, a `Wait`-mode start is rescheduled to the moment the next token refills — so a backlog trickles out at the steady refill rate instead of releasing a full window at once. | One row per key holding the fractional token count + last-refill instant (written only on accept). |

```csharp
[RateLimit("partner-api", count: 5, perSeconds: 1, Style = RateLimitStyle.Sliding)]
public class CallPartnerApi : IJob { }

// Steady pacing: ~4 requests/sec (240/min), bursts of up to 240 smoothed to the refill rate.
[RateLimit("metered-api", count: 240, perSeconds: 60, Style = RateLimitStyle.TokenBucket, Mode = RateLimitMode.Wait)]
public class CallMeteredApi : IJob { }
```

Use `Fixed`/`Sliding` to enforce a **ceiling** (reject/defer once the count is hit); use `TokenBucket` to enforce a **steady rate** against a downstream that wants smooth traffic. `TokenBucket` reschedules `Wait`-mode starts to sub-window token-refill instants, so — like the other styles — those reschedules ride `ScheduledJobActivation` (§ DB push does not accelerate `Wait`).

`perSeconds` is capped at 7 days (`RateLimitAttribute.MaxWindowSeconds`). Inputs past the cap throw at construction, and so does `WithRateLimit(...)` with a longer window (since 8.1).

## Precedence

The **key** — which bucket a job contends on — is resolved from the declaration rungs, most specific first:

```
explicit metadata at publish (WithRateLimit / IRateLimitMetadata)
  → the handler class ([RateLimit])
    → the contract type ([RateLimit])
```

Resolution happens at the job's **first execution**, and the winner is written onto the row (see
[Contract or handler?](#contract-or-handler) below) — not at publish, which is where it happened before 6.0.

An **admin override** (`IRateLimitManager`) sits above all of them, but only for the *size* of the
bucket: it replaces `count` and `perSeconds` for a key on every check, and never changes which key a job
uses. Overrides are read on each acquire attempt with no caching at the limit boundary, so raising or
lowering N takes effect on the next one — including for jobs whose row was stamped long ago.

## Contract or handler?

`[RateLimit]` can sit on the job/message type (the default; on a message every child that declares nothing resolves it, so those handlers share the budget), on a job/message handler class (that handler's children only — the natural home when the *handler* is what calls the throttled dependency), or on both, in which case the handler wins. The resolved limit is written onto the job row at its first execution, and recurring-job firings honour a contract-declared limit. See [Where do I declare the policy?](./mutex.md#where-do-i-declare-the-policy-contract-vs-handler).

## What the pipeline holds

The distributed lock is held only for the brief **check-and-increment** — never during handler execution (unlike `[Mutex]` / `[Semaphore]`, where the lock spans the whole handler). That keeps rate limits friendly to long-running jobs: a single 10-minute job doesn't block other tokens for the duration of its run.

Live state lives in `RateLimitBucket`, one row per key. `ExpirationCleanup` deletes a key's row once it has
been idle for 8 days (the 7-day maximum window plus a day of margin) and holds no Wait-mode cursor still ahead.
By then every style treats the key exactly as one it has never seen (a fixed window has rolled over, sliding
timestamps have aged out, a token bucket has refilled), so keys taken from tenants or users do not leave a row
behind forever. The sweep is batched and capped per tick like Warp's other cleanup. Adapter buckets
(`warp:adapter:{name}`, one per adapter) are not swept.

## Refusing a known-exhausted key from memory

Each check that refuses a job also computes the instant the key can next admit. Every server remembers that instant for the key. Until then, later jobs on the key are refused from memory: no lock, no override lookup, no bucket read. A hot key that is over its limit then costs one database check per process every few seconds, not one per job.

This is safe across servers. Other processes can only *use* capacity, never give it back, so the reopening instant is a lower bound everywhere: before it, the database check would refuse too. The cache never refuses a job the database would have admitted.

The one thing that can reopen a key early is an admin raising or removing its [override](#admin-overrides). The server that makes the change applies it at once. Other servers trust a remembered refusal for at most **5 seconds**, so they pick the change up within that time.

## How `Wait` spreads a backlog

Every job a full key refuses in `Wait` mode used to be rescheduled to the **same** instant: the moment the key reopened. The whole backlog woke together, `count` of it got in, and the rest ran again only to be refused again, at every window. A backlog of B jobs at C per window ran about B²/2C times before it drained.

Now each throttled job gets its **own slot**, handed out from one cursor on the bucket row (`RateLimitBucket.NextSlotUtc`). The cursor is shared by the whole cluster, so two servers never hand out the same slot. Each throttled job therefore comes back about when there is capacity for it, and runs about once.

How slots map to times depends on the style:

| Style | Throttled jobs come back… |
|---|---|
| `Fixed` | `count` at a time, at the **start of each following window**. A Fixed window admits its whole count at once, so this drains a backlog as fast as the limit allows. |
| `Sliding`, `TokenBucket` | One every `perSeconds / count`, since their capacity returns gradually. |

- **Leased blocks.** A server reserves a few slots at a time (up to 20, never more than about 5 seconds of the key's capacity), so most throttled jobs on a hot key are placed without touching the database. Slots a server reserved but did not use delay the next jobs by at most that much.
- **Not strict FIFO.** A newly published job that finds spare capacity runs at once, ahead of jobs already waiting for a slot. A waiting job that then finds its window full is refused again and takes a new slot.
- **The cursor marks a queue.** If you delete a large backlog of throttled jobs, the cursor stays ahead until that time has passed. Until then, jobs that find the key full queue behind it. Jobs that find capacity are not affected.
- **Windows shorter than the activation interval.** Throttled jobs come back when `ScheduledJobActivation` runs (see [below](#db-push-does-not-accelerate-wait)), every `ScheduledActivationInterval` (10 s by default). A batch released on one tick only meets the window open at that moment, so a window shorter than the interval gets **one window's worth per tick**. That keeps every job to about one run, but drains slower than the limit allows.

  For example, a backlog of 10,000 jobs behind a limit of 50 per 2 s:

  | Activation interval | Effective rate | Drain time |
  |---|---|---|
  | 10 s (default) | 50 per 10 s = 5 jobs/s | about 33 minutes |
  | 2 s | 50 per 2 s = 25 jobs/s | about 7 minutes |

  To get the full rate, set the activation interval to the window or less. The cost is one indexed query for due rows per tick:

  ```csharp
  services.AddWarpServer<AppDbContext>(opt =>
  {
      opt.ScheduledActivationInterval = TimeSpan.FromSeconds(2);
  });
  ```

  Windows of 10 s or longer, such as a per-minute limit, are not affected.

**Schema:** `NextSlotUtc` is a new nullable column on `rate_limit_bucket`. Add it with your usual `dotnet ef migrations add` before deploying; it is additive.

## Composition with concurrency control

When a job carries both `[Mutex]` / `[Semaphore]` and `[RateLimit]`, register `AddConcurrency()` **before** `AddRateLimit()`:

```csharp
opt.AddConcurrency();   // outer — runs first
opt.AddRateLimit();     // inner — runs only if the mutex was acquired
```

DI insertion order is outer → inner. With the mutex outer, a rejected mutex acquisition short-circuits before the rate-limit token is consumed. Reversing the order leaks a token per mutex rejection — the bucket is incremented for a job that was never going to run. The next window rollover clears it, but in the meantime the effective rate-limit ceiling is lower than configured.

## DB push does not accelerate `Wait`

DB push (`opt.UseDatabasePush()`) wakes workers on `JobEnqueued` notifications. Rate-limit `Wait`-mode reschedules land in `State.Scheduled`, which is handled by `ScheduledJobActivation` (time-driven, `ScheduledActivationInterval` default 10 s). Push does **not** speed up these reschedules — they wait for the next activation tick.

If you need sub-second `Wait` precision against a high-volume key, lower `ScheduledActivationInterval` rather than reaching for push.

## Don't put PII in the key

Rate-limit keys appear in `JobLog.Message` rows and on the dashboard `/warp/ratelimits` page. Hash or tokenise tenant identifiers; never use raw emails or usernames as the key.

## Admin overrides

Live limits are runtime-tunable via `IRateLimitManager` and exposed on the dashboard at `/warp/ratelimits` (hide-on-404 nav probe). Set / clear / list endpoints sit under `/api/ratelimits`. An admin row replaces the `count` and `perSeconds` of whatever the job resolved — see [Precedence](#precedence).

**When a change takes effect.** Lowering a limit applies to the next check on every server. Raising or removing one applies at once on the server that made the change, and on the others within about **5 seconds**: each server keeps refusing a key it has [already seen exhausted](#refusing-a-known-exhausted-key-from-memory) for up to that long. During those seconds, `Skip`-mode jobs on the key can be **deleted** although the new limit would have admitted them; `Wait`-mode jobs are only rescheduled. If dropped jobs matter, raise the limit before the traffic arrives rather than during it.

**Your own manager.** Register your own `IRateLimitManager` (after `AddWarp`, so it is the one the container resolves) to take overrides from somewhere else, such as configuration or another store. Jobs then resolve their override through it, called only for a job that has a rate limit; the dashboard and the admin API use it too. A custom manager does not clear the remembered refusals described above, so a raised limit reaches every server within the same 5 seconds.

## OpenTelemetry

Each acquire attempt emits a `warp.rate_limit_check` span (Internal kind) with these tags:

- `warp.rate_limit.key`
- `warp.rate_limit.count`
- `warp.rate_limit.window_seconds`
- `warp.rate_limit.style` (`fixed` / `sliding`)
- `warp.rate_limit.outcome` (`acquired` / `skipped` / `throttled` / `lock_contention`)

- `warp.rate_limit.cached` (`true` when the refusal came from the [in-process cache](#refusing-a-known-exhausted-key-from-memory))

`acquired` is the green path; `skipped` is the `Skip` rejection; `throttled` is the `Wait` reschedule; `lock_contention` is the brief retry path after a failed `TryAcquire` on the distributed lock.

## Counters

Throttled outcomes are counted like every other job outcome: a `Skip` rejection lands in
`stats:deleted-ratelimit` and a `Wait` reschedule in `stats:requeued-ratelimit`, beneath the `deleted` /
`requeued` totals on the [Job outcomes](/docs/dashboard/health/counters/job-outcomes) counter tab. A
`Wait` reschedule also emits `warp.job.requeued` with `reason=ratelimit` (no key tag — keys are unbounded
and PII-adjacent, so they stay on spans).

## Out of scope

- **Multi-key composition** — one `[RateLimit]` per handler. Multiple distinct keys on one job is a planned extension.
