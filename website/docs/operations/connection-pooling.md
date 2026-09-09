---
sidebar_position: 2
---

# Connection Pooling

Warp needs a **session-mode** connection to the database. A pooler running in *transaction* mode silently breaks distributed locking and database push. This page covers why, how to tell whether your connection is affected, and what to do about it.

If you landed here from an exception, this is the one:

```
System.InvalidOperationException: Attempted to release a lock that was not held
   at Medallion.Threading.Postgres.PostgresAdvisoryLock.ReleaseAsync(...)
```

Skip to [Checking a connection string](#checking-a-connection-string).

## Why Warp needs a session

Warp takes two different kinds of database lock, and only one of them survives transaction pooling.

| Kind | Primitive | Released by | Used for |
| --- | --- | --- | --- |
| **Transaction-scoped** | `pg_try_advisory_xact_lock` / `sp_getapplock` in a transaction | `COMMIT` | Most server tasks — `Heartbeat`, `Orchestrator`, `ScheduledJobActivation`, `RecurringJobScheduler`, `StaleJobRecovery`, `CounterAggregator`, `ServerCleanup`, `ExpirationCleanup`, `ErrorGroupAggregator`, `StatisticRollup` |
| **Session-scoped** | `pg_advisory_lock` / `sp_getapplock` at session scope | An explicit unlock on the **same** backend session | Work spanning more than one transaction: recurring-job registration, `MessageRouter`, sagas, rate limiting, concurrency slots |

A transaction-scoped lock is acquired, held and released inside one transaction, so it is safe through any pooling mode. A session-scoped lock is not: it must be released on the same backend session that took it.

A pooler in **transaction mode** hands your client a different backend session per transaction. The acquire lands on one backend and the release on another, so the unlock reports that the lock was never held — and, more importantly, **the acquire never provided mutual exclusion in the first place**. The exception is the symptom; the missing mutual exclusion is the actual bug.

This asymmetry is why a transaction-pooled deployment can look *mostly* healthy. Jobs run, the heartbeat ticks, the dashboard populates — while message routing quietly stops and sagas requeue forever.

Transaction pooling also breaks `UseDatabasePush()` on Postgres, which needs session-scoped `LISTEN`/`NOTIFY`.

## It is the pool mode, not the pooler

A pooler is not itself a problem. **`pool_mode = session` behaves exactly like a direct connection** — advisory locks, `LISTEN`/`NOTIFY`, `SET` and prepared statements all work normally — and Warp is fully supported through it. Only `transaction` mode, and `statement` mode which is worse, break session state.

Open-source PgBouncer defaults to session mode. Most managed poolers default to transaction mode, which is why this usually surfaces on a hosted database rather than a self-hosted one.

| Pooler | Default mode | What to use for Warp |
| --- | --- | --- |
| PgBouncer (self-hosted) | `session` | Fine as shipped. Only `pool_mode = transaction` / `statement` breaks it |
| Neon | pooled `-pooler` host is transaction mode, with no session option | The direct host, without `-pooler`. Vercel's Neon integration puts the **pooled** host in `DATABASE_URL` / `POSTGRES_URL`; the direct one is `DATABASE_URL_UNPOOLED` / `POSTGRES_URL_NON_POOLING` |
| Supabase | port `6543` (Supavisor) is transaction mode, but switchable to session mode in the dashboard | Either the direct `5432` connection, or `6543` set to session mode |
| Azure Flexible Server | built-in PgBouncer on port `6432`, transaction mode | Set `pgbouncer.default_pool_mode = session`, or connect on port `5432` |

PgBouncer can also run several pools in different modes against the same database — transaction mode for high-volume request traffic, session mode for the connection Warp uses. **Only the connection Warp opens has to be session-scoped.** Keep the pooled string for ordinary request-scoped queries if you want; give Warp the direct or session-mode one.

## Checking a connection string

Do not infer the mode from the hostname or the port — verify it. Run these as **two separate round trips** (two statements in interactive `psql`, not one semicolon-joined batch, which is a single transaction and would pass either way):

```sql
SELECT pg_try_advisory_lock(4242);
SELECT pg_advisory_unlock(4242);
```

Both must return `t`.

- `t` then `t` — session-scoped state survives. The connection is usable for Warp.
- `t` then `f` — the unlock landed on a different backend. The connection is transaction-pooled and **is not usable for Warp**. You have also just stranded advisory lock `4242` on a backend in the pool, which is exactly the failure described below.

## What happens to a lock that failed to release

Two outcomes, and Warp cannot tell them apart from the failed unlock alone:

- **The session died** — scale-to-zero suspend, a dropped connection, a killed backend. Postgres releases every session-scoped advisory lock a backend held when it terminates, and SQL Server does the same for `sp_getapplock`. Nothing leaked; the warning is benign.
- **A transaction-mode pooler routed the release elsewhere.** The lock is still held on the original backend, which is back in the pooler's server pool. Nothing can address that session to unlock it, so the lock stays stranded until the pooler recycles that server connection (`server_lifetime`, or a compute restart). PgBouncer does not run `DISCARD ALL` on server release in transaction mode, so it is not cleaned up for you.

A stranded lock degrades whichever site owned it, and most of the failure modes are quiet:

| Lock | Acquire timeout | Effect while stranded |
| --- | --- | --- |
| `warp:recurring:{name}` | 15s | `AddOrUpdateRecurringJob` throws `TimeoutException` — usually at startup, so it is loud |
| `warp:message-routing` (`MessageRouter`) | 0 | The task silently skips every tick — **messages stop fanning out to child jobs, cluster-wide, with no error** |
| `warp:saga:{type}:{key}` | 0 | That correlation key's messages requeue forever with jitter |
| `warp:ratelimit:{key}` | 5s | Jobs on that key reschedule repeatedly instead of running |
| `warp:concurrency:{key}` slot | 0 | The slot is consumed permanently, so the effective limit drops by one |

There is no way for Warp to reclaim a stranded lock — you cannot unlock a session you cannot address, and terminating an anonymous pooled backend is not something a job library should do. The remedy is the connection string.

:::note[Releasing a lock never throws]
A release runs in a `finally` / `await using` **after** the guarded work has already committed, so propagating a release failure cannot undo anything. It can only fail a completed operation (`AddOrUpdateRecurringJob` taking down host startup) or replace the real exception a guarded handler threw. Every DB-backed lock handle is therefore wrapped so the failure is logged at `Warning` with the lock name instead of thrown.

Acquiring a lock can still fail; releasing one cannot. Swallowing does not change the lock's fate — throwing releases nothing either. A repeated release warning means the lock session is being lost, which is a real problem: check the pool mode first.
:::

## Pool sizing under session mode

Session mode gives no multiplexing while a connection is open, so a pool sized for transaction-mode traffic will starve. Warp holds several long-lived connections per process:

- the worker pool (`WorkerCount`, default `min(ProcessorCount * 5, 20)`)
- the Warp server context, used by the server tasks
- the lock connections
- the `LISTEN` connection, under `UseDatabasePush()`

Size `default_pool_size` for that, or point Warp at a direct connection and leave the pooler to your request traffic. Under-sizing shows up as client-login timeouts, not as a lock error.

## Scale-to-zero Postgres

Neon suspends the compute after a period of inactivity (5 minutes by default on the free and launch plans), which terminates every session. That drops any session-scoped advisory lock a long-running task was holding, along with any `LISTEN` registration. Other serverless Postgres offerings behave the same way.

For a continuously running Warp server this rarely bites — the heartbeat and server-task loops keep the database busy well inside the window. A host that idles (a dashboard-only process, a publisher that runs occasionally) can see release warnings after a suspend. Disable autosuspend, or raise the timeout, for the database a Warp server points at.

## Summary

- Give Warp a **direct or session-mode** connection. A pooled connection for the rest of your application is fine.
- Verify with the two-round-trip `pg_advisory_lock` test rather than trusting the hostname.
- On Neon, that means the host **without** `-pooler` — not what Vercel's integration writes into `DATABASE_URL`.
- If you must pool Warp's own connection, use `pool_mode = session` and size the pool for Warp's long-lived connections.
