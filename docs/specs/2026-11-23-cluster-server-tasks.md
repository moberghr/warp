# Cluster-wide server tasks: one claim per tick for the cluster, not a lock attempt per server

- **Date:** 2026-11-23
- **Issue:** #345
- **Status:** draft, for review. Nothing implemented.
- **Branch:** `feat/cluster-server-tasks`, from `perf/claim-path` (it already batches task registration at startup and adds `ServerTaskStartupDelay`).
- **Schema impact:** one new table; `server_task` keeps its per-server rows for per-server tasks.
- **Behaviour impact:** server-task scheduling, the dashboard's server-task view, and how a rolling deploy coexists with older servers (open decision D1).

## Problem

Measured with the lab's idle probe (`idle --servers=N --window=120`, 5 workers per server, PostgreSQL, no jobs):

| | 1 server | 4 servers |
|---|---|---|
| statements per minute | 389 | 1,548 |
| per server | 389 | 387 |

All idle load is per server. Per server per minute:

| Statement | /min | Source |
|---|---|---|
| `DISCARD ALL` | ~118 | pooled connection reset, one per scope that opens a connection |
| `UPDATE warp.server_task` (status) | ~43 | bookkeeping, every task run writes its row |
| `BEGIN` / `pg_try_advisory_xact_lock` / `COMMIT` / `ROLLBACK` | ~31 / 25 / 25 / 6 | the lock transaction around each cluster-locked task |
| session-lock sequence (`CLOSE ALL`, `UNLISTEN *`, `pg_try_advisory_lock`, `pg_advisory_unlock_all`, `DISCARD TEMP`, `DISCARD SEQUENCES`, `SET SESSION AUTHORIZATION DEFAULT`) | ~42 | `MessageRouter`'s session lock, every 10 s |
| `SELECT … interval_seconds FROM server_task` | ~12 | each loop re-reading its interval |
| claim `UPDATE job … current_state` | ~20 | idle workers polling (single-worker mode in this run) |
| heartbeat | ~12 | per server |

13 of the 14 server tasks are cluster-wide (they take a cluster lock); only `Heartbeat` is per server. Every server runs every cluster-wide task on its interval; all of them pay for the lock attempt, only one does the work. Each run also wraps the task in one transaction (`IServerTask.LocksWithTransaction`), which rule 6.2.1 already records as a hazard (it pins the vacuum horizon for the whole run).

## Goal

- Cluster-wide tasks run once per tick for the cluster.
- The per-run overhead on any server is one statement, not a lock transaction plus a bookkeeping write.
- A server that dies mid-run delays a task by at most its lease; it never stops it.
- Signal-driven tasks (`Orchestrator`, `MessageRouter`) still react to job activity within a short, bounded delay.
- The dashboard shows one row per cluster-wide task: last run, which server, duration, result, next due.

**Targets (idle probe, same settings as above):** 1 server ≤ 250 statements/minute (from 389); 4 servers ≤ 900 (from 1,548).

**Non-goals:** the workers' idle polling (separate; dispatcher mode already polls once per group); `Heartbeat` and `NotificationListenerTask` (per server by nature); `BackgroundServiceHost` leases (its own mechanism).

## Design

### Table

New table `cluster_task` (Warp entity `ClusterTask`, always in the schema like every Warp entity, mirrored by the server context):

| Column | Type | Meaning |
|---|---|---|
| `name` | string, PK | the task's `Name` |
| `interval_seconds` | double? | null = disabled (same meaning as `server_task.interval_seconds` today; the dashboard edits it) |
| `next_run_at` | datetime | when the task is next due |
| `lease_until` | datetime? | the current claim's expiry; null when not running |
| `claimed_by` | Guid? | the server holding the lease |
| `run_requested_at` | datetime? | a signal asked for a run while another server held the lease |
| `last_run_at`, `last_server_id`, `last_status`, `last_message`, `last_duration_ms` | | the last finished run, for the dashboard |

`server_task` keeps one row per server for per-server tasks (`Heartbeat`). The rows it holds today for cluster-wide tasks stop being written; `ServerCleanup` already deletes a server's rows with the server.

Rows are created at startup by the batched registration (`ServerTaskHost.RegisterAllAsync`): insert-if-absent per name, `next_run_at = now`. An existing row is not reset, so a restart does not re-run every task.

### Claim: one statement for every due task

Each server's host runs one scheduling loop (not one loop per task):

```sql
UPDATE cluster_task
SET lease_until = @now + lease, claimed_by = @server,
    next_run_at = @now + interval_seconds * '1 second'
WHERE name = ANY(@names)            -- the tasks this server can run
  AND interval_seconds IS NOT NULL
  AND (next_run_at <= @now OR run_requested_at IS NOT NULL)
  AND (lease_until IS NULL OR lease_until < @now)
RETURNING name
```

(SQL Server: the same with `OUTPUT`, a JSON or TVP list of names, `ROWLOCK`/`READPAST`, as the other set-based statements.)

Like the job claim, these statements live in the provider packages behind `IWarpSqlQueries` (rule 5.1: no raw SQL in `Warp.Core`): `ClaimDueClusterTasksAsync`, `RequestClusterTaskRunAsync`, `RenewClusterTaskLeaseAsync`, `CompleteClusterTaskAsync`. The database's own clock (`now()` / `SYSUTCDATETIME()`) is used inside them.

- Each returned name is this server's to run now. A row another server holds is skipped by the lease condition; the database serialises the two `UPDATE`s on the row, so exactly one wins.
- `next_run_at` advances at claim time, so finishing a run needs no write in the common case (see Completion).
- After the claim, the loop sleeps until the earliest `next_run_at` among its tasks (one cheap read, or returned by the same statement) or a signal, whichever is first. Idle cost per server: one statement per due time, shared by every task due then.

### Running a claimed task

- The task runs in its own scope, **without** a wrapping lock transaction. Exclusivity comes from the lease. Each task commits its own work: a single `SaveChanges`, or an explicit transaction where several writes must be atomic (per-task review below).
- `OnCommittedAsync` is called after `ExecuteAsync` returns without throwing, as today. A task that commits in several transactions must dispatch the events of each committed transaction itself (or keep a single transaction); the review below marks which tasks are affected.
- `RerunImmediately`: a run that did work sets `next_run_at = now` in its completion write, so the next loop pass claims it again at once.

### Completion

One `UPDATE` by name and `claimed_by = @server`:
- clears `lease_until` and `run_requested_at` (if the request predates the claim);
- records `last_*` **only when something is worth recording**: the status changed, the task returned a message, it failed, or a minute has passed since the last recorded run. An idle task that keeps returning nothing writes at most once a minute (today: every run).

When nothing needs recording and no rerun is due, the completion write is skipped entirely; the lease simply expires. That keeps an idle run at one statement (the claim) instead of two.

### Lease and crash

- Lease = max(the task's interval, a floor of 30 s), renewed by the running server every lease/3 for a run that lasts longer (one `UPDATE … SET lease_until … WHERE claimed_by = @server`). A renewal that updates nothing means another server took over; the run is cancelled.
- A server that dies mid-run: `next_run_at` already advanced, the lease expires, and the next claim after the later of the two picks the task up. Late by at most one interval or one lease, never stopped. Every task is idempotent and works in bounded slices, so a lost tick is caught up by the next one.
- Clocks: `now` is the database's (`now()` / `SYSUTCDATETIME()`), not the server's, so lease checks do not depend on server clock skew.

### Signals

`Orchestrator` (job finalized) and `MessageRouter` (message enqueued) are woken in-process today. A woken server:
1. coalesces wakes per task (at most one claim attempt per task per 250 ms);
2. claims the task directly (the claim statement above, for that one name, ignoring `next_run_at`);
3. if another server holds the lease, sets `run_requested_at = now` on the row (one `UPDATE`); the holder's completion sees the request and sets `next_run_at = now`, so the work is redone right after.

Per wake this is one statement, the same as today's lock attempt. Under load the coalescing bounds it at 4 per second per task per server.

### Per-task review

| Task | Interval | Atomicity today | Change needed |
|---|---|---|---|
| `ServerCleanup` | 30 s | own transaction + row locks | none |
| `StaleJobRecovery` | 30 s | own transactions + `FOR UPDATE SKIP LOCKED` | none; post-commit events already per transaction |
| `MessageRouter` | 10 s + signal | own transaction; session lock only to stop duplicate work (its claim uses `SKIP LOCKED`) | drop the session lock (`LocksWithTransaction => false` path) |
| `Orchestrator` | 10 s + signal | several `ExecuteUpdate`/`SaveChanges` inside the lock transaction | check each step is safe to commit alone (readiness is re-read every pass); otherwise one explicit transaction per pass |
| `ScheduledJobActivation` | 10 s | one `SaveChanges` | none |
| `RecurringJobScheduler` | 15 s | one `SaveChanges` (jobs + `NextExecution`) | none |
| `CounterAggregator` | 1 min | read counters → write statistics → delete counters, per batch, inside the lock transaction | one explicit transaction **per batch** (smaller than today's whole-run transaction, which rule 6.2.1 warns about) |
| `ErrorGroupAggregator` | 60 s | drain inbox + upsert groups, per batch | one transaction per batch; dispatch `IssueRegressed` events per committed batch |
| `StatisticRollup` | 10 min | sum-then-delete per pass, "crash-idempotent inside the lock transaction" | one transaction per pass |
| `MetricRollup` | 1 min | compaction + rollup, two saves | one transaction per pass |
| `SloEvaluator` | 1 min | one `SaveChanges`; breach events after commit | none |
| `BacklogSampler` | 60 s | one `SaveChanges` | none |
| `ExpirationCleanup` | 5 min | many bounded deletes; `FinalState` stamped before the delete; `InstanceDown` events after commit | the stamp-then-delete pair in one transaction; events per committed step |
| `Heartbeat` | 5 s | per server | unchanged (stays a per-server loop and `server_task` row) |

### Dashboard

The server-task view lists `cluster_task` rows (one per task, with the last server, last run, status, next due and whether it is running) and, per server, its per-server tasks. Editing a task's interval writes `cluster_task.interval_seconds`. Pause semantics are unchanged (they act on workers).

## Rolling deploy (open decision D1)

During a deploy, older servers still run cluster-wide tasks under the advisory lock while upgraded servers claim leases. Without care, the same task can run on an old and a new server at once. Most tasks tolerate it (row locks, `SKIP LOCKED`, idempotent deletes); `CounterAggregator`, `ErrorGroupAggregator` and the rollups could double-count.

Options:
- **D1a (recommended):** for one version, a new server also takes the old transaction-scoped advisory lock around the run (the old wrapping transaction, kept only as a compatibility shim). It costs today's lock statements during 8.x, and is removed in the next major, with a release note.
- **D1b:** require all servers of a cluster to be upgraded together (stop old servers before starting new ones). Cheapest; a documented deploy constraint.
- **D1c:** the new claim also tries the old advisory lock without a wrapping transaction (`pg_try_advisory_lock` / `sp_getapplock` session-owned, released after the run). Needs a session-mode connection (rule 2.16), which transaction-mode poolers break.

## Rules to update

- **§2.3** (the task host takes a lock per task) and **§2.16** (`MessageRouter` as a session-lock site): cluster-wide tasks are claimed by lease; `MessageRouter` leaves the list of session-lock sites.
- **§6.2.1** (the wrapping lock transaction): replaced by per-batch transactions; the slice caps stay.
- **§8.25** (`OnCommittedAsync` runs after the lock transaction commits): it runs after `ExecuteAsync` returns, and multi-transaction tasks dispatch per committed transaction.
- New rule for the claim, lease and signal handling, with the idle-probe numbers.

## Migration

- Additive: one table. Hosts pick it up through their own migrations, like every Warp table.
- The old `server_task` rows for cluster-wide tasks become inert; `ServerCleanup` removes them with their servers. No data migration.

## Tests (both providers)

- Claim race, N=2 servers (`BarrierSignal`): exactly one runs a due task.
- Lease expiry after a crashed run: another server runs the task after the lease, not before.
- Signal while another server holds the lease: `run_requested_at` set, the holder reruns.
- `RerunImmediately`: a run that did work is claimed again at once.
- Completion writes skipped for an idle run; recorded for a failure, a message, a status change, or after a minute.
- Per task: the atomicity changes above (crash between batches leaves no double count).
- Idle probe targets above, as a lab measurement (not a CI test).

## Risks

- Removing the wrapping transaction changes each cluster-wide task's failure behaviour; the per-task review is the bulk of the work and needs a test per affected task.
- Signals now write a row when another server holds the lease; the 250 ms coalescing bounds it, but it is new database traffic under load.
- A long run with a lease shorter than the run duplicates work if renewal fails; the renewal cancels the run when it loses the lease.

## Open decisions

- **D1:** rolling-deploy strategy (above).
- **D2:** coalescing window for signal-driven claims (proposed 250 ms).
- **D3:** completion recording cadence for idle runs (proposed: on change, on message, on failure, or once a minute).
