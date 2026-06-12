---
sidebar_position: 11
---

# DB Push (optional)

Replaces polling wake-up with push notifications — PostgreSQL `LISTEN`/`NOTIFY` or SQL Server Service Broker. The dispatcher, `MessageRouter`, and `Orchestrator` wake instantly on relevant events instead of waiting for their next poll. Opt-in; default behavior (polling) is unchanged if you don't call `opt.UseDatabasePush()`.

## Setup

```csharp
builder.Services.AddWarpServer<AppDbContext>(opt =>
{
    opt.UsePostgreSql();         // or opt.UseSqlServer()

    // Push benefits the dispatcher's batch-fetch path; individual workers still poll.
    opt.UseDispatcher = true;
    opt.PollingInterval = TimeSpan.FromSeconds(5); // loose polling is fine when push is on

    opt.UseDatabasePush();
});
```

Call the provider first (`UsePostgreSql()` / `UseSqlServer()`) and `UseDatabasePush()` second — the provider registers the notification transport factory that push consumes.

The provider-specific transport is wired by whichever `UsePostgreSql()` / `UseSqlServer()` you called. Transports are resilient to connection drops — the listener reconnects with exponential backoff and fires a drain signal on every reconnect so jobs enqueued during the gap are picked up.

## What Push Accelerates

| Task             | Wake trigger                      | Push latency |
|------------------|-----------------------------------|--------------|
| Dispatcher fetch | `JobEnqueued`                     | &lt;50ms        |
| `MessageRouter`  | `MessageEnqueued`                 | &lt;50ms        |
| `Orchestrator`   | `JobFinalized`                    | &lt;50ms        |

Worker-fetch push only wires when `UseDispatcher = true` — individual-worker mode keeps polling to avoid a thundering herd on the same fetch query.

## Scheduled Jobs

Push accelerates *immediate* enqueues. Jobs published via `Schedule(job, at)` sit in `State.Scheduled` until `ScheduledJobActivation` flips them to `Enqueued` — only then does the `JobEnqueued` notification fire. Dispatcher pickup after activation is &lt;50ms via push, but the activation itself is time-driven and bounded by `ScheduledActivationInterval` (default 5s). If you need sub-second precision on scheduled jobs, lower that interval — polling is the only mechanism, since there's no event for "wall-clock time has advanced."

## SQL Server Setup Requirements

Service Broker must be enabled on the target database. Warp creates the message type / contract / queue / service idempotently on first publish, but it cannot run `ALTER DATABASE ... SET ENABLE_BROKER` for you (that requires exclusive DB access). If broker isn't enabled, the transport logs an actionable error and degrades silently to polling:

```sql
ALTER DATABASE [YourDb] SET ENABLE_BROKER WITH ROLLBACK IMMEDIATE;
```

## Observability

Transport failures are logged at Warning and incremented on `warp.notifications.publish_failures` (OpenTelemetry counter). Successful publishes increment `warp.notifications.published`. Alert on the failure counter if push health matters to your SLOs. Missed notifications caused by transient connection loss are picked up by the drain-on-reconnect signal, so a rare publish failure is not a correctness issue — it costs at most one poll interval of latency.

## Upgrading From &lt;0.9

The `Scheduled` state was introduced alongside DB push. Future-dated jobs published on the old codebase land in `Enqueued` with `ScheduleTime > now` and are correctly gated by a defensive predicate in worker queries — but they won't appear in the dashboard's Scheduled list until their time arrives. To migrate them eagerly, run once after upgrade:

```sql
UPDATE warp.job
SET    current_state = 7  -- State.Scheduled
WHERE  current_state = 1  -- State.Enqueued
  AND  schedule_time > now();
```
