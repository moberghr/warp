---
sidebar_position: 11
---

# DB Push (optional)

Replaces polling wake-up with push notifications — PostgreSQL `LISTEN`/`NOTIFY` or SQL Server Service Broker. The dispatcher, `MessageRouter`, and `Orchestrator` wake instantly on relevant events instead of waiting for their next poll. Opt-in; default behavior (polling) is unchanged if you don't call `opt.UseDatabasePush()`.

:::note[Requires a session-mode connection]
Postgres `LISTEN`/`NOTIFY` is session-scoped, so a pooler in transaction mode drops the registration and push silently never fires. See [Connection Pooling](/docs/operations/connection-pooling).
:::

## Setup

```csharp
builder.Services.AddWarpServer<AppDbContext>(opt =>
{
    opt.UsePostgreSql();         // or opt.UseSqlServer()

    // Dispatcher mode is the default since 8.0, so one fetch hands out the work instead of
    // every bare worker waking at once.
    opt.PollingInterval = TimeSpan.FromSeconds(5); // loose polling is fine when push is on

    opt.UseDatabasePush();
});
```

Call the provider first (`UsePostgreSql()` / `UseSqlServer()`) and `UseDatabasePush()` second — the provider registers the notification transport factory that push consumes.

The provider-specific transport is wired by whichever `UsePostgreSql()` / `UseSqlServer()` you called. Transports are resilient to connection drops — the listener reconnects with exponential backoff and fires a drain signal on every reconnect so jobs enqueued during the gap are picked up.

## What Push Accelerates

| Task                         | Wake trigger                      | Push latency |
|------------------------------|-----------------------------------|--------------|
| Worker fetch (both modes)    | `JobEnqueued`                     | &lt;50ms        |
| `MessageRouter`              | `MessageEnqueued`                 | &lt;50ms        |
| `Orchestrator`               | `JobFinalized`                    | &lt;50ms        |

A cross-process `JobEnqueued` wakes workers in both modes: the listener wakes the dispatcher **and** signals every bare worker of the process. In dispatcher mode (the default) one fetch hands out the work; with `UseDispatcher = false` every bare worker of the process wakes at once and each fetches for itself. Push is not "without effect" in either mode.

## Which Saves Send a Notification

Any save of your `DbContext` that commits new `Enqueued` jobs sends a `JobEnqueued` notification per queue: `IPublisher.SaveChangesAsync()`, `IBatchPublisher.SaveChangesAsync()`, and a plain `db.SaveChangesAsync()` after `Enqueue`. Warp adds an EF Core interceptor to the context registered with `AddWarp` to do this.

- **Explicit transactions:** a save inside `db.Database.BeginTransactionAsync()` sends its notification when the transaction commits, and nothing on rollback. Sending it earlier would wake a worker onto rows it cannot see yet.
- **How it is sent:** this process's workers are woken immediately, inline. The notification for other processes is published by a background relay right after the save, off the saving thread. Notifications that pile up while one is being published are folded into one per queue. The relay runs as a hosted service, so a process without a running host (a bare `ServiceProvider`) only wakes its own workers. On graceful shutdown the relay sends whatever is still queued.
- **Not covered:** `System.Transactions.TransactionScope` and a transaction committed outside EF (`Database.UseTransaction`). There the notification goes out at save time or not at all, and the job waits for the next poll.

Up to 7.7.0, only the publishers' `SaveChangesAsync` sent it, so jobs committed with `db.SaveChangesAsync()` waited up to `MaxPollingInterval` (5 minutes with push on). Without push, the in-process wake-up was skipped the same way.

## Scheduled Jobs

Push accelerates *immediate* enqueues. Jobs published via `Schedule(job, at)` sit in `State.Scheduled` until `ScheduledJobActivation` flips them to `Enqueued` — only then does the `JobEnqueued` notification fire. Dispatcher pickup after activation is &lt;50ms via push, but the activation itself is time-driven and bounded by `ScheduledActivationInterval` (default 10s). If you need sub-second precision on scheduled jobs, lower that interval — polling is the only mechanism, since there's no event for "wall-clock time has advanced."

## SQL Server Setup Requirements

Service Broker must be enabled on the target database. Warp creates the message type / contract / queue / service idempotently on first publish, but it cannot run `ALTER DATABASE ... SET ENABLE_BROKER` for you (that requires exclusive DB access). If broker isn't enabled, the transport logs an actionable error and degrades silently to polling:

```sql
ALTER DATABASE [YourDb] SET ENABLE_BROKER WITH ROLLBACK IMMEDIATE;
```

## Observability

Transport failures are logged at Warning and incremented on `warp.notifications.publish_failures` (OpenTelemetry counter). Successful publishes increment `warp.notifications.published`. Notifications dropped because the relay's queue was full increment `warp.notifications.dropped`; each costs at most one poll interval on another server. Alert on the failure counter if push health matters to your SLOs. Missed notifications caused by transient connection loss are picked up by the drain-on-reconnect signal, so a rare publish failure is not a correctness issue — it costs at most one poll interval of latency.

## Upgrading From &lt;0.9

The `Scheduled` state was introduced alongside DB push. Future-dated jobs published on the old codebase land in `Enqueued` with `ScheduleTime > now` and are correctly gated by a defensive predicate in worker queries — but they won't appear in the dashboard's Scheduled list until their time arrives. To migrate them eagerly, run once after upgrade:

```sql
UPDATE warp.job
SET    current_state = 7  -- State.Scheduled
WHERE  current_state = 1  -- State.Enqueued
  AND  schedule_time > now();
```
