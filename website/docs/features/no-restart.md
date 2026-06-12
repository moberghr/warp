---
sidebar_position: 8
---

# NoRestart (Stale-Recovery Opt-Out)

When a worker crashes mid-job, Warp's `StaleJobRecovery` normally requeues the job so another worker can pick it up. That's the right default for idempotent work — but some jobs must never run twice (charge a card, send an email, call a non-idempotent API). The NoRestart feature lets those jobs opt out: on worker crash they are marked `Failed` instead of requeued.

## Setup

NoRestart is an opt-in addon. Register it alongside `AddWarp` / `AddWarpServer`:

```csharp
builder.Services.AddWarpServer<AppDbContext>();
builder.Services.AddWarpNoRestart();
```

Without `AddWarpNoRestart()`, the `[NoRestart]` / `[Restart]` attributes are silently ignored (the publish behavior isn't registered). `.WithRestart(bool)` still works — it writes metadata directly and doesn't need the addon.

## Usage

Three ways to opt a job out of restart:

### `[NoRestart]` attribute

Apply to the job class for a blanket "never restart me" policy:

```csharp
[NoRestart]
public class ChargeCard : IJob
{
    public int CustomerId { get; set; }
    public decimal Amount { get; set; }
}

await publisher.Enqueue(new ChargeCard { CustomerId = 123, Amount = 50 });
```

### `[Restart]` attribute

Explicitly opts in (useful if you've flipped the global default to `false`):

```csharp
[Restart]
public class RegenerateThumbnail : IJob { /* idempotent */ }
```

Applying both `[NoRestart]` and `[Restart]` to the same class throws `InvalidOperationException` at publish time.

**Inheritance**: both attributes inherit by default (`Inherited = true`). A base class decorated with `[NoRestart]` applies to every derived concrete job, so you can declare one policy on `PaymentJobBase` and have all payment jobs opt out without repeating the attribute. A derived class with its own `[NoRestart]` or `[Restart]` overrides any attribute on the base — the closest direct declaration wins.

### `.WithRestart(bool)` fluent extension

Per-publish override — wins over attributes and over the global default:

```csharp
await publisher.Enqueue(
    new SendWebhook { Url = url },
    new JobParameters().WithRestart(canBeRestarted: false));
```

### Global default

Set `RestartStaleJobsByDefault` on `WarpServerConfiguration` to flip the fleet-wide default:

```csharp
builder.Services.AddWarpServer<AppDbContext>(config =>
{
    config.RestartStaleJobsByDefault = false; // jobs fail on crash unless they opt in
});
```

## Override Chain

When `StaleJobRecovery` evaluates a stale job, it resolves `CanBeRestarted` in this order (first non-null wins):

1. Per-publish metadata set via `.WithRestart()`
2. `[NoRestart]` / `[Restart]` attribute on the job class (written at publish time by `NoRestartPublishBehavior`)
3. `RestartStaleJobsByDefault` on `WarpServerConfiguration` (default `true`)

## How It Works

NoRestart has two moving parts:

- **`NoRestartPublishBehavior<T>`** runs at publish time. If metadata doesn't already carry a `CanBeRestarted` value, it inspects the job type for `[NoRestart]` / `[Restart]` and writes `CanBeRestarted = false` / `true` into the job's metadata. Attribute lookups are cached per closed generic type.
- **`StaleJobRecovery`** runs on each server (default every 30s). It finds jobs in `Processing` with `LastKeepAlive` older than `InvisibilityTimeout`, then for each:
  - If `CancellationMode != None` → `Deleted` (user intent wins).
  - Else read `CanBeRestarted` from metadata, fall back to `RestartStaleJobsByDefault`.
    - `true` → `Enqueued` with an `EventType = "Requeued"` warning log.
    - `false` → `Failed`, `ExpireAt = null`, `stats:failed` counter incremented, `EventType = "Failed"` error log `"Failed by crash recovery — job opted out of restart"`.

`Failed` jobs are never auto-deleted (per the project-wide rule); they stay visible in the dashboard for operator review until explicitly cleaned up.

## Dashboard

Jobs failed by crash recovery appear in the `Failed` tab with an error-level log `"Failed by crash recovery — job opted out of restart"`. The task activity log shows the per-sweep breakdown: `"Recovered 3 stale jobs (2 requeued, 1 failed, 0 deleted)"`.

## When To Use

- **Payments / charges** — single side effect, never retry on crash.
- **Outbound notifications** (email, SMS, webhooks) — duplicate delivery is worse than missed delivery.
- **External API calls** against non-idempotent endpoints.
- **Legal / compliance workflows** where retry semantics must be explicit.

For idempotent work — the large majority of background jobs — leave the default in place and let the worker requeue on crash.

## Relationship to Retry

NoRestart and the Retry addon are independent:

- **Retry** governs what happens when the handler *throws*. Catches the exception, increments `RetriedTimes`, reschedules.
- **NoRestart** governs what happens when the worker *dies* mid-execution without ever completing the handler. Affects stale-recovery only.

A `[NoRestart]` job can still declare `[Retry(MaxRetries = 3)]` — the two policies never collide.
