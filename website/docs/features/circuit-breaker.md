---
sidebar_position: 9
---

# Circuit Breaker

Stops hammering a failing downstream when a handler's failure rate crosses a threshold. Jobs that would have run through an open circuit are rescheduled past the recovery window instead of executing.

## Setup

Circuit Breaker is an opt-in addon. Register it inside the `AddWarpWorker` lambda:

```csharp
builder.Services.AddWarpWorker<AppDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.AddCircuitBreaker(o =>
    {
        o.Threshold = 5;                          // open after 5 consecutive failures
        o.Duration = TimeSpan.FromMinutes(1);     // stay open for 1 minute
        o.ResetJitter = TimeSpan.FromSeconds(10); // ±10s reschedule jitter
    });
});
```

The `CircuitBreakerState` entity is part of Warp's base schema — `AddWarp` registers it unconditionally — so no separate migration is required when you turn the addon on. If your DbContext was set up against an earlier Warp version that didn't include this entity, run `dotnet ef migrations add UpgradeWarp` to add it.

## Usage

By default, each request type gets its own circuit keyed on `typeof(TRequest).Name`. Override the key to share a circuit across multiple handlers:

```csharp
[CircuitBreaker(Group = "payments-gateway")]
public class ChargeCard : IJob { }

[CircuitBreaker(Group = "payments-gateway")]
public class RefundCard : IJob { }
```

Per-job overrides on the attribute take precedence over the global options:

```csharp
[CircuitBreaker(Group = "flaky-api", Threshold = 10, DurationSeconds = 300)]
public class CallFlakyApi : IJob { }
```

## States

The circuit is a three-state machine:

- **Closed** — normal operation. Handler runs. On success, `FailureCount` is reset to 0. On failure, `FailureCount` is incremented; when it reaches `Threshold`, the circuit transitions to Open.
- **Open** — `OpenUntil > now`. Jobs are rescheduled for `OpenUntil + rand(ResetJitter)` without executing the handler. A JobLog entry `"Rescheduled due to circuit breaker '<key>' (open)"` is written.
- **HalfOpen** — `OpenUntil` has lapsed. Exactly one worker wins a probe slot via an atomic CAS and executes the handler. Other workers observe `HalfOpen` and reschedule (`"... (probe-in-progress)"`). If the probe succeeds, the circuit transitions back to Closed and `FailureCount` is reset. If the probe fails, the circuit transitions back to Open with a fresh `OpenUntil`.

Without the HalfOpen gate, every worker polling when `OpenUntil` lapses would fire a concurrent probe — a thundering herd against the recovering downstream. The CAS guarantees exactly one probe fires per recovery window.

## Behavior During Open Circuit

When the circuit is open, a job goes through the pipeline like normal but the handler never runs. The pipeline behavior sets `IJobContext.Outcome = JobOutcome { State = Enqueued, ScheduleTime = OpenUntil + jitter }` and the worker reschedules the job. `FailureCount` is not incremented (the job never tried to run).

Jitter is applied to `ScheduleTime` so rescheduled jobs don't all hit the downstream at the exact moment the circuit expires. Two jobs rescheduled at the same instant against the same circuit still coordinate: only one probe wins the HalfOpen CAS, the other reschedules again with fresh jitter.

## Interaction with Retry

Circuit Breaker short-circuits before Retry. If the circuit is open when a job would have retried, the job is rescheduled — but Retry's `RetriedTimes` counter is NOT incremented (the handler didn't run, so there was nothing to retry). The retry budget is preserved for when the circuit closes and the downstream is reachable again.

## Interaction with concurrency control

Circuit Breaker runs inside the handler pipeline after the concurrency behavior (Mutex / Semaphore). A full slot short-circuits the job to `Deleted` (Skip mode) or `Enqueued` (Wait mode) before the circuit is consulted — concurrency-rejected jobs don't count toward the failure threshold.

## Configuration Options

| Option | Type | Default | Description |
|---|---|---|---|
| `Threshold` | `int` | `3` | Consecutive failures before the circuit opens |
| `Duration` | `TimeSpan` | `1m` | How long the circuit stays open before the probe window |
| `ResetJitter` | `TimeSpan` | `10s` | Jitter added to each rescheduled `ScheduleTime` |

Per-handler overrides on `[CircuitBreaker]` use `Group`, `Threshold`, `DurationSeconds`, and `ResetJitterSeconds`.

## Dashboard

Rescheduled jobs appear in the `Enqueued` tab with future `ScheduleTime`. The job's log shows `"Rescheduled due to circuit breaker '<key>' (open|probe-in-progress|probe-lost)"` — the reason disambiguates why a specific job was rescheduled.

## When To Use

- **Calls to third-party APIs** that can go down without warning (payment gateways, email providers, webhooks).
- **Downstream microservices** with a deploy window — circuit opens on failure, probes during deploy, closes when healthy.
- **Database or cache backends** that can be saturated — prevents a retry storm from piling on during recovery.

For idempotent work against reliable infrastructure, Retry alone is usually enough.
