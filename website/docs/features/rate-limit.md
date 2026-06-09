---
sidebar_position: 5.7
---

# Rate Limit

Throttle jobs sharing a key to N starts per window. When the bucket is full, the surplus is either dropped (`Skip`) or rescheduled (`Wait`). Window shape picks between a wall-clock floor (`Fixed`) and a rolling tail (`Sliding`).

Opt-in addon — register with `opt.AddRateLimit()` on the builder.

## Quick start

```csharp
builder.Services.AddWarpServer<AppDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.AddConcurrency();   // optional — register BEFORE AddRateLimit if both apply
    opt.AddRateLimit();
});

// Per-handler attribute (default: Mode = Skip, Style = Fixed)
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

```csharp
[RateLimit("partner-api", count: 5, perSeconds: 1, Style = RateLimitStyle.Sliding)]
public class CallPartnerApi : IJob { }
```

`perSeconds` is capped at 7 days (`RateLimitAttribute.MaxWindowSeconds`). Inputs past the cap throw at construction.

## Precedence

Most specific wins:

```
WithRateLimit(...)         // per-publish, highest priority
  → [RateLimit(...)]       // per-handler-type attribute
    → admin override       // IRateLimitOverrideManager — runtime tunable
```

Attribute and fluent values resolve at publish; admin overrides are read on every check (no caching at the limit boundary), so raising or lowering N takes effect on the next acquire attempt.

## What the pipeline holds

The distributed lock is held only for the brief **check-and-increment** — never during handler execution (unlike `[Mutex]` / `[Semaphore]`, where the lock spans the whole handler). That keeps rate limits friendly to long-running jobs: a single 10-minute job doesn't block other tokens for the duration of its run.

Live state lives in `RateLimitBucket`; the entity is contributed only when `AddRateLimit()` is registered.

## Composition with concurrency control

When a job carries both `[Mutex]` / `[Semaphore]` and `[RateLimit]`, register `AddConcurrency()` **before** `AddRateLimit()`:

```csharp
opt.AddConcurrency();   // outer — runs first
opt.AddRateLimit();     // inner — runs only if the mutex was acquired
```

DI insertion order is outer → inner. With the mutex outer, a rejected mutex acquisition short-circuits before the rate-limit token is consumed. Reversing the order leaks a token per mutex rejection — the bucket is incremented for a job that was never going to run. The next window rollover clears it, but in the meantime the effective rate-limit ceiling is lower than configured.

## DB push does not accelerate `Wait`

DB push (`opt.UseDatabasePush()`) wakes workers on `JobEnqueued` notifications. Rate-limit `Wait`-mode reschedules land in `State.Scheduled`, which is handled by `ScheduledJobActivation` (time-driven, `ScheduledActivationInterval` default 5 s). Push does **not** speed up these reschedules — they wait for the next activation tick.

If you need sub-second `Wait` precision against a high-volume key, lower `ScheduledActivationInterval` rather than reaching for push.

## Don't put PII in the key

Rate-limit keys appear in `JobLog.Message` rows and on the dashboard `/warp/ratelimits` page. Hash or tokenise tenant identifiers; never use raw emails or usernames as the key.

## Admin overrides

Live limits are runtime-tunable via `IRateLimitOverrideManager` and exposed on the dashboard at `/warp/ratelimits` (hide-on-404 nav probe). Set / clear / list endpoints sit under `/api/ratelimits`. Override precedence: `admin row > attribute > publish-time`.

## OpenTelemetry

Each acquire attempt emits a `warp.rate_limit_check` span (Internal kind) with these tags:

- `warp.rate_limit.key`
- `warp.rate_limit.count`
- `warp.rate_limit.window_seconds`
- `warp.rate_limit.style` (`fixed` / `sliding`)
- `warp.rate_limit.outcome` (`acquired` / `skipped` / `throttled` / `lock_contention`)

`acquired` is the green path; `skipped` is the `Skip` rejection; `throttled` is the `Wait` reschedule; `lock_contention` is the brief retry path after a failed `TryAcquire` on the distributed lock.

## Out of scope (v1)

- **`stats:ratelimit` counter** — first attempt saturated the PG connection pool under load (fresh DbContext scope per fire). Deferred to v1.1 with worker-side wiring.
- **`[RateLimit]` on `IMessage` types / handler classes** — same `request is not IJob` bail-out as Timeout; planned cross-addon refactor reads attributes at pipeline time from `_jobContext.HandlerType`.
- **Multi-key composition** — one `[RateLimit]` per handler. Multiple distinct keys on one job is a planned extension.
