---
sidebar_position: 6
---

# Releases

## 3.11.0

*2026-08-12*

Additive, but **this release requires a migration** — one new index on `job`. Everything else is additive with no schema impact: the new metric keys are new rows in the existing `Counter` / `Statistic` tables. Two deliberate behaviour changes affect **what numbers you see**, not how jobs execute; a third relaxes several background-task intervals, trading latency for a much quieter idle server.

:::warning Migration required — read before upgrading

Earlier drafts of these notes said "no migration". **That is no longer true.** See [Migration: one new index on `job`](#migration-one-new-index-on-job) below, including the note on building it without a write outage.

:::

### Migration: one new index on `job`

Run `dotnet ef migrations add <Name>` against your `DbContext` and apply it. The migration contains a single statement:

```sql
CREATE INDEX ix_job_current_state_schedule_time
    ON warp.job USING btree (current_state, schedule_time);
```

**Why.** `ScheduledJobActivation` runs `UPDATE job SET current_state = 1 WHERE current_state = 7 AND schedule_time <= now() RETURNING id, queue, schedule_time` on every tick. It filters `current_state` **without** `kind`, so it cannot use the leading column of the existing `(Kind, CurrentState, Queue, ScheduleTime)` index. The planner instead used `(Kind, CurrentState, CreateTime)` on `current_state` alone, heap-fetched **every** row sitting in `Scheduled`, and discarded the future-dated ones. On 250,000 job rows with 5,675 in `Scheduled` that cost **31.8 ms and 2,686 buffers per tick**; with the index it is **0.35 ms and 4 buffers**, because the 2,841 rows previously fetched-and-discarded are now excluded by the index condition.

The cost is **+17.8% on a bulk insert and +8.9% on a bulk state transition** — about **5 µs of database time over a job's entire life** (one insert plus roughly three state transitions) — and 9.2 MB of index against 71 MB of existing `job` indexes. The worker's claim query was re-checked with and without: identical plan, no regression.

**The SQL Server provider's activation statement has the identical predicate**, so the index applies there too and is emitted by the same migration.

:::danger Building the index locks the table

A plain `CREATE INDEX` takes a lock that **blocks writes to `job` for the whole build**. On a large `job` table that is a write outage — jobs cannot be published or claimed while it runs. If that matters, hand-edit the generated migration before applying it:

- **PostgreSQL** — `CREATE INDEX CONCURRENTLY` (must run outside a transaction; in EF Core, use `migrationBuilder.Sql(..., suppressTransaction: true)`).
- **SQL Server Enterprise** — `WITH (ONLINE = ON)`.

:::

**When this index is not worth it.** There is **no jobs/sec break-even** — write cost scales with your job rate and read cost scales with how many rows sit in `Scheduled`, which is itself the job rate times the fraction delayed times the average delay, so throughput appears on both sides and cancels. **The deciding variable is how long work sits in `Scheduled`.** Warp's defaults keep that high: retry delays are `[15, 60, 300]` seconds, the webhook retry schedule is `[1m, 10m, 1h, 6h]` (a delivery in its six-hour backoff holds a `Scheduled` row for six hours), and saga timeouts and rate-limit `Wait` reschedules park rows there too. But if you run **no retries, no webhooks, no sagas and no scheduled jobs**, nothing accumulates in `Scheduled`, the read cost this removes was never being paid, and you are buying ~18% insert overhead for nothing — drop the index from the generated migration. See [Queue metrics](./features/queue-metrics.md#scheduled-jobs-and-the-currentstate-scheduletime-index).

> **Correction.** An earlier analysis on this branch described the pre-index plan as a **sequential scan** costing ~115 ms. That was wrong — it came from a test seed whose `parent_job_id` column was entirely NULL, which changed which index the planner found usable. Against a realistic seed the pre-index plan is an index scan and the real cost is 31.8 ms of pointless heap fetching. The 31.8 ms figure is the one to trust.

### Requeue and outcome metrics get a reason

`stats:requeued` was a single flat number covering retry backoff, mutex waits, rate-limit waits and saga conflicts alike, and `stats:deleted` mixed operator deletes with mutex skips, rate-limit skips and timeout deletes. Both now carry a breakdown alongside the unchanged totals:

```
stats:succeeded                 top-level total — a success is not unsuccessful, so it sits
                                outside the umbrella
stats:unsuccessful              umbrella — DERIVED on read as failed + deleted, never stored
  stats:failed                  state total
    stats:failed-retry-exhausted / -saga
  stats:deleted                 state total
    stats:deleted-timeout / -concurrency / -ratelimit / -saga
stats:requeued                  top-level total — a requeue is not a terminal outcome, so it
                                sits outside the umbrella too
  stats:requeued-retry / -concurrency / -ratelimit / -circuitbreaker / -saga / -manual / -recovery
stats:retried-jobs              standalone — distinct jobs that entered retry, not retry events
```

Totals are written independently of the breakdown, so nothing needs summing, and an outcome with no attributable cause (a plain handler throw with no addon involved) still lands in its total — the difference between a total and the sum of its reasons is the unattributed remainder.

Retry **exhaustion** is now distinguishable from a first-attempt failure, which it never was: the retry behaviour used to signal exhaustion by setting no outcome at all, producing exactly the same observable event as a job with no retry policy.

The umbrella is **computed on read as `failed + deleted`** and is never written as a `Counter` row. Ten sites move those two totals, and a stored umbrella would have to be maintained at every one or under-report. Only those two belong under it: a **success is not unsuccessful**, and a **requeue is not terminal** — the job runs again and lands in one of the other totals — so both stay top-level.

`Reason` is a new `init` property on the public `JobOutcome`, typed as the closed `OutcomeReason` enum. Custom pipeline behaviours may set it. It is deliberately not a free-form string: an unbounded reason would mint an unbounded number of `Statistic` rows. The token lookup returns `unknown` for an unmapped member rather than throwing — it runs inside the job's own `catch`, where a throw would be laundered into a fake handler failure and re-poison the job forever; a guard test keeps every member mapped.

New meter: **`warp.job.requeued`** (`job.type`, `queue`, `reason`, `application`), always emitted. Concurrency and rate limiting previously emitted spans only, which are sampled — a requeue was not a countable signal. The concurrency and rate-limit **keys** are deliberately not tags (unbounded and PII-adjacent); they stay on the span.

### Dashboard requeues and crash recovery are now counted

Requeueing from the dashboard, and recovering a job whose worker died, both wrote a `Requeued` log row and **no counter at all** — so `stats:requeued` silently under-reported every operator action and every recovered crash. Both now count, attributed as `-manual` and `-recovery`.

### Behaviour change: retries are reported as retried, not failed

`RetryOptions.Delays` defaults to `[15, 60, 300]`, so a retry is scheduled into the future and lands in `Scheduled`, not `Enqueued`. The worker's OpenTelemetry status label tested only `Enqueued`, so **every default-configured retry attempt was emitted as `status=failed`** on `warp.job.completed` and `warp.job.duration`, and `warp.job.retried` never fired. The database recorded the same attempt correctly as a requeue, so the two disagreed.

**If you alert on `warp.job.completed{status=failed}`, your failure count will drop and a `status=retried` series will appear.** Job execution is unchanged — only the label.

### Behaviour change: outcome stats are append-only

`stats:succeeded`, `stats:failed` and `stats:deleted` used to be **decremented** when a requeue or delete undid the outcome already recorded. That is removed, on the principle that **a metric records an event and current state is a query**:

- The decrement wrote only the lifetime key, never an hourly bucket — so a lifetime total already disagreed with the sum of its own buckets.
- The dashboard throughput chart deltas these counters, and a decrement could drive that delta negative (silently clamped to zero, under-reporting the tick).
- Failed jobs never auto-expire, so the **Failed tile** — a live `Job` query — already answers "how many are failed right now", accurately and permanently. The decrement made the counter a worse copy of that query while destroying the only thing a counter uniquely provides: "how many ever failed".

**These three totals will read higher than before for anyone who requeues or deletes jobs**, and they no longer decrease. Dashboard tiles and navigation badges are unaffected — they query the `Job` table, not these counters.

### Behaviour change: the circuit breaker now counts retry exhaustion

With the breaker registered **outer** of retry (the documented order — "Circuit Breaker short-circuits before Retry"), the terminal failure of a retried job never incremented `FailureCount`: the breaker skipped any attempt that carried a `JobOutcome`, and retry exhaustion now carries one. The breaker counts a `Failed` outcome as the dependency failure it is; reschedule and delete outcomes are still skipped. If you run breaker-outer, circuits that never opened during retry-exhausting outages will now open — that is the feature working, not a regression. In any registration order, a handler that stamps a `Failed` outcome itself and then throws now also counts toward its circuit — a deliberately failed attempt is a failure.

### Behaviour change: `retry-exhausted` is only stamped when a retry budget existed

`RetryOptions.MaxRetries` defaults to `0` and the retry behaviour runs for every job once `AddRetry()` is called — so a job type with no `[Retry]` attribute had its first and only failure labelled `stats:failed-retry-exhausted`. Zero-budget failures are now unattributed (they land in the remainder row on the Counters page, where they belong). If you chart `stats:failed-retry-exhausted`, expect it to drop to only genuinely exhausted retries.

### Startup validation: singleton-lease renewal margin

`AddWarpServer` now throws when `BackgroundServiceLeaseTtl` (or its 30s default) is less than **3× `HealthCheckInterval`** — the lease is renewed by the heartbeat, and a thinner margin lets one slow round-trip hand a running singleton service to a second server. **A config that started before may fail at startup after upgrading** (e.g. an explicit 10s TTL against the new 5s heartbeat default, or a heartbeat raised past 10s with the TTL left at its 30s default). The exception names both knobs; raise the TTL or lower the interval.

### Behaviour change: the implicit worker group follows `DefaultQueue`

The `Publisher` now honours `WarpConfiguration.DefaultQueue` (previously ignored — untargeted publishes always landed on the literal `"default"`). To keep the pair coherent, a server whose `Queues` is left untouched now polls `[DefaultQueue]` instead of the literal `"default"` — so setting only `DefaultQueue = "orders"` publishes to `orders` **and** processes it, instead of stranding every job on a queue no worker polls. An explicitly set `Queues` is never widened.

### Behaviour change: quieter idle — seven background-task intervals relaxed

An idle Warp server had grown noisy. On the recommended `dispatcher-push` configuration it was issuing **2.9 database queries per second doing nothing at all**, against 1.2 q/s measured in May. Per-loop attribution (30s window, 3 runs, every run reproducing the same command totals exactly) found the two biggest contributors were not the newer background tasks but the two oldest cadences:

| Loop | Interval | Commands / 30s | Share of idle traffic |
|---|---|---:|---:|
| `ScheduledJobActivation` | 5s | 24 | 28% |
| `Heartbeat` | 3s | 20 | 23% |
| everything else (11 loops) | — | 43 | 49% |

Together those two were **51%** of all idle database traffic. Seven defaults have been relaxed in total:

| Setting | Was | Now | What gets slower |
|---|---|---|---|
| `PollingInterval` | 1s | **10s** | Backoff **floor** for a worker that just went idle — see below |
| `ScheduledActivationInterval` | 5s | **10s** | Worst-case `ScheduleTime` → eligible-for-pickup latency |
| `MessageRoutingInterval` | 1s | **10s** | Worst-case `IMessage` → child jobs latency (poll-only) |
| `HealthCheckInterval` | 3s | **5s** | Pause propagation; singleton-lease renewal cadence |
| `BacklogSampleInterval` | 15s | **60s** | Refresh rate of the Queues page backlog gauges |
| `ErrorGroupingInterval` | 15s | **60s** | Delay before an error appears as an issue |
| `ExpirationCleanupInterval` | 60s | **5m** | Delay between a row's `ExpireAt` and its deletion |

All seven are ordinary configuration — set any of them back if the old latency matters more to you than the query rate.

**The two that need the most thought:**

- **`PollingInterval` 1s → 10s.** This is the **floor** of the worker's exponential backoff, not a fixed poll rate: the delay grows from this floor toward `MaxPollingInterval` (30s) on consecutive empty polls and **resets to the floor the moment a job is processed**. So this does not slow a busy worker down — it slows the *first* poll after a worker goes quiet. The consequences differ by configuration:
  - **Same-process publishes are unaffected.** A `Kind=Job` row committed in `Enqueued` on the same server fires a local `JobEnqueued` signal that shortcuts the backoff entirely, independent of DB push.
  - **With `UseDatabasePush()`, cross-process publishes are unaffected** — the notification wakes the dispatcher.
  - **Without push, cross-process pickup latency can now be up to 10s instead of 1s.** If you publish from a web app and process on a separate worker process with no push configured, this is the change you will feel. Enable `UseDatabasePush()` or set `PollingInterval` back.
- **`MessageRoutingInterval` 1s → 10s.** Same shape: routing is signal-driven within a process and push-driven across processes, so this is the poll-only backstop. Note that `UseDatabasePush()` bumps this to 30s when it is still at the default — that check previously compared against a hardcoded `1s` and would have **silently stopped applying** once the default moved, leaving push deployments polling 3× more often than intended. The default is now a named constant that both sides read.

**The rest, briefly:**

- **`ScheduledActivationInterval` 5s → 10s.** This interval *is* the worst-case latency between a job's `ScheduleTime` and it becoming eligible for pickup — the task is time-driven and deliberately does not participate in DB-push wake-up, so push cannot shorten it. It has no correctness coupling: the only cost is latency. **A scheduled job may now start up to 10s after its scheduled time instead of 5s**, and the same bound applies to rate-limit `Wait`-mode reschedules and to webhook retry backoff (which was already documented as "delay + activation latency + worker pickup", not a precision scheduler).
- **`HealthCheckInterval` 3s → 5s.** The binding constraint here is `BackgroundServiceLease` renewal — the heartbeat must tick comfortably inside the 30s lease TTL, and 5s still leaves a 6× margin. The visible cost is that **pause propagation is now bounded by 5s rather than 3s**: `PauseServer`/`PauseWorkerGroup` stamps `PausedAt`, and each server stops fetching at its next heartbeat.
- **`BacklogSampleInterval` 15s → 60s.** Backlog depth and oldest-age are point samples, so a backlog spike shorter than the sampling interval can now be missed entirely. Queue-*wait* is unaffected — it is measured per claim on the hot path, not sampled.
- **`ErrorGroupingInterval` 15s → 60s.** An error now takes up to a minute to show up on the Issues page, and occurrence-inbox rows live correspondingly longer before being drained. No occurrences are lost — the drain is exactly-once by construction regardless of cadence.
- **`ExpirationCleanupInterval` 60s → 5m.** Expired rows are deleted up to five minutes late. Retention windows become correspondingly fuzzy at the edge; nothing is retained less than configured.

Four loops were measured at **exactly zero** idle cost and were left alone (`SloEvaluator`, `StatisticRollup`, `CounterAggregator`, `ExpirationCleanup`, `Orchestrator`) — their intervals are long enough, or their guards short-circuit before touching the database.

**Also fixed: `ErrorGroupAggregator` no longer pays for an idle tick.** It ran its cardinality-guard `GROUP BY` over `ErrorGroup` *before* draining the occurrence inbox — so every tick on an idle server paid for a group-count query guarding against an overflow that could not happen (measured at ~10 of 87 commands per 30s, third behind the two oldest loops). It now probes the inbox with a single `AnyAsync` and returns before loading the guard, making an idle tick one cheap `SELECT`. Behaviour when there *is* work is unchanged.

#### Measured result

Idle database commands per second, same harness as before (an `ActivityListener` on Npgsql's activity source, so every command is counted including raw `DbCommand` ones), 3 runs per scenario with every run reproducing identical totals:

| Scenario | May (2026-05-14) | Before (30s window) | After (120s window) |
|---|---:|---:|---:|
| `workers-poll` *(the default configuration)* | 6.4 | 10.1 | **3.6** |
| `workers-push` | 3.1 | 4.8 | **2.4** |
| `dispatcher-poll` | 4.5 | 8.3 | **2.9** |
| **`dispatcher-push`** *(recommended)* | 1.2 | 2.9 | **2.1** |

**The before/after columns are not a clean comparison and should not be quoted as one.** The "before" numbers came from a **30s** window, and the old polling backoff took roughly 31s to first reach its 30s ceiling — so that window was partly measuring the ramp rather than steady-state idle, inflating the "before" figures for the polling scenarios. The "after" numbers use a 120s window. **`dispatcher-push` (−28%) is the fairest row**, having the least polling in it and therefore the least ramp contamination; treat the other three deltas as directional only.

#### Known and not fixed: loop bookkeeping is ~47% of what remains

The per-statement census on `dispatcher-push` (30s) reads: 14× `UPDATE server_task` (one per server-task tick), 8× `INSERT server_log`, 7× advisory lock acquire, 6× heartbeat CTE, 3× scheduled activation, and assorted single-digit probes.

That means **roughly 47% of the remaining idle traffic is the loop machinery reporting on itself** — the per-tick `server_task` row update, plus `ServerLog` rows written by tasks that did no work: `ScheduledJobActivation` ×3, `RecurringJobScheduler` ×2, `StaleJobRecovery` ×1, `MessageRouting` ×1, `ServerCleanup` ×1 per 30s.

This is **deliberately not fixed here.** Skipping the bookkeeping write on a no-op tick would roughly halve idle traffic again, but it changes what the dashboard's server-task history means — a task that ran and found nothing would become indistinguishable from one that did not run at all — so it needs its own change and its own UI decision. Recorded here so it is not re-derived from scratch.

### Fixed: `DefaultQueue` was ignored on publish

`WarpConfiguration.DefaultQueue` is documented as the queue used when a caller names none, and `BatchPublisher` honoured it — but `Publisher` resolved `queue ?? "default"` on both its job and message paths. A deployment that set `DefaultQueue` and pointed its worker group at that queue had batch children routed correctly while ordinary publishes went to `"default"`, where nothing was polling: those jobs sat `Enqueued` and never ran. Both paths now consult `DefaultQueue`. Explicit queue arguments still win, and deployments that never set it are unaffected.

## 3.10.0

*2026-08-03*

Additive minor release — no breaking API changes. Three features: in-box **dropped-record visibility** and **metrics retention tiers** (both no migration — new rows in the existing `Statistic`/`Counter` tables) and **SLO / error budget** (two new tables, `slo_definition` / `slo_evaluation`, via a standard `dotnet ef migrations add`).

### Dropped-record visibility

Warp's lossy recording pipelines (outbound adapters, inbound endpoints, client events) drop records when their bounded channel saturates. Until now that was counted only on an OpenTelemetry meter — invisible unless you'd wired up an OTel backend, which defeats the point of the in-box dashboard. Now a saturated pipeline is visible and alertable **without OTel**:

- A **Dropped (24h)** tile on the dashboard turns red when any pipeline has dropped records recently (windowed, so it clears itself once drops stop). The count is folded into the durable `warpsys:records-dropped` stat via the same retention tiers as every other metric — bounded storage, no per-drop database write on the failure path.
- A throttled **`RecordsDropped`** operational event fires through the notifier seam (Slack/email/PagerDuty) when a pipeline is dropping, at most once per pipeline per 5-minute window, so you're alerted rather than having to go looking.

Per-process by design (each process reports the records it itself dropped), and idle — no database work when nothing has been dropped.

### Metrics retention tiers

Warp's time-series metrics — per-type/handler execution, queue-wait, adapter/endpoint call stats, browser events, error-group trends — used to be kept at a single hourly resolution and then **deleted** after 7 days. This release turns that into a real multi-resolution store: recent data at **5-minute** resolution, rolled up to **hourly** then **daily** as it ages, with old buckets **summed into the coarser tier before deletion** instead of dropped.

- **Fine → hourly → daily rollup.** The write path emits at the fine (5-min) tier — the same number of counter rows, just a finer stamp. A new `StatisticRollup` server task sums 5-min buckets into their hour (after 6h), hourly into their day (after 7d), and prunes daily (after 90d). It sums-then-deletes inside its lock transaction, so a crash can't double-count, and it **replaces** the old delete-only hourly prune.
- **Explicit tiers.** Keys carry an `m5` / `h1` / `d1` marker (`…:hist:{token}:m5:{stamp}`), with a matching `pcth` latency-histogram so windowed percentiles are possible. Lifetime totals and the all-time `pct` histogram are untouched.
- **Bounded storage, coarse long history.** Each dimension keeps a fixed number of buckets instead of accumulating — and old history is *retained* at daily resolution rather than lost. `DailyStatisticsRetention = null` keeps daily forever.
- **One family-agnostic implementation.** The rollup keys purely on the trailing tier suffix, so it downsamples `jobstat`, `qwait`, adapter, endpoint, client-event, and error-group trends alike — and **migrates every pre-existing hourly row** to the new scheme automatically. The dashboard's counters/stats-history graphs read across the tiers unchanged.

```csharp
services.AddWarp<AppDb>(opt =>
{
    opt.UsePostgreSql();
    opt.FineResolutionMinutes = 5;                          // fine bucket width (must be >= 1)
    opt.FineResolutionRetention = TimeSpan.FromHours(6);    // fine → hourly age
    opt.HourlyStatisticsRetention = TimeSpan.FromDays(7);   // hourly → daily age (was a delete age pre-3.10)
    opt.DailyStatisticsRetention = TimeSpan.FromDays(90);   // daily prune; null keeps daily forever
});
```

This is the storage foundation the **SLO / error-budget** work in this release builds on — 5-minute resolution is what fast-burn alerting needs. See **[Metrics retention tiers](./features/metrics-retention.md)**.

> Migration note: the retention tiers are **additive with no migration** — all tiers are new rows in the existing `Statistic`/`Counter` tables, and legacy hourly keys migrate to the tiered scheme automatically on the rollup's normal schedule.

### SLO / error budget

Warp already folds every health signal it needs into durable aggregates — execution, queue-wait, backlog, deadline attainment. This release turns those into **promises**: declare an objective (a target over a rolling window) and Warp continuously computes attainment, the remaining **error budget**, and a multi-window **burn rate**, surfaces them on the dashboard, and alerts on a burn — all off the worker hot path.

- **Five objective kinds** — success-rate and deadline-attainment (windowed error budget), execution/queue-wait latency percentiles (windowed via the new `pcth` histogram), and backlog depth (current gauge). Scoped per job-type / queue, optionally per executor application.
- **Real fast-burn** — the short window is the objective's window ÷ 12 floored to 5 minutes, and it's genuinely populated because it reads the **5-minute fine tier** from the metrics retention tiers. Every objective reports a fast (recent) and a slow (full-window) burn rate.
- **Off the hot path** — a periodic `SloEvaluator` reads the already-folded aggregates and upserts one status row per objective; the only new hot-path write is the deadline-miss counter (§8.7) at finalization.
- **Alerting** — a healthy→breaching edge fires `WarpEventType.SloBreached` through the notifier seam (`BacklogBreached` for depth objectives), once per edge, suppressed while acknowledged. Warp raises the event; the host routes it.
- **Seed + edit** — define objectives in code with `opt.AddSlo(o => o.AddObjective(...))` (insert-if-absent, DB wins) or manage them in the dashboard **SLOs** page; the read/command services are registered by `AddWarp`, so a dashboard-only host serves them.

```csharp
services.AddWarpServer<AppDb>(opt =>
{
    opt.UsePostgreSql();
    opt.AddSlo(o =>
    {
        o.AddObjective(SloKind.SuccessRate, "MyApp.Jobs.SendEmail", target: 0.995);
        o.AddObjective(SloKind.QueueWaitLatency, "default", target: 30_000, percentile: 95);
    });
});
```

See **[SLO / error budget](./features/slo-error-budget.md)**.

> Migration note: the SLO half adds two tables (`slo_definition`, `slo_evaluation`) — a standard `dotnet ef migrations add` / `database update`. Disable with `SloEvaluationInterval = null` or by not calling `AddSlo()`.

## 3.9.0

*2026-07-28*

Additive minor release — no breaking API changes. Two new tables (`error_group`, `error_occurrence`), picked up by a standard `dotnet ef migrations add` / `database update`. Always on unless you disable it.

### Error grouping — Issues

Warp already persists every error signal it produces — failed-job exceptions in `JobLog`, 5xx in `EndpointCallLog`, failed outbound calls in `AdapterCallLog`, browser errors in `ClientEventLog`. This release folds all four into **issues**: one durable row per real problem, not per occurrence, with a count, first/last-seen, a trend, a sample, and an `Unresolved / Resolved / Ignored` lifecycle. Sentry-lite, on the same lossy-fold → durable `Counter` pipeline as everything else — so the trends **survive raw-row cleanup**.

It's an **always-on Core feature** (no addon to enable). Set `ErrorGroupingInterval = null` to turn it off.

```csharp
services.AddWarp<AppDb>(opt =>
{
    opt.UsePostgreSql();
    opt.ErrorGroupingInterval = TimeSpan.FromSeconds(15);   // default; null disables grouping
});
```

- **Zero hot-path cost**: instead of scanning (and indexing) the hot log tables, each source appends one `ErrorOccurrence` inbox row — jobs inside the finalization save they were already doing, endpoint/adapter/client in their existing paths — and a new `ErrorGroupAggregator` server task **drains-and-deletes** the inbox off the hot path, computes the fingerprint, and upserts the group. No fingerprint CPU on the worker, no new index.
- **Fingerprint**: `hash(source + exception-type + locus)`, with the message **normalized out of identity** (digits/GUIDs/hex/quotes → placeholders) so message-varying errors group and the normalized message is a PII-safe title. Fine-grained — stack-bearing sources group on the **top in-app stack frame**, so two bugs in one handler are two issues.
- **Broader than exceptions**: jobs count **every** attempt (retry + terminal), so flaky handlers show up; endpoint **4xx** become `status + route` groups (default-filtered, kept off the reliability SLI); adapters count `Failed` only.
- **Lifecycle**: a new issue gets a UI badge (not an alert); Resolve/Ignore via `IErrorGroupCommandService.SetStatus`; a resolved issue re-opens only on a genuinely new occurrence and fires a `WarpEventType.IssueRegressed` operational event through the notifier seam. Ignored never auto-re-opens.
- **Bounded + durable**: raw groups are trimmed on age and count; the hourly trend folds into `Counter → Statistic` and survives. A `MaxDistinctErrorGroups` cap (2000/source) collapses overflow into `{other}` — important for the public client ingest source.
- **Sink- and app-aware**: `Otel`-only sources write no rows (so no issues from them, jobs excepted); issues are attributed to the **executor** application under a disjoint `errorgroup` counter namespace.
- **Built to diagnose**: the fingerprint **unwraps** reflection/aggregate wrappers, so the title shows the real `TimeoutException`, not the `TargetInvocationException` the mediator throws — and skips JIT reflection stubs so one handler stays one issue. Each group records the **version it was first seen in and the latest still producing it** (plus environment) to tie an issue to a deploy, and keeps a rolling **recent-occurrences timeline** (message + version + a per-occurrence trace link) so one issue becomes a walkable list of concrete failures. Culprits are the short type name, not the assembly-qualified string.

A new always-shown **Issues** dashboard page (`/issues` + `/issues/:fingerprint`) surfaces the grouped list with source badges, trend sparklines, and Resolve/Ignore, and the detail links out to the unified trace view (per occurrence) and to the failed jobs behind a job issue. `GET {prefix}/api/issues`, `GET .../issues/{fingerprint}`, `POST .../issues/{fingerprint}/status` serve the same data in any `AddWarp` process. See **[Error grouping / Issues](./features/error-grouping.md)**.

> Migration note: 3.9.0 is **additive** — two new tables (`error_group`, `error_occurrence`), picked up by a standard `dotnet ef migrations add` / `database update`. Set `ErrorGroupingInterval = null` to disable the feature entirely.

## 3.8.0

*2026-07-26*

Additive minor release — no breaking API changes. One new table (`client_event_log`), picked up by a standard `dotnet ef migrations add` / `database update`. No behavior change unless you opt in.

### Client (frontend) observability

The browser-side complement to Warp's server-side observability (adapters, endpoints, jobs): a public **ingest endpoint** your frontend posts to, so unhandled **errors**, **Core Web Vitals**, explicit **logs**, and custom **events** land in Warp attributed to an application — Sentry-lite, on the same lossy-ingest → flusher → rows + durable `Counter` fold pipeline as everything else.

```csharp
services.AddWarp<AppDb>(opt =>
{
    opt.UsePostgreSql();
    opt.AddClientObservability(o =>
    {
        o.AddIngestKey("shop-web", "pk_live_abc123");   // public write-only DSN key -> trusted app name
        o.AllowedOrigins.Add("https://shop.example.com");
    });
});

app.UseRouting();
app.MapWarpClientObservability();   // POST /warp/ingest  +  GET /warp/ingest/client.js (shipped script)
```

Warp ships the browser script (served at `{ingestPath}/client.js`): include it with a `data-key` and it auto-captures errors + web vitals, keeps a breadcrumb trail, and exposes `window.warp.log(...)` / `window.warp.track(...)`, batching via `fetch(keepalive)` / `sendBeacon`.

- **Public endpoint, guarded**: a public write-only **DSN key** maps to a trusted application name (unknown → 401), a CORS **origin allowlist** (else 403), an **in-memory** per-key rate limit (never touches the DB on the request path), and hard size/batch caps. Recording is lossy — the browser is never blocked.
- **Never inflates the DB**: raw rows are trimmed on **age and count**; error/vital/event **trends** fold into `Counter → Statistic` and **survive raw-row cleanup**, with browser-controlled names collapsed to `{other}` beyond a cardinality cap. Web vitals report **p75** (Google's Core-Web-Vitals percentile).
- **PII-aware** (§1.2): caller IP off by default; property maps redacted + truncated; consent is the host's responsibility.
- **Sink-respecting**: `Database` (default) / `Both` write rows; `Otel` skips the DB, the `warp.client.*` meters carry it.
- **Unified client↔server session timeline**: the shipped script propagates a W3C `traceparent` (per-request trace id) *and* `baggage: session.id=…` (the OTel session id) on same-origin calls. The API stamps the session onto `EndpointCallLog` plus a `session.id` span attribute on the request span. The dashboard's **session timeline** merges a browser session's client events with the server endpoint calls they triggered, in order, with drill-down into the unified trace view. One session, client and server, on one page — and it lights up in any OTel backend too.

A new always-shown-when-enabled **Client** dashboard page surfaces error rate, Core Web Vitals (colored by Google good/needs-improvement/poor), top errors, and a filterable event stream. See **[Client observability](./features/client-observability.md)**.

### Unified trace view

A single screen showing **everything that happened for one trace** — the browser request, the server endpoint it hit, the jobs that endpoint spawned, and the outbound adapter calls those jobs made — built entirely from rows Warp **already** persists. The insight: a `Job`, an `EndpointCallLog`, an `AdapterCallLog`, and a client `Request` event are each already a span (they all carry a trace id, a timestamp, and a duration). So the trace view **unions those existing rows on their shared trace id** — no new span table, no collector, nothing added to the worker hot path.

The `/trace/{traceId}` page now renders a time-ordered **waterfall** (client → server → jobs → outbound, on one shared axis, with source badges, error highlighting, and per-span drill-down) **above** the existing job-DAG graph. It's reachable from the session timeline and from any job / endpoint / adapter detail. Save the whole picture locally for a near-Jaeger experience on your own database; switch to an external collector once your data outgrows it (Warp's OTel spans are always emitting). Served by `GET {prefix}/api/traces/{traceId}`.

> Migration note: 3.8.0 adds `client_event_log` plus one nullable column (`EndpointCallLog.Session`) — additive, picked up by a standard `dotnet ef migrations add` / `database update`.

## 3.7.0

*2026-07-26*

Additive minor release — no breaking API changes, no schema change. Warp now tracks **queue-wait latency** and **backlog depth** — the two health signals a job system needs beyond per-job execution time.

### Queue metrics — queue-wait & backlog

Warp already tracked how long jobs *run*; it now tracks how long they *wait*. **Queue-wait latency** is measured at the worker claim site (`claim − ScheduleTime`) as a single Counter write batched into the "Processing" `JobLog` save — the hot path gains no query and no extra round-trip. **Backlog depth + oldest-age** per queue are sampled off the hot path by a new `BacklogSampler` server task (one grouped query, served by the existing `(Kind, CurrentState, Queue, ScheduleTime)` index — no new index). Both are **always on**, no addon opt-in.

New always-on meters `warp.job.queue.wait` (histogram), `warp.job.queue.depth` and `warp.job.queue.oldest_age_seconds` (observable gauges), tagged `queue` + `application` (when set). Queue-wait folds a Counter→Statistic duration-sum + latency histogram, so **avg / p95 / p99 survive raw-row cleanup**. Respects `JobMetricsSink` (`Otel` skips the Counter writes; the meter still carries the data). Sliced by executor application under a disjoint counter-key namespace when `ApplicationName` is set.

A new always-shown **Queues** dashboard page surfaces per-queue backlog depth, oldest-age, and queue-wait percentiles; `GET {prefix}/api/queues/metrics` serves the same data in any `AddWarp` process.

```csharp
services.AddWarp<AppDb>(opt =>
{
    opt.UsePostgreSql();
    opt.BacklogSampleInterval = TimeSpan.FromSeconds(15);   // default; the only knob
});
```

See **[Queue metrics](./features/queue-metrics.md)**.

## 3.6.0

*2026-07-26*

Additive minor release — no breaking API changes. A new operational-event notifier seam, plus three fixes (one a 3.5.0 regression). No schema change.

### Operational notifications — `IWarpNotifier`

Warp hands operational events it already detects — a webhook delivery exhausting, a saga force-completed, an application instance or worker server going down — to a host-implemented `IWarpNotifier`, **post-commit** and as a **redaction-safe snapshot** (identity/metadata only, never a payload body). The host decides what to do (Teams, Slack, email, page, log); Warp ships **no** channel integrations — it's a pure seam, the mirror of `IWebhookDeliveryExhaustedHandler`. Alerting (Warp telling *you* something is wrong) is distinct from webhooks (your app telling *external subscribers* about domain events).

```csharp
services.AddWarp<AppDb>(opt =>
{
    opt.UsePostgreSql();
    opt.AddNotifier<TeamsNotifier>();   // several coexist; each receives every event; none registered ⇒ inert
});
```

Events are **not persisted** (no notification outbox) — delivery to your sink is best-effort (dropped on a crash in the commit→dispatch window, or if the notifier is down); only `WebhookDeliveryExhausted` is re-emitted on the executor's crash-recovery. A new default-no-op `IServerTask.OnCommittedAsync(ct)` hook lets server tasks dispatch strictly post-commit. See **[Operational notifications](./features/operational-notifications.md)**.

### Fixed

- **`UsePostgreSql()` now prefers a DI-registered `NpgsqlDataSource`.** When the DbContext carries only a connection string but an `NpgsqlDataSource` is registered in DI (Aspire `AddNpgsqlDataSource` / `AddAzureNpgsqlDataSource`, AWS RDS IAM, Cloud SQL, client-cert setups), Warp's lock / semaphore / notification / server-context connections now inherit it instead of silently opening from a raw connection string that drops those auth + SSL settings.
- **Webhook executor no longer breaks `dotnet ef` / ASP.NET Core startup in Development (3.5.0 regression).** `AddWarp` wires the always-on webhook executor, which resolves `IHttpClientFactory`; Core now calls `AddHttpClient` so a plain `AddWarp` process (no `AddAdapters`) passes `ValidateOnBuild` instead of failing to construct the handler.
- **`IWarpNotificationTransport.ListenerReady` is now a default interface member.** It was added as a *required* member in an earlier release (source-breaking for custom transports); it now defaults to `Task.CompletedTask`, so an external transport implementation compiles without it. The built-in Postgres / SQL Server transports keep their real readiness signal.

## 3.5.0

*2026-07-25*

Additive minor release — no breaking API changes, no schema change. Everything Warp records per call (adapter calls, inbound endpoint requests) and per job (execution metrics) can now be routed to your **database** (default), to **OpenTelemetry**, or to **both** — so a high-throughput deployment can take the diagnostics firehose off its database.

### Observability sinks — `RecordingSink`

`RecordingSink { Database = 1, Otel = 2, Both = 3 }` (`Warp.Core.Observability`), default `Database` ⇒ byte-for-byte current behavior.

- **`opt.AddAdapters(o => o.Sink = …)`** and **`opt.AddEndpointObservability(o => o.Sink = …)`** — `Database` writes call-log rows + `Counter`→`Statistic` aggregates (unchanged); `Otel` writes **neither** and instead enriches the span Warp already emits with the captured detail as span attributes (`warp.adapter.*` on the outbound `Client` span, `warp.endpoint.*` on the ambient inbound request span); `Both` does both.
- **`WarpConfiguration.JobMetricsSink`** — the equivalent switch for per-job-type/handler execution metrics: `Otel` skips their `jobstat` `Counter` writes on the finalization path (the perf win), since the meters carry the same data.
- **Meters added / extended** (always-on, null-listener, low-cardinality tags): `warp.endpoint.calls` / `warp.endpoint.duration`, `warp.job.execution.total` / `warp.job.execution.duration`, and an `application` tag on `warp.adapter.*`.

Detail rides the span, **not** a parallel log stream: span attributes are set only when the span is sampled in (`Activity.IsAllDataRequested`), so one consistent trace-sampling decision governs the whole call, and captured payloads travel on the trace exporter only — never the `ILogger` provider chain (§1.2). The always-on meters are never sampled, so counts / error-rate / latency stay exact regardless. See **[Observability sinks](./features/observability-sinks.md)**.

## 3.4.0

*2026-07-24*

Additive minor release — no breaking API changes, no schema change. Saga ergonomics + an outbox diagnostic, from a saga-feature trial.

- **`SagaTestHarness<TContext>`** (`Warp.Core.Sagas.Testing`) — exercise saga correlation / create / converge / complete / dead-letter / timeout **without a worker**, in the fast test tier (SQLite in-memory). `DispatchAsync` → `SagaDispatchOutcome`; `GetSagaAsync` / `CountAsync` for assertions.
- **`InProcessLockProvider` + `opt.UseInProcessLock()`** — a single-process `IWarpLockProvider` that satisfies `AddSagas()` without a DB provider (tests + single-process / mediator-only hosts; not for multi-server).
- **`WarpConfiguration.WarnOnUnsavedStagedJobs`** (default on) — Publisher / BatchPublisher warn when a scope stages jobs via `IPublisher` but ends without `SaveChangesAsync` (the silent outbox footgun). Skips worker handler scopes; count-only; never throws.

## 3.3.0

*2026-07-24*

Additive minor release. Multi-application observability for **shared-database** deployments — opt-in via `WarpConfiguration.ApplicationName` (null ⇒ unchanged behavior). Distinguish and filter by which application created a job, made an adapter call, owns an endpoint, or sent a webhook, and see every process (server or not) in one **Applications** view — without changing execution or routing.

:::warning Schema change — generate a migration
This release adds two tables (`application_instance`, `application_instance_log`) and seven nullable columns. It is **100% additive** — no drops or renames — picked up by the standard `dotnet ef migrations add / database update`. Legacy rows read as "(unassigned)"; rolling-deploy safe (old processes ignore the new columns/tables).
:::

- **Application registry:** `ApplicationInstance` (non-server processes, via a lightweight `AddWarp` heartbeat host) + `Application` / `Version` / `Environment` on `Server`. Unified dashboard `InstanceView` (servers ∪ non-server instances) — the renamed **Applications** page.
- **Provenance:** nullable `Application` on `Job` (the publishing app, preserved on requeue), `AdapterCallLog`, `EndpointCallLog`, `WebhookDelivery`; a global `application` filter on Jobs / Adapters / Endpoints.
- **Per-app metrics:** adapter + endpoint metrics under disjoint `adapter-app` / `endpoint-app` counter namespaces, and per-job-**type** + per-**handler** execution metrics attributed to the **executor** app — all fold to `Statistic` and survive raw-row cleanup.
- **DB-push listener moved to `Warp.Core`** (via the `IDispatcherWake` seam) so non-server dashboard / publisher processes receive cross-process events and drive realtime push.
- `warp.application` OTel span attribute; `IApplicationQueryService` + `/api/applications*`. See **[Applications](./features/applications.md)**.

## 3.2.0

*2026-07-22*

Additive minor release — no breaking API changes. Three new observability surfaces turn Warp into a one-stop shop for HTTP traffic in both directions, plus durable outbound webhook delivery. Ships two new packages (`Moberg.Warp.Adapters.Http`, `Moberg.Warp.Adapters.Refit`); inbound endpoint observability lands inside the existing `Moberg.Warp.Http`, and durable webhook delivery is built into `Moberg.Warp.Core` (always on, no opt-in). Adds new dashboard sections.

:::warning Schema change — generate a migration
This release adds new tables (`AdapterDefinition`, `AdapterCallLog`, `WebhookDelivery`, `EndpointCallLog`). They are added to the model **unconditionally** (so the migration story doesn't depend on which hosts opt into which feature) and sit empty until a feature is enabled. Run `dotnet ef migrations add` against your `DbContext` and apply it when upgrading from 3.1.x.
:::

### Outbound adapters — observe the calls you make to other services

`Warp.Adapters` makes every call your app *makes to* an external dependency (a vendor API, a SOAP service, a payment gateway) first-class: named, timed, telemetered, and captured on failure. The protocol-agnostic core (`Warp.Core.Adapters`) exposes a call-scope API usable for any transport; `Warp.Adapters.Http` auto-scopes `IHttpClientFactory` clients via a `DelegatingHandler`, and `Warp.Adapters.Refit` derives operation names from Refit interfaces.

**Telemetry is unconditional** (null-listener, zero cost without a listener) — every completed call emits a `Client` Activity plus `warp.adapter.*` meters. **Only DB recording is gated by `opt.AddAdapters()`**, which wires the bounded, lossy call-log channel + flusher. The differentiator is a **cluster-shared rate limiter** (DB-backed token leasing on `RateLimitBucket`) that per-process Polly cannot provide — same adapter name across the fleet shares one limit, first-writer-wins on the persisted policy.

```csharp
services.AddWarp<AppDb>(opt =>
{
    opt.AddAdapters();
    opt.AddAdapter("acme-payments", a =>
    {
        a.Recording.CaptureResponseBodies = CaptureMode.OnFailure;
        a.UseSharedRateLimit(perSeconds: 1, limit: 10);
    });
});
```

Observe-first is the recommended rollout: register with no resilience/rate-limit policy (the passive observing handler alone is zero behavioural change), read the data, then add policy per adapter once it justifies the split. Dashboard at **/warp/adapters**.

### Durable webhooks — delivery as a Warp feature

Durable outbound webhook delivery is now **built into Core** (`Warp.Core.Webhooks`) and **always on** — no package, no opt-in. `AddWarp` wires the dispatcher/executor/signer and every `AddWarpServer` worker drains the `warp:webhooks` queue, so a delivery staged by any process is executed by any server (the old "server without the webhooks package silently doesn't drain" footgun is gone). The host owns subscriptions, fan-out, and payload building; Warp owns everything after `IWebhookDispatcher.SendAsync` — delivery lifecycle, retries, signing, redelivery, exhaustion, and the dashboard. **The delivery, not the job, is the state machine**: the executor always completes; webhook failures never surface as failed jobs or pollute the Jobs UI. Each attempt is recorded as an `AdapterCallLog` row through the `warp-webhooks` adapter when `AddAdapters()` is enabled (the per-attempt timeline); the delivery state, retries, and exhaustion work regardless. `opt.AddWebhooks(...)` remains only for optional config — a custom `IWebhookSigner` or an exhausted-delivery callback.

```csharp
opt.AddWebhooks(w => w.UseCustomSigner<MySigner>());
// ...later, inside your transaction:
var deliveryId = await dispatcher.SendAsync(new WebhookSend
{
    Url = endpoint.Url,
    EventType = "order.created",
    Payload = json,
    Signing = WebhookSigning.StandardWebhooks,
    Secret = endpoint.Secret,
    RetrySchedule = null, // built-in [1m, 10m, 1h, 6h]; [] = single attempt
});
```

Retries ride `ScheduledJobActivation` (no timers/scans) — a backoff, not a precision scheduler. Signing supports `StandardWebhooks` (HMAC-SHA256) or a custom `IWebhookSigner`; declaring custom signing without a signer throws at `AddWebhooks` time. Secrets are self-contained on the row but redacted on every read surface. A settled delivery can be redelivered via `IWebhookCommandService.Redeliver`. Requires a worker somewhere to drain the `warp:webhooks` queue. Dashboard at **/warp/webhooks**.

### Inbound endpoint observability — the mirror of adapters

`Warp.Http` gains `opt.AddEndpointObservability(...)` + `app.UseWarpHttpObservability()`: adapters observe calls the app *makes to* other services; this observes requests *made to* your **Warp HTTP endpoints** (handlers exposed via `MapWarpHttp`). Identity is HTTP method + route **template** (`GET /orders/{id}`) — already bounded, no path-cardinality heuristic. It captures caller IP / user-agent / authenticated user (PII, redactable), duration, status, and request/response bodies + headers on failure or always. The middleware no-ops for anything that isn't a Warp endpoint.

```csharp
opt.AddEndpointObservability(o =>
{
    o.CaptureResponseBodies = CaptureMode.OnFailure;
    o.GroupSelector = ctx => ctx.Request.Headers["X-Client"].FirstOrDefault();
});
// after UseRouting():
app.UseWarpHttpObservability();
```

4xx is counted as success (client error, keeps the server-health error rate meaningful); `Failed` is reserved for 5xx or an unhandled exception. Dashboard at **/warp/endpoints**.

### Log retention is now age *and* count

Adapter call logs, webhook deliveries, and endpoint call logs are each cleaned on **both** a time cap and a row-count cap, whichever trims first. New `int?` count caps on `WarpConfiguration` (`AdapterCallLogRetentionCount`, `WebhookDeliveryRetentionCount`, `EndpointCallLogRetentionCount`, all null = off) sit beside the existing `*Retention` `TimeSpan`s; adapters also take a per-adapter `WarpAdapterOptions.CallLogRetentionCount` override. Webhook count-trim excludes `Pending` (live work).

### Aggregate metrics survive log deletion

Counts, error rate, **and average latency** now come from `Counter`→`Statistic` aggregates (a per-dimension duration-sum counter backs the average), so they persist after the raw call-log rows are swept. Only last-failure timestamps and recent-call lists read raw rows and degrade gracefully to empty/null once logs are deleted.

### Performance hardening

- **Counter aggregation** drains in bounded id-ordered batches with a single `Statistic` batch-load per batch, instead of materialising the entire `Counter` table and doing a per-key lookup — bounds memory and round-trips under a hot-adapter write backlog.
- **Body capture** reads only a bounded prefix rather than materialising a whole response/request body into a string just to truncate it.
- **Detail pages** scope their stat load to the single adapter/route instead of loading every adapter's rows.
- **Capture truncation** cuts on a UTF-8 character boundary (no `U+FFFD` from a split multibyte char).
- Indexed `WebhookDelivery.CreatedAt` for the count-based cleanup sweep.

### Token-bucket rate limiting

`RateLimitStyle` gains a third shape, `TokenBucket`, alongside `Fixed` and `Sliding`. Where the other two enforce a **ceiling** (reject/defer once the count is hit in a window), token-bucket enforces a **steady rate**: tokens refill continuously at `count / perSeconds` per second up to a burst capacity of `count`, and each start consumes one. A fresh key starts full; when empty, a `Wait`-mode start is rescheduled to the moment the next token refills, so a backlog trickles out at the refill rate instead of releasing a full window at once. Use it against a downstream that wants smooth traffic rather than a hard cap. Reschedules ride `ScheduledJobActivation` like the other styles (DB push does not accelerate `Wait`). See [Rate Limiting](/docs/features/rate-limit).

### Dashboard quality-of-life

- **Cancel a batch from the dashboard** — a batch detail page can cancel the whole batch in one action. Cancellation is transitive: every non-terminal descendant (children *and* pending continuations reached through already-finished children) is gracefully cancelled; terminal jobs are left untouched.
- **Job detail leads with the type, not the GUID** — the detail page headline is now the job type (a clickable link to that type's list), with the id demoted to a subline. The job list and detail surface addon-metadata chips (`[Mutex]`, `[Retry]`, `[Timeout]`, …) so you can see a job's applied policies at a glance.
- **Jobs-by-type list** — click a job type anywhere it appears to get a filtered list of every job of that type, with an optional state filter.
- **Large trace fan-outs collapse** — a message/batch that spawns hundreds of children no longer renders an unusable trace graph; child slots are capped with a "+N more" affordance.
- **Host-configurable branding** — `UseWarpUI` accepts an instance name, logo URL, and a portal back-link (label + URL) so a shared dashboard can identify which environment it is and link back to your own portal. See [Dashboard Overview](/docs/ui/overview).

### Bug fixes

- **Addon attributes on handler classes are rejected at startup** — `[Timeout]`, `[Mutex]`, `[Semaphore]`, and `[RateLimit]` belong on the request/job type, not the handler implementation (the pipeline reads them off the request). Misplacing one used to silently no-op; `AddWarp` now throws a clear startup error pointing at the offending handler. Self-handling jobs (where the request *is* the handler) are exempt.
- **Retry no longer shadows the `[Retry]` attribute** — `RetryPublishBehavior` stamped the global default retry policy into metadata even when the job type carried its own `[Retry]` attribute, masking the per-type policy. It now stamps the type's attribute when present and falls back to the default otherwise.
- **Child jobs no longer inherit parent metadata** — spawned children started with a copy of the parent's metadata (addon keys, retry policy), causing unintended policy bleed. Children start clean.
- **npm supply chain** — cleared all 37 npm Dependabot alerts across `src/ui` and `website` via transitive lockfile bumps.

## 3.1.0

*2026-06-29*

Additive minor release — no breaking changes, no schema changes, drop-in from 3.0.x. Headlined by native binary I/O in `Warp.Http`: file uploads and file/stream responses now flow through ordinary `[WarpHttp*]` handlers instead of forcing you to hand-write a Minimal API endpoint. Also folds in the `dotnet ef` design-time fix for the 3.0 server context.

### Binary HTTP — file uploads and file/stream responses

`Warp.Http` endpoints used to be JSON-only: requests bound from route/query/header/JSON-body, and every response was JSON-serialized. Binary payloads — uploading a file, streaming a download, returning a bodyless `404` — didn't fit the envelope, so you had to drop to a hand-written `MapPost`/`MapGet`. Both directions are now first-class on a `[WarpHttp*]` handler.

**Responses — return `IResult`.** A handler whose response type is `Microsoft.AspNetCore.Http.IResult` owns the full HTTP response; the JSON writer detects it and skips serialization. Use the built-in `Results.Stream` / `Results.File` / `Results.Bytes` / `Results.NotFound` / `Results.Ok`:

```csharp
public sealed record GetAvatar([FromRoute] long Id) : IRequest<IResult>;

[WarpHttpGet("/api/users/{id}/avatar")]
public sealed class GetAvatarHandler(IBlobStore blobs) : IRequestHandler<GetAvatar, IResult>
{
    public async Task<IResult> HandleAsync(GetAvatar request, CancellationToken ct)
    {
        var blob = await blobs.GetAsync(request.Id, ct);

        return blob is null ? Results.NotFound() : Results.Stream(blob.Content, blob.ContentType);
    }
}
```

**Requests — bind from a multipart form.** Request members typed `IFormFile`, `IFormFileCollection`, or `IFormCollection` (or carrying `[FromForm]`) bind from `multipart/form-data` alongside any `[FromRoute]`/`[FromQuery]` members. The generated endpoint advertises `multipart/form-data` and disables antiforgery, matching a hand-written `MapPost` with an `IFormFile` parameter:

```csharp
public sealed record UploadDoc([FromRoute] long FolderId, IFormFile File, [FromForm] string Title)
    : IRequest<IResult>;

[WarpHttpPost("/api/folders/{folderId}/docs")]
public sealed class UploadDocHandler : IRequestHandler<UploadDoc, IResult> { /* … */ }
```

A request cannot bind both a multipart form and a JSON `[FromBody]` parameter — Minimal API reads the body once, so the combination is now a compile-time error, **WHTTP006**.

### `dotnet ef` no longer sees the Warp server context

The internal **Warp server context** introduced in 3.0 is now hidden from EF Core's design-time tooling. Because `AddWarpServer` registers `WarpServerContext<TContext>` in DI, `dotnet ef` discovered it alongside your own `DbContext` and failed every command with *"More than one DbContext was found"* unless you passed `--context YourDbContext`. The server context is a runtime-only implementation detail you can never migrate, so the tooling should never have offered it as a target. `AddWarpServer` now strips the design-time discovery hook (the non-generic `DbContextOptions` forwarder EF enumerates), so `dotnet ef` resolves cleanly to your context with no `--context` flag. Runtime resolution of the server context is unchanged.

## 3.0.0

*2026-06-26*

Isolates Warp's autonomous server-internal database work onto a dedicated, internal **Warp server context** so it stops polluting your application's command logs. No consumer API changes — you still call `AddWarp` / `AddWarpServer` exactly as before — but the internal blast radius (the worker, every server task, and the background-service host now resolve a separate context) makes this a major.

### Quiet server logging

All of Warp's server-internal database work — the **job worker** (fetch/execute/complete), `Heartbeat`, `Orchestrator`, `MessageRouter`, `ScheduledJobActivation`, `RecurringJobScheduler`, `StaleJobRecovery`, `CounterAggregator`, the cleanups, and the background-service host — now runs on an internal server context whose EF Core command logging is demoted to `Debug`. At a default `Information` level, **that SQL no longer appears in your logs, and your own `DbContext`'s command logging is unaffected.** Zero configuration; opt back in with `opt.EnableServerCommandLogging = true`.

The server context is a runtime-only mirror of the Warp model: it maps to the same physical tables as your `DbContext` (column/table names pulled from your model, so naming conventions are honoured) and is excluded from migrations — your context remains the schema owner. It exists only under `AddWarpServer`, with its connection supplied by the provider (`UsePostgreSql` / `UseSqlServer`).

The only DB work left on your `DbContext` is the **outbox** (the publisher staging job rows in your transaction) and your **handler's own code** — exactly what you'd want logged. See [EF Core Integration → Server-internal logging](./operations/ef-core-integration.md#server-internal-logging--the-warp-server-context).

### Cross-assembly handler discovery (source generator)

The mediator source generator now registers a handler whose message/request **contract lives in a referenced assembly** — the shared-contract layout where contracts sit in one project (e.g. `Contracts.dll`) and handlers in another (a separate worker or API). Discovery used to be driven only by message/request types *declared in the compilation being built*, so a handler such as `FooHandler : IMessageHandler<FooMessage>` whose `FooMessage` came from a referenced assembly was never DI-registered or routed, and the worker failed the job with `No handlers registered for message type FooMessage`. It only worked when the message and its handler lived in the same assembly.

Discovery is now **handler-driven**: each assembly registers the handlers it declares, keyed by the message/request type read off the handler interface — regardless of where that type is declared. Only locally-declared handlers are registered (each assembly emits its own registration), so co-located handlers still register exactly once and a referenced assembly's handlers are not re-registered by a consumer. Applies symmetrically to `IJobHandler`, `IMessageHandler`, `IRequestHandler`, and `IStreamRequestHandler`.

Generated mediator member names (wrapper fields, dispatch methods) are now derived from the fully-qualified type name, so two request/job types that share a simple name across different namespaces or assemblies no longer produce colliding members.

## 2.1.0

*2026-06-25*

Additive minor release — no breaking changes, no schema changes, drop-in from 2.0.x. Smooths over several EF-integration and testability pain points: an explicit way to contribute Warp's model, a fail-fast check when it's missing, an in-memory publisher for tests, and an HTTP binding fix for bodyless commands. Also rolls up the dependency/security fixes that were staged after 2.0.1.

### `ApplyWarpModel` — explicit, design-time-friendly model registration

`AddWarp` still wires Warp's EF model onto your `DbContext` automatically, but that wiring lives on `DbContextOptions` — so a separate migrator or design-time host that builds the context differently can end up with a model that doesn't match your runtime, producing empty or drifting migrations. You can now declare Warp's model in your context's own `OnModelCreating`:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);
    modelBuilder.ApplyWarpModel(schema: "warp"); // null = database default schema
}
```

When the model is declared this way, every host — your API, your workers, a standalone migrator, `dotnet ef` — sees an identical model with no `DbContextOptions` divergence. `ApplyWarpModel` is idempotent, so it composes safely with the implicit `AddWarp` wiring (whichever runs first wins; the other is a no-op). This is now the recommended pattern for dedicated migration / Aspire-style hosts. See [EF Core Integration](./operations/ef-core-integration.md).

### Fail-fast when the Warp model is missing

Publishing against a context that never received Warp's model used to fail deep inside EF Core with the cryptic `Cannot create a DbSet for 'Job'`. Warp now validates the model **at host startup** (a hosted service that runs before any worker, server task, or request) and throws a clear, actionable error naming both fixes — call `AddWarp<TContext>` or `ApplyWarpModel`. A matching guard on the `Publisher` / `BatchPublisher` constructors backstops non-hosted (raw `ServiceProvider`) usage.

### `InMemoryPublisher` — test publish-side code without a database

Handlers and application code that call `IPublisher` (or `IBatchPublisher`) are now unit-testable without standing up the Warp store. `InMemoryPublisher` (in `Warp.Core.Testing`) records every publish in memory:

```csharp
using Warp.Core.Testing;

var publisher = new InMemoryPublisher();
services.AddSingleton<IPublisher>(publisher);

// … exercise the handler …

var published = publisher.Published.ShouldHaveSingleItem();
published.Payload.ShouldBeOfType<SendEmailJob>();
```

It implements both `IPublisher` and `IBatchPublisher`; recorded calls land in `Published` and `Batches`, and `SaveChangesCount` tracks `SaveChangesAsync`. No `Job` DbSet, no migrations, no container required.

### Bodyless `POST` / `PUT` / `PATCH` commands now bind (`Moberg.Warp.Http`)

A handler whose request type has no body members — an empty record or a command bound entirely from route/query/header values (e.g. `POST /api/auth/logout`) — was classified as whole-body and bound the request as a required `[FromBody]`, so a request with no body returned **400** before the handler ran. Zero-member requests now bind via `[AsParameters]` (the same shape empty `GET` requests already use) and a bodyless request succeeds.

### Dependency & security maintenance

Rolls up the dependency bumps merged after 2.0.1, clearing all open Dependabot alerts across the bundled dashboard (`Moberg.Warp.UI`) and the repo's build/docs tooling (notably `ws`, `form-data`, `react-router`, `hono`, `vite`, `launch-editor`, `shell-quote`, `joi`).

## 2.0.1

*2026-06-12*

Security maintenance release. No API changes, no schema changes — drop-in upgrade from 2.0.0. Clears all open Dependabot alerts across the bundled dashboard and the docs-site tooling.

### Dashboard dependency fixes (`Moberg.Warp.UI`)

The dashboard bundle shipped in `Moberg.Warp.UI` is rebuilt against patched front-end dependencies:

- **react-router 7.13.2 → 7.17.0** — clears four advisories: an unauthenticated RCE via vendored turbo-stream deserialization (`GHSA-49rj-9fvp-4h2h`, high), two denial-of-service vectors (unbounded `__manifest` path expansion `GHSA-8x6r-g9mw-2r78` and reflected single-fetch input `GHSA-rxv8-25v2-qmq8`, both high), and a protocol-relative open redirect (`GHSA-2j2x-hqr9-3h42`, medium).
- **hono → 4.12.25** (transitive) — clears four medium advisories: JWT middleware accepting any auth scheme, IPv6 deny-rule bypass, `app.mount()` prefix-stripping on percent-encoded paths, and `Set-Cookie` injection via unsanitized cookie attributes.

### Build tooling (docs site only — not shipped)

Repo-only dependency bumps in the Docusaurus docs site, with no effect on any published package:

- **shell-quote → 1.8.4** — `quote()` not escaping newlines in object `.op` values (`GHSA-w7jw-789q-3m8p`, critical).
- **joi → 18.2.1** (forced via `overrides`) — uncaught `RangeError` on deeply nested input through recursive `link()` schemas (`GHSA-q7cg-457f-vx79`, medium).

## 2.0.0

*2026-06-09*

First major after 1.0. One breaking, mechanical rename — the executing-host entry point and its config/builder types — plus a new service-only deployment mode. The upgrade is a find-and-replace; see the migration steps below.

### `AddWarpServer` — one entry point for the worker and background services

A process registers a **server**; the job worker is one component of it. The executing-host entry point is now `AddWarpServer<TContext>(opt => ...)`, which runs the worker by default. Call `opt.DisableWorker()` for a service-only server that runs `WarpBackgroundService` instances (and the supporting heartbeat/cleanup tasks) but processes no jobs.

```csharp
// Full server (worker runs)
builder.Services.AddWarpServer<AppDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.AddBackgroundService<EmailPump>();
});

// Service-only server (no job worker)
builder.Services.AddWarpServer<AppDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.DisableWorker();
    opt.AddBackgroundService<EmailPump>();
});
```

### Breaking changes & migration

The old worker-centric names are **removed** (not deprecated) in 2.0 — rename them:

| 1.x | 2.0 |
|---|---|
| `services.AddWarpWorker<T>(...)` | `services.AddWarpServer<T>(...)` |
| `WarpWorkerConfiguration` | `WarpServerConfiguration` |
| `WarpWorkerBuilder<T>` | `WarpServerBuilder<T>` |
| `IOptions<WarpWorkerConfiguration>` | `IOptions<WarpServerConfiguration>` |

- The `opt => ...` lambda is otherwise unchanged — config fields (`opt.WorkerCount`, `opt.Queues`, addon methods, `opt.UsePostgreSql()`, …) keep their names and behavior. `AddWarpServer()` with no `DisableWorker()` call behaves exactly like the old `AddWarpWorker()`.
- These are hard renames: references to the old names are **compile errors** in 2.0, so the compiler points you at every site to change. There is no silent runtime change.

### Service-only servers

`opt.DisableWorker()` runs a server with background services and the supporting infrastructure (registration, heartbeat, lease renewal, cleanup) but no job worker — no worker hosts, no job-orchestration server tasks, no `Worker`/`WorkerGroup` rows. A provider (`UsePostgreSql`/`UseSqlServer`) is still required.

To run a server with no job processing, call `opt.DisableWorker()` — **don't** set `opt.WorkerCount = 0` and leave the worker enabled. `AddWarpServer` **throws at registration** for that contradiction (worker enabled but zero total workers), since it would otherwise produce a server that runs job orchestration but never executes any jobs.

## 1.0.1

*2026-06-03*

Bug-fix release. No API changes, no schema changes — drop-in upgrade from 1.0.0.

### Fix: ServerCleanup foreign-key crash when reaping a stale server that runs background services

`ServerCleanup` removes servers whose heartbeat has lapsed — deleting their `BackgroundServiceInstance` and `BackgroundServiceLease` child rows and then the server row in one transaction. It locked the server rows with a mode (`FOR NO KEY UPDATE` on Postgres, `UPDLOCK` on SQL Server) that is *compatible* with the lock a child `INSERT` takes on its parent for the foreign-key check. So a server that had only just lapsed — a GC pause or a slow loop — but was still running its `BackgroundServiceHost` could insert a fresh `BackgroundServiceInstance` row for itself between cleanup's child-delete and its server-delete. That orphaned the foreign key and aborted cleanup with `23503` (Postgres) / `547` (SQL Server); the task then failed on every tick and stale servers were never reaped.

The cleanup lock is now exclusive (`FOR UPDATE` on Postgres, `XLOCK` on SQL Server), which conflicts with the FK-check lock: a concurrent instance insert for a server under cleanup now blocks until cleanup commits, then fails cleanly because the server is gone (a still-alive server simply re-registers on its next loop). Regression tests on both providers assert that the cleanup lock blocks the concurrent insert.

## 1.0.0

*2026-06-03*

The first stable release. Warp has been running the same unified job/message/request model across the 0.x line for months; 1.0.0 draws the line and commits to it.

### Stability commitment

From 1.0.0 onward, Warp follows [semantic versioning](https://semver.org/) as a contract, not just a number scheme:

- **Public API is stable.** The surfaces you build against — `IPublisher`, `IMediator`, `IJob` / `IMessage` / `IRequest<T>` / `IStreamRequest<T>` and their handlers, the `AddWarp` / `AddWarpWorker` builders, the addon methods (`AddRetry`, `AddConcurrency`, `AddTimeout`, `AddRateLimit`, `AddSagas`, `UseDatabasePush`, `AddDashboardPush`), and the `Warp.Http` attributes — will not break within 1.x. Additive changes only.
- **Database schema is stable.** The `warp` schema (jobs, logs, servers, counters, statistics, recurring jobs, addon tables, background-service tables) will not see breaking migrations within 1.x.
- **Breaking changes are reserved for 2.0.** Anything that would force a code or schema change waits for the next major.

Upgrading from 0.17.2 is **drop-in**: no schema migration, no required code changes for typical applications. Two things to be aware of:

- **`Warp.Core` no longer pulls in the ASP.NET Core shared framework** (see below). Worker- or publisher-only projects that used `Microsoft.AspNetCore.*` types *transitively through Warp.Core* must now add `<FrameworkReference Include="Microsoft.AspNetCore.App" />` themselves. Projects that reference `Warp.UI` / `Warp.Http`, or that are ASP.NET apps already, are unaffected.
- **A new `WHTTP005` build warning** (see below) may surface on existing `Warp.Http` GET/DELETE handlers. Builds with `TreatWarningsAsErrors` will need the one-line nullable fix it points to.

### HTTP: all handler-class attributes forwarded to endpoint metadata (#220)

`MapWarpHttp` previously forwarded only `[Authorize]` / `[AllowAnonymous]` from a handler class to its generated endpoint; every other attribute was silently dropped. Now **every** attribute on the handler class is forwarded as ASP.NET endpoint metadata (via `EndpointBuilder.WithMetadata`), excluding only Warp's own `[WarpHttp*]` routing markers. So `[EnableRateLimiting("policy")]`, `[Tags]`, `[OutputCache]`, `[ProducesResponseType]`, and any custom metadata attribute now compose with their middleware exactly as on a hand-written Minimal API endpoint.

### `Warp.Core`: dropped the ASP.NET Core framework reference (#221)

`Warp.Core` replaced its `<FrameworkReference Include="Microsoft.AspNetCore.App" />` with granular `Microsoft.Extensions.*` package references (DI, logging, options, configuration). Core, the worker, and the providers no longer drag the entire ASP.NET Core shared framework into hosts that don't need it — only `Warp.UI` and `Warp.Http` depend on ASP.NET now. See the upgrade note above for the rare case this affects.

### `Warp.Http`: the `[AsParameters]` required-query-param trap is now a build warning (#222)

On a non-body verb (GET / DELETE), `Warp.Http` binds the request via ASP.NET's `[AsParameters]`, which makes any non-nullable value-typed property a **required** query parameter and ignores its C# default. A bare request then returns 400 instead of falling back to the default — a silent, easy-to-ship bug. The source generator now emits **`WHTTP005`** for exactly this shape (non-nullable value-typed query param carrying a C# default), so it's caught at build time. Make the property nullable and apply the default in the handler. The binding docs now cover this and the mixed route + `[FromBody]` PATCH pattern, and the diagnostics table is complete (`WHTTP001`–`WHTTP005`).

### Developer experience (#222)

- **Accurate README & XML docs.** Removed stale `AddHandlers(...)` / `AddPipelineBehaviors(...)` calls from the README — handlers and pipeline behaviors are auto-registered by the source generator. Added a package-id ↔ namespace cheat-sheet (`Moberg.Warp.*` packages, `Warp.*` namespaces) and version-pinning guidance. Added XML-doc summaries to the core public surface (`IPublisher`, `IMediator`, `AddWarp`, the `Warp.Http` verb attributes) so IntelliSense shows signatures.
- **Smaller dashboard bundle.** The dashboard is now route-code-split: the initial JS payload dropped from ~510 KB to ~296 KB gzip, and the trace-graph and chart stacks load only on the routes that use them. Consolidated on a single date library.
- **Build resilience.** The embedded dashboard SPA is now collected after the build-time `npm run build`, so dependency bumps no longer break the build with a stale-resource error.

## 0.17.2

*2026-06-02*

Bug-fix release. No API changes, no schema changes — just deterministic ordering on dashboard queries.

### Fix: stable ordering for servers, server tasks, and other dashboard lists

Several dashboard query paths fetched collections without a fully-specified `ORDER BY`, so the database was free to return rows in any order. Because the UI renders rows in API order and refetches every ~10s, lists visibly reshuffled between refreshes.

- **Server tasks** (`GetServerTaskSummaries`) had no ordering at all — now ordered by task name.
- **Servers** (`GetServers`) were ordered only by `StartedTime`; servers in a cluster can share a start instant, leaving the tie unresolved. An `Id` tiebreaker now keeps the order stable. Worker groups are derived from the already-deterministic worker query (`WorkerGroupId`, then worker `Id`), so they were unaffected.
- **Failed-job type counts** (`GetFailedJobTypeCounts`) were ordered by count only; equal counts now break ties by type name.
- **Recurring jobs** (`GetRecurringJobs`) were ordered by `NextExecution` only; equal schedules now break ties by name.

## 0.17.1

*2026-05-26*

UI polish on top of 0.17.0. No API changes, no schema changes — just dashboard ergonomics.

### Fix: history-panel error rows render red end-to-end

The job-detail history panel previously colored only `EventType = "Failed"` rows red. Rows like a `Deleted` event with an attached `TimeoutException` (e.g., `TimeoutMode = Delete` capturing the timeout reason) showed a neutral card with just the inner stack-trace `<pre>` block painted red — visually confused.

The color rule now keys off `event.exception`: any history row carrying an exception payload paints the whole card red (border, background, header text). Rows without an exception fall back to the event-type-based color map. `Failed` events without an exception (a pipeline behavior that short-circuits via `Outcome.State = Failed` with no exception attached) still render red as a fallback.

### Fix: dashboard content centered on wide displays

`MainLayout`'s main content area previously had no max-width constraint — on 1920px+ displays the cards floated against the left edge with empty whitespace on the right. The `<Outlet/>` is now wrapped in `max-w-screen-2xl mx-auto` (1536px, matching the convention most modern admin dashboards converge on). Section sidebars (Jobs / Batches / Messages) stay outside the wrapper so they continue to hug the viewport edge.

### Feature: collapse/expand toggle on Payload and Metadata blocks

Job-detail Payload and Metadata blocks have a clamped height (`max-h-40`, ~160px) with overflow-scroll for big JSON documents. Useful for keeping the page navigable, but reading a multi-line metadata dict required scrolling inside a 160-pixel window.

Both blocks now have a small "Expand" toggle next to the heading that removes the clamp and switches the `<pre>` to `whitespace-pre-wrap` so the full content reads inline. "Collapse" puts it back. Default state is unchanged from 0.17.0 (clamped).

### Internal

- Marketing screenshot pipeline bumped from 1280×800 to 1920×1080 viewport with full-page capture on every entry — the recurring page's Actions column, list-page right-side controls, and job/batch detail pages were being horizontally / vertically cropped. New `22-message-detail.png` entry was missing from the suite.
- Demo `/addons` adapter reports the addon-conditional nav items (Concurrency, Rate Limits, Sagas) as off so the top nav stays compact in marketing screenshots. The dedicated pages still render via direct URL — only the nav links are suppressed.

---

## 0.17.0

*2026-05-25*

External adopter feedback drove most of this release. Addon entities are now part of the base schema (no more mirroring opt-ins across hosts), `AddWarp` fails fast on misconfigured DbContext registration, and the source generator's multi-project ergonomics are fixed. The worker's "Requeued" log row is split into discrete `Failed` + `Scheduled`/`Enqueued` rows; job list pages sort by finished time on terminal states; metadata persists enums as strings instead of integers; the dashboard's history panel reduces color noise; and destructive UI actions now go through a confirmation dialog.

### Feature: addon entities always in the schema

`opt.AddConcurrency()` / `AddCircuitBreaker()` / `AddRateLimit()` / `AddSagas()` previously contributed their EF entities (`ConcurrencyLimit`, `CircuitBreakerState`, `RateLimitBucket`, `RateLimitOverride`, `SagaState`, `SagaJobLink`) only when the host called the opt-in. In a multi-host deployment (API + Workers + BackOffice + Migrations all built from the same monorepo), every host had to declare the same addon set or the migrations job would skip tables a downstream host needed at runtime — manifesting as `relation "warp.concurrency_limit" does not exist` on the first attribute-decorated handler invocation.

`WarpModelCustomizer` now registers all six addon entities unconditionally. The opt-in methods still gate runtime behavior (pipeline behaviors, admin services, dashboard endpoints) — the schema is just always there. One migration covers every deployment shape, and operators don't need to remember which addons each host declares.

Trade-off: six tables exist in deployments that don't use the addons. Each is an empty B-tree with no inserts and one indexed `Name` / `GroupKey` column; the overhead is cosmetic. If you actively need to avoid the tables (very strict multi-tenant schema discipline), file an issue.

### Feature: `opt.ExcludeHandlersFromAssembly(...)` for multi-host solutions

The Warp source generator scans handlers transitively across project references and auto-registers everything it finds. In a single-host solution that's convenient; in a multi-host one (API + Workers built from a shared `Domain` project that transitively pulls each other's handlers) it causes scope-validation explosions — the API host can't satisfy worker-only DI dependencies on handlers it doesn't intend to invoke.

```csharp
builder.Services.AddWarp<AppDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.ExcludeHandlersFromAssembly(typeof(WorkerOnlyMarker).Assembly);
});
```

The exclusion is per-host (configured on the `AddWarp` / `AddWarpWorker` lambda) and runs after `WarpGeneratedHandlerRegistry.ApplyAll` to scrub `IRequestHandler<,>` / `IJobHandler<>` / `IMessageHandler<>` / `IStreamRequestHandler<,>` registrations whose implementation type lives in the excluded assembly. Pipeline behaviors and other DI registrations are unaffected.

### Feature: `AddWarp` fails fast on misconfigured DbContext registration

Adopters using `AddDbContextFactory<T>` (Blazor render-scoped contexts, design-time tooling) without also calling `AddDbContext<T>` previously saw silent failures — empty migrations from `dotnet ef migrations add` and runtime `Unable to resolve service for type 'AppDbContext'` errors after their handlers had already partially executed.

`AddWarp<TContext>` now checks the service collection for a Scoped `TContext` registration and throws at startup with an actionable message:

```text
AddWarp<AppDbContext>() requires AppDbContext to be registered via
services.AddDbContext<AppDbContext>(...). If you're using
AddDbContextFactory<AppDbContext>(...) (e.g. for Blazor / design-time
tooling), also call AddDbContext<AppDbContext>(...) so Warp's scoped
services can resolve the context.
```

### Feature: split `"Requeued"` worker log into `Failed` + `Scheduled`/`Enqueued`

The worker auto-retry path previously wrote one `JobLog` row with `EventType = "Requeued"` that combined the exception detail and the next-attempt timestamp. Operators on the dashboard couldn't visually separate the failure from the requeue, and the row had to render in a hybrid yellow color that didn't communicate "this attempt failed."

The worker now emits two log rows on retry-after-error:

- **`Failed`** with the exception, error message, and per-attempt duration. Renders red.
- **`Scheduled`** (or **`Enqueued`** for immediate retry) with `Message = "Retry scheduled for <ISO timestamp>"`. Renders neutral.

Single-row emission is preserved for the non-retry paths (terminal Completed/Failed/Deleted, or addon-driven requeues like Mutex Wait / Rate-Limit Wait — those still emit one row with `EventType` matching the literal state). `JobCommandService.RequeueJob` (admin "Requeue" button) and `StaleJobRecovery` (crash recovery) continue to emit `"Requeued"` — those are user-driven actions, not retry-after-failure.

### Feature: finished-time sort on terminal-state job lists

Job list pages for terminal states (Completed, Failed, Deleted, including failed-by-type) now sort by the latest `JobLog` terminal-event timestamp descending, falling back to `CreateTime` when no terminal log exists. The Completed page shows the most recently finished jobs at the top, which is what operators expect from "newest first."

Non-terminal pages (Enqueued, Processing, Scheduled, Awaiting) sort by `CreateTime` directly — there is no terminal log row to find, so the subquery would be wasted work.

A composite index on `job_log (job_id, event_type, timestamp)` is added to support the correlated subquery; the previously-existing single-column `job_log (job_id)` index is removed (the composite is a superset).

### Feature: metadata enums persisted as strings

`JobParameters().WithMutex("k", ConcurrencyMode.Skip)` previously wrote the integer enum value (`1`) into `Job.Metadata`. The dashboard rendered the raw JSON, so operators saw `"ConcurrencyMode": 1` and had to mentally map the integer back to the enum name.

`MetadataSerializer` now adds `JsonStringEnumConverter` to its options. New rows write `"ConcurrencyMode": "Skip"`; `MetadataConvert.To<TEnum>` was extended with a `string → enum` branch using `Enum.TryParse(ignoreCase: true)`. Applies to all enum metadata properties — `ConcurrencyMode`, `RateLimitMode`, `RateLimitStyle`, `TimeoutMode`, `TimeoutScope`, `CancellationMode`.

### Feature: UI confirmation dialogs on destructive actions

Delete, Requeue, Trigger, and Disable buttons across the dashboard now open a confirmation dialog before firing the mutation. Coverage:

- **Job list page**: single Delete + single Requeue + bulk Delete + bulk Requeue + by-type Delete-all + by-type Requeue-all.
- **Job detail page**: Delete, Requeue, Cancel (running job).
- **Recurring jobs list**: Trigger, Remove.
- **Recurring jobs detail**: Trigger, Delete, Disable. Enable stays immediate (harmless to re-enable).

A new `<ConfirmDialog>` component wraps `@base-ui/react`'s `AlertDialog`. The dialog states the action, the resource (job ID, count, name), and uses the destructive variant for irreversible actions (delete, disable).

### Feature: dashboard history panel — color noise reduction

The job detail page's history panel previously colored every event type (Created blue, Processing purple, Completed green, Failed red, Requeued yellow, Deleted grey). With the `Failed` + `Scheduled` split adding more rows per retry, the timeline became visually noisy.

Only two event types are now colored:

- **`Failed`** — red border + background + text.
- **`Completed`** — green border + background + text.

Everything else (`Processing`, `Created`, `Scheduled`, `Enqueued`, `Requeued`, `Deleted`) renders neutral. The exception detail still gets its dedicated red `<pre>` block on Failed rows. The policy is documented in source so future event types fall through to neutral by default.

### Feature: `Activated` → `Enqueued` rename in `ScheduledJobActivation`

The log row written when `ScheduledJobActivation` transitions a job from `Scheduled` to `Enqueued` was previously `EventType = "Activated"`. Operators reading the dashboard had to mentally translate "Activated" → "is now Enqueued."

The row is now `EventType = "Enqueued"` with message `"Enqueued from Scheduled — was scheduled at <ISO timestamp>"`. Lines up with the literal state and reduces the event-type vocabulary by one.

### Fix: source generator `WarpMediatorServiceExtensions` is now `internal`

`Warp.SourceGenerator` emits a `WarpMediatorServiceExtensions` class in every consuming project. Previously this class was `public`, which caused `CS0436` "duplicate type" warnings across project references in multi-project solutions — under `TreatWarningsAsErrors`, the build failed.

The emitted class is now `internal`. Each assembly's copy is callable from within that assembly (the `[ModuleInitializer]` wiring path is unchanged), but referenced projects don't see it. CS0436 is gone for this type.

### Fix: `JobOutcome` short-circuit documentation

`JobOutcome` has `init`-only properties; pipeline behaviors that want to short-circuit (skip retry, mark deleted) have to construct a new instance rather than mutating an existing one. The previous XML doc didn't make this clear and external adopters hit `CS8852`. The doc now includes a worked example showing the canonical pattern.

### Documentation

Three new pages under `operations/`:

- **EF Core integration** — `AddDbContext` vs `AddDbContextFactory`, design-time tooling, addon entity always-on contract, naming conventions.
- **Multi-project source generation** — CS0436 fix explained, `ExcludeHandlersFromAssembly` usage, `IJob`-as-HTTP-body rejection, instance-class requirement.
- **Migrating from Wolverine** — translation table (`InvokeAsync` → `Send`, cascade return values → explicit Publish + SaveChanges, static handlers → instance, etc.), with the auth-policy diagnostic checklist.

`llms-full.txt` updated to match — stale `services.AddWarpRetry` / `AddJobHandlers` references removed, install snippet includes the provider NuGet, addon opt-ins documented on the builder lambda parameter.

### Breaking changes

- **`"Requeued"` worker log → `Failed` + `Scheduled`/`Enqueued` split.** Code that queries `JobLog` for `EventType = "Requeued"` to count retries no longer matches the worker's emissions. Switch to counting `Scheduled` + `Enqueued` rows where `Level = "Information"` and `Message.StartsWith("Retry scheduled for")`. `JobCommandService.RequeueJob` and `StaleJobRecovery` continue to emit `"Requeued"` — those are admin / crash-recovery actions, not retry-on-failure.
- **`"Activated"` → `"Enqueued"`.** Same shape applies for any code keyed off the literal `"Activated"` event type.
- **`Job.Metadata` JSON shape: enum values are now strings.** Code that reads metadata directly via SQL (`metadata->>'ConcurrencyMode'::int` in Postgres) breaks. Reading via the EF model + `IConcurrencyMetadata` / `IRateLimitMetadata` accessors continues to work — `MetadataConvert.To<TEnum>` handles both formats.
- **`WarpMediatorServiceExtensions` is `internal`.** Code that explicitly called `Warp.Core.Handlers.Generated.WarpMediatorServiceExtensions.AddWarpMediator(services)` from a different assembly stops compiling. The cross-assembly registration path goes through `[ModuleInitializer]` + `WarpGeneratedHandlerRegistry.ApplyAll` — no direct call is needed. Intra-assembly calls (in your test fixtures) continue to work.
- **`AddWarp<T>` throws when TContext isn't registered as Scoped.** Hosts that previously got away with `AddDbContextFactory<T>` (without also calling `AddDbContext<T>`) now fail at startup with a clear error. Add `AddDbContext<T>` to the registration chain.
- **Removed: `WarpConfiguration.EntityConfigurators` is no longer used by in-tree addons.** The list itself remains as a public extension point for third-party / provider-package addons.

### Migration

Run `dotnet ef migrations add UpgradeWarp_0_17_0` and apply. The migration is mostly additive:

- `CREATE TABLE` for any addon entity your host wasn't previously opting into.
- `CREATE INDEX warp.ix_job_log_job_id_event_type_timestamp` on `job_log (job_id, event_type, timestamp)`.
- `DROP INDEX warp.ix_job_log_job_id` (the single-column index is now redundant).

On large `job_log` tables the composite index build can take a while — schedule the migration accordingly.

---

## 0.16.0

*2026-05-25*

`WarpBackgroundService` is now a base part of Warp instead of an opt-in addon, plus automatic cleanup of orphaned service definitions. Message routing is rewritten around an atomic batch-claim + single-transaction commit, and bare workers now wake on local in-process enqueues without needing `UseDatabasePush()`. Activation and routing pickup are now per-row audited in `JobLog`, the dashboard surfaces toast notifications and an error boundary, and a long-standing saga serialization hang on SQL Server is fixed.

### Feature: bare workers wake on local in-process enqueues

`WarpWorker` (non-dispatcher mode) used to sit on its exponential-backoff sleep — up to `MaxPollingInterval` (30s default, 5 min with push enabled) — between empty polls, missing in-process enqueues from `Publisher.Publish`, `MessageRouter` routing, `ScheduledJobActivation`, and handler outboxes on the same server.

Each `WarpWorker` now subscribes to a new `ServerTaskSignal.JobEnqueued` channel on `ServerTaskSignals<TContext>`. Anywhere a `Kind=Job` row commits in `Enqueued`, the local signal fires; the worker bypasses its backoff and re-polls immediately. The behaviour is independent of `UseDatabasePush()` — push remains the cross-process / cross-server wake mechanism, but same-process wakes no longer round-trip through the DB. When push is enabled, the listener fires the same signal on incoming notifications, so multi-server deployments converge on the same wake-up path.

No configuration changes are required to opt in. Tuning `MaxPollingInterval` to the long end (the default 30s, or 5min with push) is now safe — that ceiling is only ever reached when there is genuinely no work, and signal wake-up shortcuts it the moment new work appears.

### Feature: `MessageRouter` batch-claim + atomic transaction

`MessageRouter` previously locked one message row at a time and called `SaveChangesAsync` once per message inside the for-loop. With `ServerTaskBatchSize = 100` and ~10ms commit latency, the steady-state ceiling was around 100 messages/s per routing server.

The router now does:

1. One atomic `ClaimEnqueuedMessagesAsync(N)` round-trip — `UPDATE ... RETURNING` (PG) / CTE + `UPDATE ... OUTPUT INSERTED.*` (SQL Server) — flips up to `N` rows from `Enqueued → Processing` and streams them back as tracked entities.
2. Handler discovery + child-job creation in-memory across all claimed messages.
3. One `SaveChangesAsync` + commit at the end of the iteration.

All three steps run inside one explicit transaction opened in `MessageRouter.ExecuteAsync`, so a process crash anywhere mid-batch rolls everything back: messages stay `Enqueued` and the next router tick re-routes them. The new `IWarpSqlQueries<TContext>.ClaimEnqueuedMessagesAsync` replaces `LockNextEnqueuedMessageAsync` — provider implementations are required to update.

`WarpWorkerConfiguration.ServerTaskBatchSize` default is raised from `100 → 1000` to take advantage of the per-batch commit. The trade-off is multi-server fairness: a router server now holds the routing advisory lock ~10× longer per iteration (still bounded — a few hundred milliseconds in practice). Tune down if you run many routing servers against the same DB and observe one server monopolising work.

### Feature: per-row `Activated` and `Routed` `JobLog` entries

`ScheduledJobActivation` previously flipped `Scheduled → Enqueued` in one bulk `UPDATE` with no per-row audit — operators reading the dashboard saw "Requeued at X" then "Processing at Y" with a one-second gap and nothing between them. `MessageRouter` had the same shape: children got a `Created` log entry on routing, but the parent `Message` row got nothing, so per-row routing latency was invisible.

Both paths now write one `JobLog` per affected row, committed atomically with the state flip:

- **`Activated`** — written by `ScheduledJobActivation` for every job that transitions `Scheduled → Enqueued`. Message records the previous `ScheduleTime`.
- **`Routed`** — written by `MessageRouter` for every successfully-routed message, recording the handler count (`"Routed to N handler(s)"`). Failure paths (unknown message type, no registered handlers) intentionally skip this log; those paths still write their existing `Failed` entry.

`IWarpSqlQueries<TContext>.ActivateScheduledJobsAsync` now returns `(Id, Queue, ScheduleTime)` instead of just `Queue` so the caller can materialize one `Activated` log per row — both shipped providers are updated, custom provider implementations need to project the two additional columns.

### Breaking: `ServerTaskSignal` enum values renumbered

`ServerTaskSignal` previously used implicit values (`JobFinalized = 0, MessageEnqueued = 1`). Project guideline §8.11 ("Enums always start at 1") plus the new `JobEnqueued` member made this the right moment to fix that pre-existing violation:

```csharp
// 0.15.x (implicit)
JobFinalized = 0
MessageEnqueued = 1

// 0.16.0 (explicit)
JobFinalized = 1
MessageEnqueued = 2
JobEnqueued = 3   // new
```

In-process the values are routing tokens only — not persisted to the DB and not exposed over the wire — so the rename is transparent for the vast majority of consumers. The exception is callers that **serialize** `ServerTaskSignal` values as integers (config files, custom event payloads, JSON dumps): those need to be re-saved under the new mapping. Code that uses the named members (`ServerTaskSignal.JobFinalized`, `.MessageEnqueued`) is unaffected.

### Breaking: public constructor surface for Core publishers

`Publisher<TContext>`, `BatchPublisher<TContext>`, `JobCommandService<TContext>`, `RecurringJobService<TContext>`, and `SagaStore<TContext>` (plus worker-side `MessageRouter<TContext>`, `ScheduledJobActivation<TContext>`, `WarpWorker<TContext>`) gain a required `ServerTaskSignals<TContext>` constructor parameter. Users who let DI resolve these via `IPublisher` / `IBatchPublisher` / `IJobCommandService` / etc. see no change — the registration is updated and the new dependency is available from `AddWarp`. Code that hand-rolls these types via `new` (subclassing, direct test instantiation) needs to pass `serviceProvider.GetRequiredService<ServerTaskSignals<TContext>>()` (or `new ServerTaskSignals<TContext>()` for a no-op in tests).

`Publisher` / `BatchPublisher`'s previously-optional `IWarpNotificationTransport? notificationTransport = null` parameter is also now required (no implicit `NullNotificationTransport` fallback) — pass `serviceProvider.GetRequiredService<IWarpNotificationTransport>()` or an explicit instance.

### Breaking: `AddBackgroundService<TContext, T>()` → `AddBackgroundService<T>()`

The two-type-parameter form was needed when the registration baked `TContext` into a generic query service. The query service has moved to `AddWarp<TContext>` (where `TContext` is already known from the receiver), so the user-facing call drops one type parameter:

```csharp
// 0.15.x
opt.AddBackgroundService<AppDbContext, KafkaDrainService>();

// 0.16.0
opt.AddBackgroundService<KafkaDrainService>();
```

No backwards-compat overload is shipped — the old form is a compile error. A one-line replace per call site is all that's needed.

### Schema: four BG-service tables now ship with every install

`BackgroundServiceDefinition`, `BackgroundServiceInstance`, `BackgroundServiceLease`, `BackgroundServiceLog` are now added unconditionally by `AddWarp<TContext>()` via `WarpModelCustomizer` — previously they were added conditionally by `AddBackgroundService<T>()`. Existing deployments that previously called `AddBackgroundService<T>()` see no schema change. Deployments that **didn't** previously call it will get those four tables on the next `dotnet ef migrations add`. The tables stay empty until a `WarpBackgroundService` is registered — no runtime cost, just a one-time migration.

If you bypass `WarpModelCustomizer` (e.g., a unit-test `DbContext` that calls `modelBuilder.AddOutboxStateEntity(schema)` directly in `OnModelCreating`), the four entities are now included in `AddOutboxStateEntity` and no additional calls are needed — you can delete any explicit `AddBackgroundServiceDefinitionEntity`/`...InstanceEntity`/`...LeaseEntity`/`...LogEntity` calls in your override.

### Feature: orphaned `Definition` rows are cleaned up automatically

Renamed or removed services left a permanent row in `BackgroundServiceDefinition` (the dashboard's "Services" list would keep showing the old name forever). `ExpirationCleanup` now sweeps orphan Definitions on its existing 60-second cadence — a row is deleted when no live `BackgroundServiceInstance` references its name **and** the row's `LastSeenAt` is older than `WarpConfiguration.BackgroundServiceDefinitionOrphanGrace` (default 2 minutes). The grace window covers the rolling-deploy gap between server A's exit and server B's startup; tune it up if your deploys take longer.

### Dashboard: `Services` nav is always shown

The `Services` flag was removed from `GET /api/addons`; the dashboard nav entry is always present. The list page is simply empty when no `WarpBackgroundService` is registered — same shape as the Jobs / Recurring / Servers tabs.

### Worker heartbeat: BG-service CTEs run unconditionally

The provider-package heartbeat SQL (`HeartbeatAsync`) previously branched on whether the addon was registered. Both branches collapsed to the BG-service-aware variant — two extra `UPDATE` statements piggyback on every heartbeat round-trip. For deployments with no registered service the UPDATEs target empty tables and are no-ops; no measurable perf change. The `else` branch (lighter SQL) is removed along with the `HasBackgroundServiceTables` flag.

### Fixed: saga serialization could hang for 20s on SQL Server under contention

`SagaHandlerProxy` and `SagaCommandService` used `IWarpSemaphoreProvider` with `maxCount = 1` — semantically a mutex, but routed through Medallion's `SqlDistributedSemaphore` row-based protocol. Under SQL Server's lock-based MVCC, that protocol's `TryAcquireAsync(TimeSpan.Zero)` did not reliably fast-fail when another transaction held the semaphore-state row: subsequent contenders blocked on the configuration row lock for the full operation budget instead of returning `null` immediately.

Both call sites now use `IWarpLockProvider`, which wraps Medallion's `SqlDistributedLock` (`sp_getapplock` on SQL Server, `pg_try_advisory_lock` on Postgres). Native distributed locks with timeout zero reliably fast-fail without taking row locks, so busy-saga contenders now requeue with a jittered `Busy` outcome (§8.17) in single-digit milliseconds rather than wedging for ~20s. Both sites had to switch together because they share the same lock name (`warp:saga:{type}:{key}`); `AddSagas` registration now validates that `IWarpLockProvider` is present instead of `IWarpSemaphoreProvider`. Both provider packages already register both primitives, so no provider-side change is required.

### Dashboard: toast notifications, error boundary, and per-domain hooks

The dashboard's mutation surfaces — job requeue/delete, bulk job operations, recurring-job actions, server pause/resume, concurrency/rate-limit upserts — previously either failed silently or showed ad-hoc inline UI on failure. Every mutation now flows through a centralized hook layer that emits a `sonner` toast on success and on error, so an operator who clicks "Requeue" on the dashboard reliably sees what happened.

A hand-rolled `<ErrorBoundary>` (no extra dependency) now wraps the app — a crashed page no longer takes the whole dashboard with it.

Internally, the ten list pages (Jobs, Messages, Batches, Recurring, Servers, Sagas, Counters, Concurrency Limits, Rate Limits, Background Services) are migrated to `@tanstack/react-query` with a single `QueryClient`, per-domain `useX` hooks, and a single `useRealtimeInvalidation` bridge that routes SignalR hub events into cache invalidation — replacing the per-page `useState + useEffect + axios + useRefreshKey + useRealtimeRefetch` pattern. Tabular pages share a new `<DataTable>` built on `@tanstack/react-table` for consistent pagination, empty/loading/error states, and column metadata. Detail pages are intentionally left on the legacy pattern for a follow-up; `useRefreshKey` and `useRealtimeRefetch` remain in place to support them. No design changes.

Bundle size grows from ~470 kB gzip to ~487 kB gzip (~17 kB for `react-query` + `react-table` + `sonner`). Build remains analyzer-clean.

### Demo mode: clock is now pinned for deterministic screenshots

`npm run dev -- --mode demo` now pins `Date.now()` to `2026-05-25 11:00 UTC` before the demo module loads, and `formatRelativeTime` uses `Date.now()` as the "now" baseline. The result is that "X ago" labels in demo mode are stable across runs, and `npm run screenshots` is now deterministic enough to use as a visual-regression check — untouched pages diff at 0 px, migrated pages at \<0.2% pure subpixel noise. The screenshot baselines under `website/static/img/screenshots/` are regenerated against the pinned clock and the new dashboard pages.

## 0.15.3

*2026-05-22*

One performance fix to the bulk job operations behind the dashboard's "Delete" and "Requeue" buttons. No public API changes; no behavior changes on the single-row paths.

### Fixed: bulk delete/requeue scaled linearly with per-row DB round-trips

`IJobCommandService.BulkDeleteJobs` and `BulkRequeueJobs` — and the per-type variants `DeleteFailedJobsByType` / `RequeueFailedJobsByType` that loop over them — were implemented as a `foreach` over the input ID array calling the single-row `DeleteJob` / `RequeueJob` once per item. Each iteration opened its own transaction, took a row lock via `LockJobByIdWaitAsync`, ran a separate `UPDATE`, two `INSERT`s (`JobLog`, `Counter`), and a `COMMIT` — roughly 6 round-trips per row. A 20,000-row dashboard "Delete all" landed ~120k serial round-trips at the DB and took minutes on cloud Postgres / SQL Server.

Both methods are now chunked (500 IDs/chunk, sized to stay under SQL Server's 2,100-parameter limit) and use a single conditional `ExecuteUpdateAsync` per source-state group inside one transaction per chunk. `JobLog` inserts are batched via the change tracker; `Counter` rows are aggregated to one row per state-group instead of one row per affected job. For `BulkRequeueJobs`, parents are locked once per unique parent and `JobCount` is bumped by the total affected children, replacing N×`LockJobByIdWaitAsync` calls when many children share a `Batch` / `Message` parent.

For the 20k-row scenario the round-trip count drops from ~120k to a few hundred — minutes become seconds.

### Concurrency semantics

The conditional UPDATE uses `WHERE CurrentState = sourceState` (the source state from the chunk's snapshot) to make the bulk path a tie-breaker against concurrent single-row Delete/Requeue calls on the same job:

- Whichever writer commits first wins the row's transition.
- The loser's UPDATE re-evaluates the predicate against the post-commit state, finds the row excluded, and reports it as `Skipped` in the `BulkResultModel`.
- The loser writes **no** `JobLog`, **no** `Counter` increment, **no** half-update — exactly one of `Delete` or `Requeue` ever lands per row.

`BulkRequeueJobs` Phase 1 (children) runs before Phase 2 (parent lock), matching the child-then-parent lock order used by single `RequeueJob` to prevent cross-caller deadlock with a single-row requeue racing for the same parent. Both methods sort their target IDs and `BulkRequeueJobs` sorts parents by PK before iteration, so two concurrent `BulkRequeueJobs` callers cannot acquire parent locks in opposing orders — the only remaining deadlock cycle in this code path is eliminated by construction rather than relying on DB-level detection.

### Notification fanout

`BulkRequeueJobs` previously fired one `JobEnqueued` notification per requeued job — for a 20k-row requeue that meant 20k publishes through the notification transport. The method now fires one `JobEnqueued` per **distinct queue** touched, after all chunks commit. Workers are queue-scoped (§2.9), so per-queue wake-up is sufficient. Bulk requeue across one default queue now produces exactly one notification instead of 20,000.

### Behavior preserved

- Per-chunk atomicity: each chunk is one transaction; mid-chunk failures roll back fully.
- `Processing` jobs still receive `CancellationMode = Graceful` rather than being deleted directly — workers detect via `RunJobMonitor`, same contract as single-row `DeleteJob`.
- Already-`Deleted` rows count as `Succeeded` (no-op), phantom IDs count as `Skipped`, `Succeeded + Skipped == jobIds.Length`.
- Duplicate IDs in the input array are deduped at the entry point and credited as no-op `Succeeded`, matching the 1-by-1 behavior where the second call sees `state == Deleted` / `Enqueued` and returns silently.

No action required on upgrade.

## 0.15.2

*2026-05-18*

Two bug fixes — one correctness fix in `ExpirationCleanup`, one supervisor behavior change that stops silently dropping `BackgroundService` state writes. No public API changes.

### Fixed: `ExpirationCleanup` foreign-key violation when batch splits a parent from its children

`ExpirationCleanup.RunCleanupAsync` and `RunCountBasedCleanupAsync` selected expired jobs whose children were all expired and then `ExecuteDeleteAsync`-ed the batch in one statement. When `Take(ExpirationBatchSize)` (default 100) cut off mid-tree — parent included, some expired children outside the batch — the DELETE intermittently violated the self-FK `fk_job_job_parent_job_id`:

```
23503: update or delete on table "job" violates foreign key constraint
       "fk_job_job_parent_job_id" on table "job"
```

The fix tightens both predicates to delete only **leaves** (`!x.ChildJobs.Any()`). An expired job with any child rows still pointing at it stays in place; on the next cleanup tick its (already-expired) children are gone and the parent itself becomes a leaf and is deleted. Trees of expired jobs drain one level per tick — a 3-level chain takes 3 ticks of `ExpirationCleanupInterval` (default 5 minutes) to fully clear, but never violates the FK.

Behavior preserved:
- Failed jobs still never auto-expire (`ExpireAt = null`, §8.2). An expired ancestor of a `Failed` descendant remains uncleanable until an operator requeues/deletes the failed leaf — same as before.
- Sibling successes still expire and clean up independently of failed siblings — also the same as before. Trace context for failure investigations should come from your OTel export, not the operational DB.

No action required on upgrade.

### Changed: supervisor no longer silently swallows DB write failures

`BackgroundServiceSupervisor` previously caught and logged any DB exception from its own state writes (`SetStatus`, `RecordFault`, `ResetRestartCount`) and continued as if the write had succeeded. Under DB contention (pool exhaustion, command-timeout under load) this meant the dashboard timeline could quietly stop matching reality — the service would still run, but the row's `Status` and the lifecycle log would diverge from the actual supervisor state. Observers never fired for the dropped transition, so anyone watching `IBackgroundServiceStatusObserver` would just see gaps.

The supervisor now lets those exceptions propagate to a single outer iteration-level `try/catch` that treats the failure exactly like a faulted service: emits `LogSupervisorFault` (a new `Lifecycle / Error` log entry distinct from `LogFaulted`, which is reserved for user-code faults), increments `warp.background_services.faulted` (tagged with `exception_type`), waits out the next entry in the backoff sequence on real wall-clock time, and re-runs the iteration. `RestartCount` is **not** incremented for supervisor-side faults — that counter remains reserved for user-code faults.

The trade-off this codifies: service execution is now gated on the supervisor's ability to persist its status. A `BackgroundService` whose own `ExecuteAsync` doesn't touch the DB at all will be briefly paused (1s+ backoff) during a Warp-DB blip rather than running with a stale dashboard reading. This was judged the better default — silent staleness on a thrashing service is worse to diagnose than a delayed-but-visible fault.

Operator-facing changes:
- Lifecycle log now has a new entry type `Supervisor faulted: <ExceptionType>: <message>` (Lifecycle / Error) — distinct from `Service faulted: …` which is still emitted for user-code throws.
- `warp.background_services.faulted` counter is now incremented for supervisor-side faults too. If you alert on this counter, expect a small bump under DB contention; the `exception_type` tag will distinguish infrastructure faults (`NpgsqlException`, `SqlException`) from user faults.
- For deployments using `WarpBackgroundService` with no scoped DB dependencies, expect occasional brief restart-backoff cycles during DB pressure rather than silent execution.

No public API changes.

## 0.15.1

*2026-05-18*

One packaging fix to `Moberg.Warp.Core`. No public API changes.

### Fixed: source generator missing from the NuGet package

The mediator/job dispatch source generator (`Warp.SourceGenerator`) was wired up as a project-level analyzer in `Warp.Core.csproj` but never packed into `Moberg.Warp.Core.nupkg`. In-tree consumers (Warp's own tests, demos, benchmarks) reference Core via `<ProjectReference>` so the analyzer flowed through normally — masking the gap. Downstream NuGet consumers got the runtime DLL but no generator, so `[ModuleInitializer]`-driven `WarpGeneratedHandlerRegistry` registrations never ran. `AddWarpWorker` then registered nothing and the first `IMediator.Send(...)` threw `"No handler registered for {RequestType}"`.

The bug has been present since handler auto-registration shipped in 0.10.0 (2026-04-27). It surfaced for consumers with more than a trivial number of handlers, where the manual `services.AddScoped<IRequestHandler<,>, ...>()` workaround stopped scaling.

The fix is one block in `Warp.Core.csproj` mirroring what `Warp.Http.csproj` has had since day one — embed the generator DLL into `analyzers/dotnet/cs/`:

```xml
<ItemGroup>
  <None Include="..\Warp.SourceGenerator\bin\$(Configuration)\netstandard2.0\Warp.SourceGenerator.dll"
        Pack="true" PackagePath="analyzers/dotnet/cs/" Visible="false" />
</ItemGroup>
```

No action required on upgrade beyond bumping the `Moberg.Warp.Core` version. Consumers that worked around the bug with reflection-based handler scanning can drop the workaround once on 0.15.1 — `AddWarpWorker` / `AddWarp` will pick handlers up automatically.

## 0.15.0

*2026-05-17*

One new opt-in addon — `WarpBackgroundService` — dashboard-visible analog of .NET's `BackgroundService` that runs outside the worker pool. No breaking changes; no public API removals.

### New: `WarpBackgroundService` addon

Opt-in via `opt.AddBackgroundService<T>()`. Lets you register a long-lived background loop that Warp manages — restart-with-backoff, optional cluster-singleton coordination via lease, captured `ILogger<T>` output in the dashboard. Migration from plain `BackgroundService` is one line — replace the base class.

```csharp
public sealed class KafkaDrainService : WarpBackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<KafkaDrainService> _logger;

    public KafkaDrainService(IServiceScopeFactory scopes, ILogger<KafkaDrainService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    public override ServiceScope Scope => ServiceScope.Singleton;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // ... drain a batch, commit offsets, etc.
            _logger.LogInformation("Drained {Count} messages", count);
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }
}

services.AddWarpWorker<AppDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.AddBackgroundService<KafkaDrainService>();
});
```

**Scope:**

- `ServiceScope.PerServer` (default) — every server with the service compiled in runs an independent instance. Matches plain `BackgroundService` semantics.
- `ServiceScope.Singleton` — exactly one instance across the cluster, coordinated by a lease in `background_service_lease`. The lease has a 30 s TTL, renewed every 3 s on the existing `Heartbeat` server task. On hard-kill, failover takes ≤ 30 s; on graceful shutdown, the holder deletes the lease row before exit so a waiter acquires immediately.

**Lifecycle is always-on:** if `ExecuteAsync` throws — or returns without the cancellation token being signalled — the supervisor records the fault, applies exponential backoff (1 → 2 → 4 → 8 → 16 → 30 s, capped), and re-invokes. A successful run of ≥ 5 minutes resets the counter. There is no stop button in v1 — to disable a service you remove the registration and redeploy.

**Log capture out of the box:** a per-instance `ILoggerProvider` is wired up automatically; user `ILogger<TService>` calls flow into both the normal log stack AND a `background_service_log` table that the dashboard renders. Volume guardrails: level filter (`MinLogLevel` defaults to `Information`, override per-service), 100 entries/sec rate cap with a 10 s drop window, 4 KB message truncation, retention of 1 000 rows per instance / 7 days (both global-configurable). Lifecycle events (`Started`, `LeaseAcquired`, `LeaseLost`, `Faulted`, `Restarting`, `Stopped`, `ConfigurationMismatch`) share the same table with `Source = Lifecycle`.

**Configuration-mismatch detection:** each server stamps its declared `Scope` onto its instance row. If a rolling deploy ships a version where `Scope` changed (e.g., `PerServer → Singleton`), the new instances detect the disagreement against the existing `background_service_definition` row, write `Status = ConfigurationMismatch`, and refuse to run user code until the deploy completes. The dashboard surfaces the mismatch loudly so split-brain across the transition window is impossible.

**Dashboard:** new `/warp/services` nav, gated by the existing `/api/addons` discovery flag (`services: true`). List view aggregates per service name; detail view shows per-instance tabs with status, captured exception, lease panel (singleton only) with TTL countdown, and a log tail filterable by source and level.

**Lifetime contract:** registered as singleton. Inject `IServiceScopeFactory` and create scopes per work unit — same pattern recommended for plain `BackgroundService`. `ValidateScopes = true` in Development catches the captive-scoped-dependency foot-gun (e.g., taking `DbContext` directly in the ctor) at startup.

**Telemetry:** four OTel counters — `warp.background_services.started`, `warp.background_services.faulted` (tagged `service_name` + `exception_type`), `warp.background_services.restarts`, `warp.background_services.lease_lost`.

### Schema change

If you manage your Warp migrations manually, opt-in users will need to add four new tables and two indexes (note the second index is the one for the cross-server dashboard log tail — easy to miss):

```sql
CREATE TABLE warp.background_service_definition (...);
CREATE TABLE warp.background_service_instance   (...);
CREATE TABLE warp.background_service_lease      (...);
CREATE TABLE warp.background_service_log        (...);

CREATE INDEX ix_background_service_log_per_instance
    ON warp.background_service_log (server_id, service_name, id);
CREATE INDEX ix_background_service_log_by_service
    ON warp.background_service_log (service_name, id DESC);
```

EF Core auto-migration produces these from the model when `opt.AddBackgroundService<T>()` triggers the entity configurators. Deployments that do **not** call `AddBackgroundService<T>()` see no schema change — the entities are absent from the model and the cleanup tasks (`ExpirationCleanup`, `ServerCleanup`, `WarpServerRegistration.StopAsync`) guard with `Model.FindEntityType(...) != null` before touching them.

### Stats

- ~9 500 lines of source + tests across 106 files
- 1 931 tests (PostgreSQL + SQL Server) — zero skipped, zero failures
- Full feature documentation at `features/background-services.md`

### Links

- [GitHub Release](https://github.com/moberghr/warp/releases/tag/0.15.0)

---

## 0.14.1

*2026-05-16*

Two patch-level fixes to the dashboard surface. No public API changes.

### Fixed: dashboard timestamps off by the client's UTC offset

The dashboard rendered `Started`, `Heartbeat`, and other server/job timestamps in the client's local timezone but advertised them as the UTC moment — a server that started 5 minutes ago appeared as "about 2 hours ago" on a `UTC+2` client. The root cause was on the backend: SQL Server `datetime2` (and Postgres `timestamp` without timezone) columns carry no `DateTimeKind` info, so EF Core materialized values with `Kind=Unspecified`. `System.Text.Json` then serialized the resulting `DateTime` without a `Z` suffix, and the browser's `new Date(...)` parsed the unmarked string as local time.

A new `ValueConverter` applied via `WarpModelCustomizer` stamps `Kind=Utc` on every `DateTime` / `DateTime?` property read from `Warp.Core`-assembly entities. The convention is assembly-scoped (not namespace-prefixed), so a user's entity sharing the same DbContext is never touched. Any property the user has already attached their own converter to is left alone.

No action required on upgrade — the fix takes effect after a redeploy. Production code that already wrote `Kind=Utc` (everything routed through `TimeProvider.GetUtcNow().UtcDateTime` per [§5.7](https://moberghr.github.io/warp/docs/) and Warp itself) continues to behave identically.

### Changed: addon-discovery probes consolidated into `/api/addons`

The dashboard previously discovered opt-in addons by firing one `GET` against each addon's data route (`/api/concurrency`, `/api/ratelimits`, `/api/dashboard/push/probe`) and treating the 404 as the "addon not enabled" signal. That worked, but it surfaced three red 404s in DevTools every session and made the network tab noisy when triaging unrelated issues.

`GET /api/addons` replaces all three with one always-200 response:

```json
{ "concurrency": true, "rateLimits": false, "push": true, "sagas": false }
```

The booleans are computed from DI service presence. The dashboard makes one round-trip per session, decides nav visibility from the flags, and uses `push` to gate the SignalR connect. A one-shot retry on transient failure keeps a single network blip from hiding all addon nav for the rest of the session.

The legacy `/api/dashboard/push/probe` route was removed in this release. `/api/concurrency` and `/api/ratelimits` still 404 when the corresponding addon isn't registered — they're the data endpoints — but the bundled dashboard no longer probes them speculatively.

External integrations that were polling `/api/dashboard/push/probe` should switch to `/api/addons` and read the `push` boolean.

## 0.14.0

*2026-05-14*

Two new addons (`[Timeout]` and `[RateLimit]`), realtime dashboard updates over SignalR, opt-in handler-reported progress bars, and a major idle-query-rate reduction on the server-task path. One small but pointed PG provider fix (honour `NpgsqlDataSource`) makes Aspire / Managed Identity / Vault setups work without manual wiring. Two breaking surfaces — a server-task default flip and a cross-addon `IXxxMetadata` property rename — fall out of this work; both are spelled out below.

### New: `[Timeout]` addon

Opt-in via `opt.AddTimeout()`. Caps how long a handler is allowed to run; on deadline, the worker cancels the handler's `CancellationToken`.

```csharp
opt.AddRetry();    // optional, but MUST come before AddTimeout
opt.AddTimeout(o => { o.Default = TimeSpan.FromMinutes(10); });

[Timeout(seconds: 30)]                                            // Delete, PerAttempt
[Timeout(seconds: 30, Mode = TimeoutMode.Fail)]                   // throws TimeoutException → retried by AddRetry
[Timeout(seconds: 30, Mode = TimeoutMode.Fail, Scope = TimeoutScope.Total)]   // bounds the entire retry chain
public class CallSlowApi : IJob { }

// or per-publish
await publisher.Enqueue(new GenerateReport(),
    new JobParameters().WithTimeout(TimeSpan.FromMinutes(5)));
```

Two modes (`TimeoutMode`):

- **`Delete`** (default) — pipeline sets `Outcome { State = Deleted }`. **Not** retried by `AddRetry` (the outcome path bypasses retry's `catch`). Use when "kill it and move on" is the right answer.
- **`Fail`** — pipeline throws `TimeoutException`, which `AddRetry` catches and reschedules. Without `AddRetry`, the job ends `Failed`. Use when a slow upstream may succeed on retry.

Two scopes (`TimeoutScope`):

- **`PerAttempt`** (default) — each attempt gets a fresh budget.
- **`Total`** — `DeadlineUtc` stamped on publish; once past the deadline, every attempt's timer fires immediately. Bounds total wall-clock to roughly `TimeoutSeconds` plus retry backoff. Only useful with `Mode = Fail`.

Cooperative cancellation only — handlers that ignore the token complete normally, and the timeout doesn't fire after-the-fact. See [the Timeout feature page](features/timeout) for the full breakdown.

**Pipeline ordering rule:** `AddRetry()` MUST be called before `AddTimeout()`. DI insertion order is outer→inner; retry has to wrap timeout to see the `TimeoutException`. `TimeoutAddonOrderingTests` pins this.

### New: `[RateLimit]` addon

Opt-in via `opt.AddRateLimit()`. Throttle jobs sharing a key to N starts per window.

```csharp
opt.AddConcurrency();  // optional, but MUST come before AddRateLimit
opt.AddRateLimit();

[RateLimit("sendgrid", count: 10, perSeconds: 60)]                              // Fixed, Skip (defaults)
[RateLimit("sendgrid", count: 10, perSeconds: 60, Mode = RateLimitMode.Wait)]   // requeue surplus
[RateLimit("crm", count: 100, perSeconds: 60, Style = RateLimitStyle.Sliding)]  // rolling window
public class SendEmail : IJob { }

// or per-publish
new JobParameters().WithRateLimit("sendgrid", 10, TimeSpan.FromSeconds(60));
```

Two styles (`RateLimitStyle`):

- **`Fixed`** (default) — wall-clock window floor-aligned to global UTC ticks. Cheap, predictable bursts at boundaries.
- **`Sliding`** — rolling window over the last N starts, defensively trimmed. Smoother distribution; slightly more storage churn per check.

Two outcome policies (`RateLimitMode`):

- **`Skip`** (default) — surplus jobs end `Deleted`.
- **`Wait`** — surplus jobs are rescheduled via `JobOutcome.RescheduledState` with 100–500 ms jitter on lock contention.

Live state lives in a new `RateLimitBucket` entity; admin overrides in `RateLimitOverride`. Both are contributed only when `AddRateLimit()` is registered. `perSeconds` is capped at 7 days (`MaxWindowSeconds`). The pipeline lock is released after check-and-increment — **not** held during handler execution (unlike `[Mutex]`).

Dashboard CRUD lives at `/warp/ratelimits` and follows the hide-on-404 nav probe.

**Caveats:**

- DB push does **not** accelerate `Wait`-mode reschedules — they land in `State.Scheduled` and depend on `ScheduledJobActivation` polling.
- Don't put PII in the key — keys appear in `JobLog.Message` and on the dashboard.
- **Pipeline ordering rule:** `AddConcurrency()` before `AddRateLimit()`. Mutex/Semaphore rejection should not waste a rate-limit token; reversing the order causes the wasted-token bug until the next window rollover.

See [the Rate Limit feature page](features/rate-limit) for the matrix and tuning notes.

### Breaking: cross-addon `IXxxMetadata` property rename

`IConcurrencyMetadata` properties were unprefixed (`Key`, `Limit`, `Mode`) in 0.13.0. All `IXxxMetadata` interfaces share a single backing `Dictionary<string, object>` per job, so bare names silently collided when a job carried both `[Mutex]` and the new `[RateLimit]`. The fix renames every metadata property to its addon namespace:

- `IConcurrencyMetadata.Key` → `ConcurrencyKey`
- `IConcurrencyMetadata.Limit` → `ConcurrencyLimit`
- `IConcurrencyMetadata.Mode` → `ConcurrencyMode`
- `IRateLimitMetadata` ships with `RateLimitKey` / `RateLimitCount` / `RateLimitWindowSeconds` / `RateLimitMode` / `RateLimitStyle`.

The attribute and fluent surfaces (`[Mutex("k")]`, `[Semaphore("k", N)]`, `WithMutex(...)`, `WithSemaphore(...)`) are unchanged — only direct callers of `IConcurrencyMetadata` need updating. Custom pipeline behaviours, test fakes, or third-party integrations that read or set these properties must rename to the prefixed form.

### New: realtime dashboard push (SignalR)

Opt-in via `opt.AddDashboardPush()`. Registers a `WarpDashboardHub` at `${RoutePrefix}/api/hub` plus a `DashboardBroadcaster<TContext>` `BackgroundService`. The broadcaster subscribes to `ServerTaskSignals<TContext>` (third consumer after `Orchestrator` and `MessageRouter`) and emits `JobFinalized` / `MessageEnqueued` events to connected dashboards. Each broadcast carries the current `DashboardStatistics` DTO as the SignalR payload, so N connected clients no longer trigger N × `GET /api/status` refetches.

```csharp
builder.Services.AddWarp<AppDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.UseDatabasePush();    // required for multi-server fanout
    opt.AddDashboardPush();
});
```

Coalesce window defaults to 100 ms (`WarpDashboardPushConfiguration.CoalesceWindow`) — burst signals (a 50-job batch finalising) collapse to one broadcast per kind per window.

**Caveats:**

- Multi-server fanout reuses `UseDatabasePush()`. Without it, each server's broadcaster only sees signals from its own workers; clients connected to server A miss events originating on server B until the 30 s safety-net poll.
- Per-view data (filtered job lists, job detail, logs) stays on event-driven REST refetch. Push is invalidations + the stats DTO, not per-view payloads.
- Frontend probes `${RoutePrefix}/api/dashboard/push/probe` once at boot and falls back to 30 s polling when the addon is absent (hide-on-404 mirroring `/api/concurrency`).

Auth piggybacks on the existing `WarpUIMiddleware` — both the SignalR negotiate and the WebSocket-upgrade HTTP requests pass through `/api/`, so an auth-protected dashboard requires no extra wiring.

See [the Dashboard Push feature page](features/dashboard-push) for telemetry hooks and tuning.

### Breaking: `ServerTaskSignals<TContext>` moved namespace

`ServerTaskSignals<TContext>` and `ServerTaskSignal` enum moved from `Warp.Worker.Services` to `Warp.Core.Events` so addons can subscribe without taking a dependency on the worker assembly. `Subscribe()` is promoted to `public IDisposable`. Anyone reaching into these types directly (rare — most consumers use the addon surface) needs to update their `using` directive.

### New: `IJobContext.ReportProgress(name, percent)`

```csharp
public class GenerateReport : IJobHandler<GenerateReportRequest>
{
    private readonly IJobContext _context;

    public GenerateReport(IJobContext context) => _context = context;

    public async Task HandleAsync(GenerateReportRequest message, CancellationToken ct)
    {
        for (var i = 0; i < total; i++)
        {
            // ... process row i ...
            _context.ReportProgress("rows", (i * 100) / total);
        }

        _context.ReportProgress("upload", 0);
        await UploadAsync(ct);
        _context.ReportProgress("upload", 100);
    }
}
```

Percent is clamped to `0..100`. Multiple named bars per job are supported — pass an empty name (or use the `ReportProgress(int percent)` overload) for the single-bar case. The detail page renders one bar per name in the right column above History/Logs; the card is hidden entirely when a job reported no progress. Reporting is **opt-in per handler** — jobs that don't call `ReportProgress` incur zero overhead and produce no rows.

`IJobContext.ReportProgress` writes to an in-memory `JobProgressCollector` (one per running job, mirrors the existing `JobLogCollector`). The worker's existing `RunJobMonitor` loop drains the collector every ~1 s during handler execution and on the terminal commit. Each *changed* bar emits one row; unchanged bars emit nothing, so a stalled bar at 47% doesn't churn rows every second. Final values land in the same `SaveChangesAsync` as the terminal `JobLog` row.

Progress is **display telemetry, not state** — it never participates in the state machine, the orchestrator, or worker scheduling.

### Breaking: `IJobContext` gained two abstract members

```csharp
void ReportProgress(string name, int percent);
void ReportProgress(int percent);
```

Custom `IJobContext` implementations (test fakes, third-party pipeline integrations) need to add these two members. Both can be empty bodies if the fake doesn't care about progress. The concrete `JobContext` shipped in `Warp.Core` implements them via an internal collector reference.

### Perf: server-task idle query rate down ~92%

With `UseDispatcher = true` + `UseDatabasePush()` and default intervals, idle query rate drops from ~10–15 q/s to ~1.2 q/s — measured by `Warp.PerfTest --mode idle` against PG with an `ActivityListener` on the Npgsql source. See [perf-results.md](https://github.com/moberghr/warp/blob/main/docs/perf-results.md#idle-queries-per-second-server-task-overhead) for the full matrix.

### Breaking: server-task default changes

The query-rate work changes three defaults. Most users won't notice — the bookkeeping these defaults gate is internal — but anyone tuning intervals or implementing custom `IServerTask`s should review:

- **`IServerTask.LocksWithTransaction` defaults to `true`.** Server tasks now serialize via xact-scoped advisory locks (`pg_try_advisory_xact_lock` / `sp_getapplock` with `@LockOwner='Transaction'`) — auto-released on commit/rollback rather than held via a session-scoped Medallion lock. Cuts the per-iteration lock chatter from 3 round-trips (acquire/release + work) to 1 fold. Custom server tasks that need the **session-scoped** Medallion behaviour (e.g., tasks that span multiple transactions, or call `SaveChangesAsync` more than once per `ExecuteAsync`) must opt out by overriding `LocksWithTransaction => false`. `MessageRouter` does this — it commits once per routed message.
- **`CounterAggregationInterval` defaults to `60s`** (was `5s`). Counter rows are still written immediately by hot paths; only the aggregation roll-up cadence is relaxed. Dashboard `Statistic` cards will be at most 1 min stale instead of 5 s. If you rely on fresh aggregated counters, set it back: `opt.CounterAggregationInterval = TimeSpan.FromSeconds(5);`.
- **`MaxPollingInterval` auto-bumps to 5 min when `UseDatabasePush()` is called and the value is still at its default.** Push notifications cover work activation; the long polling fallback exists only to backstop missed notifications, which the listener already drains on reconnect. Explicit overrides on `opt.MaxPollingInterval` are respected — the auto-bump only fires when the value is still the class default. `MessageRoutingInterval` and `OrchestrationInterval` follow the same pattern.

### New: bounded server-task batching

Orchestrator, MessageRouter, ScheduledJobActivation, and StaleJobRecovery now bound their per-iteration work via `WarpWorkerConfiguration.ServerTaskBatchSize` (default `100`). This prevents a single iteration from churning through a multi-thousand-row backlog while holding the orchestration lock; subsequent iterations drain the remainder via `RerunImmediately = true`. Tune up if you've sized your DB to swallow larger batches.

### Fix: PG provider honours `NpgsqlDataSource`

`Warp.Provider.PostgreSql` previously opened raw `NpgsqlConnection`s from the EF connection string for its distributed lock, semaphore, and LISTEN/NOTIFY transport. That bypassed any `NpgsqlDataSource` attached to the `DbContext` options — Aspire's `AddAzureNpgsqlDataSource` against Postgres Flexible Server with Managed Identity, custom `NpgsqlDataSourceBuilder` config (Vault-issued passwords, custom cert validation, channel binding), and similar setups all broke with a `28000 / no pg_hba.conf entry` from the first `AddOrUpdateRecurringJob` call.

`UsePostgreSql<TContext>` now reads `NpgsqlOptionsExtension.DataSource` off the `DbContextOptions<TContext>` and threads it through to `PostgresLockProvider`, `PostgresSemaphoreProvider`, and `PostgresNotificationTransport` — lock + semaphore via Medallion's `PostgresDistributedSynchronizationProvider(DbDataSource)`, transport via `dataSource.OpenConnectionAsync`. When the data source isn't present (plain `UseNpgsql(connectionString)`), behaviour is unchanged.

No public-surface breaking changes — all new entry points are additive ctors plus a private resolver.

### Schema additions

Two new tables (contributed only when `AddRateLimit()` is registered):

- `RateLimitBucket` — live per-key window state
- `RateLimitOverride` — admin runtime overrides

Two new nullable columns on `JobLog` (always present):

- `Name` (`nvarchar(100)?` / `varchar(100)?`) — progress bar name; null for all non-`Progress` rows
- `Value` (`smallint?`) — percent `0..100`; null for all non-`Progress` rows

No new indexes — the existing `(JobId)` index serves the progress detail-page read; rate-limit reads are keyed by bucket name. Existing deployments need a one-step EF migration to add the two `JobLog` columns plus the two new tables (the latter only when opting into `AddRateLimit()`); no data backfill required.

### Dependencies

`@microsoft/signalr@^9.0.6` added to the UI for the new dashboard-push transport. Eleven transitive Dependabot alerts cleared via lockfile bumps (no direct upgrades).

## 0.13.0

*2026-05-11*

Concurrency-focused release: the Mutex addon generalizes into a unified Mutex + Semaphore primitive with a runtime-editable admin layer, OpenTelemetry coverage broadens to cover producer / receive / mediator / server-task spans, and `CompletionBatch` gets transient-deadlock retry. Mutex Wait mode is new. The OTel span rename and the addon namespace rename are both breaking.

### Breaking: Mutex addon renamed to Concurrency

The Mutex addon is generalized into a unified concurrency primitive that backs both `[Mutex]` (limit = 1) and the new `[Semaphore]` (limit > 1) over a single pipeline behavior and metadata contract. The rename is mechanical but breaking:

- Namespace `Warp.Core.Mutex` → `Warp.Core.Concurrency`
- `opt.AddMutex()` → `opt.AddConcurrency()`
- `MutexMode` → `ConcurrencyMode`
- `IMutexMetadata` → `IConcurrencyMetadata`
- Distributed lock-key prefix `warp:mutex:` → `warp:concurrency:`

`[Mutex("key")]` and `WithMutex("key", ...)` continue to work — they're now thin wrappers over the unified primitive with `limit = 1`. No database migration. The lock-key prefix flip means any in-flight locks held under the old prefix won't be observed by the new code at upgrade — locks are short-lived, but don't deploy a rolling restart mid-burst on the same hot key.

### New: `[Semaphore("key", N)]` attribute

The companion to `[Mutex]` for `limit > 1`. Same pipeline, same metadata interface, same admin layer — just exposes the limit. Default `Mode` is `Wait` (`[Mutex]` defaults to `Skip`).

```csharp
[Semaphore("sendgrid", 10)]
public class SendEmail : IJob { }
```

Backend implementations differ — documented on [the semaphore feature page](features/semaphore):

- **PostgreSQL** — N distinct named advisory locks (`warp:concurrency:k:0` … `warp:concurrency:k:{N-1}`) over `pg_try_advisory_lock`. Per-process slot cache with random start offset.
- **SQL Server** — delegates to Medallion's `SqlDistributedSemaphore`.

The two backends construct lock names differently at `limit = 1`, so `[Mutex("k")]` and `[Semaphore("k", N)]` against the same key are independent on PG but share the slot pool on SQL Server. **Pick one or the other per key** — mixing both attributes against the same key is a portability footgun. CLAUDE.md and the feature docs spell this out.

### New: Mutex Wait mode

`[Mutex]` now supports a `Mode` option. The existing default `Skip` short-circuits the duplicate to `Deleted`. The new `Wait` mode requeues the duplicate with `ScheduleTime = now` and writes a `Requeued` audit-log entry — mutual exclusion without dropping work.

```csharp
[Mutex("payment:123", Mode = ConcurrencyMode.Wait)]
public class ChargeOrder : IJob { }
```

Or fluent: `new JobParameters().WithMutex("payment:123", ConcurrencyMode.Wait)`.

Caveats are promoted to the top of [the mutex feature page](features/mutex):

- **Best-effort order across requeued jobs**, not strict FIFO. The wait set is not a queue; on lock release, whichever requeued copy a worker fetches first wins.
- **No fairness.** A hot publisher against the same key can starve a long-blocked job indefinitely. If you need ordering, build a job-per-stream chain instead of relying on Wait.

### New: `IConcurrencyLimitManager` — runtime-editable limits

Attribute and fluent limits are compile-time defaults. `IConcurrencyLimitManager.AddOrUpdateLimit("key", N)` lets you raise or lower a key's effective limit at runtime; the override is persisted on a new `ConcurrencyLimit` entity and takes precedence. Resolver precedence: `admin row > attribute > 1`.

Surfaced as a new dashboard page at `/warp/concurrency` (visible only when `AddConcurrency` is registered — the SPA probes `/api/concurrency` and hides the nav entry on 404). Five REST endpoints: list / get / set / clear / clear-all.

### New: `stats:requeued` counter + `/counters` dashboard page

Both the new Mutex Wait outcome and the existing Retry behavior produce requeue events. A new `stats:requeued` (and `stats:requeued:{hour}` for the rolling 7-day window) counter row is emitted by `WarpWorkerService` / `WarpDispatcherWorker` for any `Enqueued`/`Scheduled` outcome — so retry rate now has visibility for free.

These — and any addon-defined counter keys — surface on a new **Counters** dashboard page at `/warp/counters`. The page has two parts:

- **Hourly history chart** — every counter whose key suffix parses as `yyyy-MM-dd-HH` becomes a series. 24h / 7d toggle, Chart.js legend toggle, fixed colors for built-ins (`succeeded` green / `failed` red / `deleted` gray / `requeued` amber), deterministic hash-derived colors for addon series.
- **Rolled-up table** — non-hourly keys sorted lexicographically. Hourly variants are filtered out so the table stays readable.

`ExpirationCleanup` now prunes any `Statistic` row whose key suffix parses as `yyyy-MM-dd-HH` and is older than 7 days — generalized from the prior per-prefix logic (which also had a latent bug where `stats:succeeded:*` rows were never actually cleaned because the `CompareTo` bound was anchored to `stats:failed:`). Addon-defined hourly metrics get the same retention treatment for free.

### Breaking: consumer span renamed `Warp.Execute` → `process <queue>`

The job-execution span emitted on `WarpTelemetry.ActivitySource = "Warp"` is now named `process <queue>` per OTel messaging-spans convention (e.g. `process default`, `process critical`). The legacy `Warp.Execute` name no longer appears.

**If you matched on the literal `Warp.Execute` span name** (in exporter rules, alerting queries, dashboard filters), update those matchers. Prefer the OTel-native filter `messaging.operation.name = process` — that's stable across renames and matches every queue.

### Behavioural change: `Activity.Current` inside handlers without a listener

Previously, the worker's consumer activity was always allocated and `Activity.Current` was non-null inside every handler — even in deployments that never wired up `tracerBuilder.AddSource("Warp")`. This was a per-job allocation tax with no observable benefit for non-OTel users.

Now, when no `ActivityListener` is attached for the Warp source, the worker skips the allocation and `Activity.Current` is `null` inside the handler. Handlers that read `Activity.Current` to extract trace context (e.g. to forward to a downstream HTTP call) must either attach a listener (`tracerBuilder.AddSource("Warp")`), tolerate null, or use `JobExecutionContext.Current.TraceId` — which carries the job's trace id regardless of listener state.

### Adds: producer + receive + mediator + server-task spans

`Publisher.Enqueue` / `Publish` / `Schedule` and `BatchPublisher.StartNew` now emit a Producer-kind `send <queue>` activity per publish. Critically, `Job.ParentSpanId` still references the *caller's* span (the HTTP request, parent handler) — the producer span is a sibling event marker on the caller's trace, not the consumer's parent. Tests pin this invariant.

The worker emits a Client-kind `receive <queue>` span around post-fetch / pre-handler bookkeeping (mark ownership, log "Processing", commit). Receive precedes the consumer span and is a sibling under the caller's trace.

`IMediator.Send(TRequest)` and `IMediator.CreateStream(TRequest)` emit Internal-kind `process <RequestType>` spans wrapping the full pipeline + handler (or pipeline + stream enumeration). The stream activity lives across the entire `await foreach` and closes via `try/finally` even on early break or exception.

`ConcurrencyPipelineBehavior` (the unified Mutex+Semaphore behavior) emits a child `warp.concurrency_acquire` Internal-kind span around the acquire attempt with `warp.concurrency.key`, `warp.concurrency.limit`, and `warp.concurrency.acquired` tags.

Each `ServerTaskLoop` iteration (Heartbeat, Orchestrator, MessageRouter, RecurringJobScheduler, …) emits a `warp.server_task <Name>` Internal-kind span tagged with `warp.task.lock_held` and `warp.task.message`.

### Adds: `messaging.operation.type` and `messaging.message.conversation_id` on consumer span

OTel messaging conventions split the operation verb into `messaging.operation.name` (free-form, was already set) and `messaging.operation.type` (low-cardinality, now set alongside). The consumer also gains `messaging.message.conversation_id = job.TraceId`, `error.type` on failure, `warp.job.attempt`, `warp.worker.id`, and `messaging.batch.message_count` for batch jobs.

### Adds: mediator metrics

Two new instruments on `WarpTelemetry.Meter = "Warp"`:

- `warp.mediator.duration` — Histogram, ms, tags `kind` (`request`/`stream`), `request_type`, `status` (`succeeded`/`failed`/`cancelled`).
- `warp.mediator.in_flight` — UpDownCounter, tags `kind`, `request_type`.

### Wiring (no change)

```csharp
services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource("Warp"))
    .WithMetrics(m => m.AddMeter("Warp"));
```

Single source + single meter, both named `"Warp"`.

### Fix: production deadlock retry in `CompletionBatch.FlushRangeAsync`

Under dispatcher mode (`UseDispatcher = true`) the completion batch occasionally hit a transient deadlock (SQL Server 1205, Postgres `40P01` / `40001`) on the parallel statistics + job-row update, which previously fell straight through to the existing split-on-failure path (slow, and lost the natural batch). Now `FlushRangeAsync` retries on transient deadlock with 50/100/200 ms exponential backoff before splitting.

Detection lives on a new `IDatabaseExceptionClassifier.IsTransientDeadlock` with provider-specific implementations in `Warp.Provider.PostgreSql` and `Warp.Provider.SqlServer` — exposed as a stable extension point.

### Fix: "Processing" log row no longer orphaned under dispatcher shutdown

`WarpDispatcher.FetchAndDistribute` wrote the `Processing` `JobLog` row *before* delivering the job to a worker's channel. If the channel write got cancelled during host shutdown, the row was orphaned (no `Completed` / `Failed` / `Cancelled` follow-up) and the dashboard showed a job stuck mid-flight that never actually ran. Moved to `WarpDispatcherWorker.MarkWorkerOwnership`, which runs after the worker actually starts processing the job. The log row now also carries the actual `WorkerId`, matching single-worker mode.

## 0.12.0

*2026-05-07*

### New: Moberg.Warp.Http

Optional package that exposes Warp `IRequest<TResponse>` and `IStreamRequest<TResponse>` handlers as ASP.NET Minimal API endpoints. Annotate the **handler class**, call `services.AddWarpHttp()` + `app.MapWarpHttp()`, and the endpoint is live. Source-generated dispatch (no per-request reflection); independent of `Moberg.Warp.UI`.

#### Quick start

Install `Moberg.Warp.Http` next to `Moberg.Warp.Core`, then tag handlers:

```csharp
using Microsoft.AspNetCore.Mvc;          // [FromRoute], [FromQuery], [FromHeader], [FromBody]
using Warp.Core.Handlers;
using Warp.Http;

public sealed record GetOrder([FromRoute] Guid Id) : IRequest<OrderDto>;

[WarpHttpGet("/orders/{id}")]
public sealed class GetOrderHandler : IRequestHandler<GetOrder, OrderDto>
{
    public Task<OrderDto> HandleAsync(GetOrder request, CancellationToken ct) =>
        Task.FromResult(new OrderDto(request.Id, "pending"));
}
```

Wire it up in `Program.cs` — independent of `Warp.UI`, composes with anything you already have:

```csharp
builder.Services.AddWarpHttp();

var app = builder.Build();
app.MapWarpHttp();      // null-group handlers
app.Run();
```

`GET /orders/{id}` is now live. The handler runs through the same `IPipelineBehavior<TRequest, TResponse>` pipeline as `IMediator.Send` — anything you've registered for cross-cutting concerns (auth, logging, validation, telemetry) applies automatically.

#### Binding leans entirely on ASP.NET Minimal API

No custom parser. `IParsable<T>`, `TryParse`, query arrays, nullable types, route constraints, content negotiation — all of it works because the source generator emits a Minimal API delegate that hands `TRequest` to ASP.NET:

```csharp
public sealed record ListOrders(
    [FromQuery] int Page,
    [FromQuery] int PageSize,
    [FromQuery] string[] Tags,                 // ?Tags=a&Tags=b → string[]
    [FromHeader(Name = "X-Trace-Id")] string TraceId)
    : IRequest<ListOrdersResponse>;

[WarpHttpGet("/orders")]
public sealed class ListOrdersHandler : IRequestHandler<ListOrders, ListOrdersResponse> { ... }
```

```csharp
public sealed record GetOrderTyped([FromRoute] Guid Id) : IRequest<OrderDto>;

[WarpHttpGet("/orders/{id:guid}")]                 // route constraint rejects non-GUIDs at routing time
public sealed class GetOrderTypedHandler : IRequestHandler<GetOrderTyped, OrderDto> { ... }
```

For mixed route + body shapes, declare a class with `[FromRoute]` / `[FromBody]` properties:

```csharp
public sealed class SubmitOrder : IRequest<OrderDto>
{
    [FromRoute(Name = "tenantId")] public Guid TenantId { get; set; }
    [FromBody]                     public SubmitOrderBody Body { get; set; } = new(string.Empty);
}

[WarpHttpPost("/orders/{tenantId}/submit")]
public sealed class SubmitOrderHandler : IRequestHandler<SubmitOrder, OrderDto> { ... }
```

A whole-body POST DTO without per-property attributes also just works — ASP.NET binds `TRequest` from the JSON body directly.

#### Streaming becomes Server-Sent Events

`IStreamRequest<T>` handlers turn into `text/event-stream` endpoints. `HttpContext.RequestAborted` propagates to the handler's enumerator, so a client disconnect ends the loop:

```csharp
public sealed record OrderFeed([FromQuery] int Count) : IStreamRequest<OrderEvent>;

[WarpHttpGet("/orders/feed")]
public sealed class OrderFeedHandler : IStreamRequestHandler<OrderFeed, OrderEvent>
{
    public async IAsyncEnumerable<OrderEvent> HandleAsync(
        OrderFeed request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        for (var i = 0; i < request.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return await NextEventAsync(ct);
        }
    }
}
```

Each `yield return` becomes one `data: {...}\n\n` SSE frame.

#### "Submit a job via HTTP"

`IJob` and `IMessage` cannot be HTTP-exposed directly — the source generator rejects them at compile time with `WHTTP001`. The recommended pattern is a thin `IRequest<Guid>` wrapper whose handler calls `IPublisher.Enqueue`. Explicit, debuggable, no framework magic:

```csharp
public sealed record EnqueueReport(Guid TenantId) : IRequest<Guid>;

[WarpHttpPost("/reports/generate")]
public sealed class EnqueueReportHandler(IPublisher publisher)
    : IRequestHandler<EnqueueReport, Guid>
{
    public async Task<Guid> HandleAsync(EnqueueReport req, CancellationToken ct)
    {
        var jobId = await publisher.Enqueue(new GenerateReportJob(req.TenantId));
        await publisher.SaveChangesAsync(ct);
        return jobId;                              // returns 200 with the job id JSON
    }
}
```

#### Auth, status codes, groups

`[Authorize]` / `[AllowAnonymous]` on the handler class surface as endpoint metadata, so group-level `RequireAuthorization(...)` composes naturally:

```csharp
[Authorize(Policy = "OrdersWrite")]
[WarpHttpPost("/orders/cancel")]
public sealed class CancelOrderHandler : IRequestHandler<CancelOrder, Unit> { ... }
```

```csharp
app.MapGroup("/api/public")
   .RequireAuthorization("publicPolicy")
   .MapWarpHttp("public");                        // only handlers with Group = "public"
```

Customize the response shape (status code, `Location` header) by implementing `IHttpResponseShape` on the response type — keeps the handler signature clean and the framework coupling localized:

```csharp
public sealed record CreatedOrder(Guid Id) : IHttpResponseShape
{
    public void Apply(HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCodes.Status201Created;
        ctx.Response.Headers.Location = $"/orders/{Id}";
    }
}
```

Multi-attribute is supported for versioning aliases (each attribute needs `Name = "..."`):

```csharp
[WarpHttpPost("/v1/orders", Name = "CreateOrderV1")]
[WarpHttpPost("/v2/orders", Name = "CreateOrderV2")]
public sealed class CreateOrderHandler : IRequestHandler<CreateOrder, OrderDto> { ... }
```

#### Compile-time diagnostics

| Code      | Trigger                                                            |
|-----------|---------------------------------------------------------------------|
| `WHTTP001` | Handler is invalid (request type implements `IJob` / `IMessage`, or handler doesn't implement a recognized request/stream interface). |
| `WHTTP002` | Multi-attribute on a single handler without `Name = "..."` on every attribute. |

#### Response semantics

| Handler kind            | Status | Body                                       |
|-------------------------|--------|--------------------------------------------|
| `IRequest<TResponse>`   | 200    | JSON of `TResponse`                        |
| `IRequest<Unit>`        | 204    | empty                                      |
| `IStreamRequest<T>`     | 200    | `text/event-stream` (one `data:` per item) |

Full docs at [features/http](features/http). Shipped in [#154](https://github.com/moberghr/warp/pull/154).

### Test Suite Improvements

- **HTTP request-isolation suite** — five concurrency tests bombard the in-memory test app with 200 parallel requests (50 for streaming) and assert per-request DI scope (N requests → N distinct `ScopeProbe` constructions), no `AsyncLocal<T>` bleed between handlers, no request-payload crossover, pipeline behaviors observe per-request inputs, and concurrent `IStreamRequest` endpoints emit only their own block of items.

## 0.11.0

*2026-05-06*

Stability release. No breaking API changes. Several latent product bugs surfaced as flakes during a test-infrastructure refactor and are fixed here; pause's "not instant" semantics are now spelled out in the public API surface.

### Improvements

- **Pause is documented as heartbeat-driven** — `IServerCommandService.PauseServer` / `PauseWorkerGroup` only stamp `PausedAt` on the DB row. Each server's worker pool keeps fetching until that server's next `Heartbeat` tick (cadence `HealthCheckInterval`, default 3s) refreshes its in-memory `PauseStateHolder`, and an in-flight worker iteration that already passed its pause check will still complete its current claim. Treat pause as "no new fetches after up to one heartbeat", not as a synchronous barrier — callers needing hard quiesce semantics combine pause with a wait of `HealthCheckInterval + PollingInterval`. The previous docs implicitly suggested instant propagation; the type docs now match the actual behavior.
- **`HealthCheckInterval` is now nullable** — `WarpWorkerConfiguration.HealthCheckInterval` is `TimeSpan?`. Set it to `null` to disable the auto `Heartbeat` loop entirely (the task stays DI-resolvable for manual invocation). Useful for tests that need to drive heartbeat ticks deterministically; existing values continue to work unchanged.
- **MIT LICENSE file** — repository root now ships a proper `LICENSE` file alongside the `MIT` `PackageLicenseExpression` already present in NuGet metadata, and the `README` has a license section pointing at it ([#149](https://github.com/moberghr/warp/pull/149)).

### Bug Fixes

- **Dispatcher shutdown is now exception-safe** — `WarpDispatcher.ExecuteAsync` wraps its loop in `try/finally` so the channel writer is always completed and the per-host registration disposed, even when an unexpected exception escapes the loop body. Without this, a non-`OperationCanceledException` slipping through the catch could leave dispatcher workers blocked on `WaitToReadAsync` until `IHostOptions.ShutdownTimeout` (default 30s) fired, masking the real failure. Closes the `DispatcherShutdownIntegrationTests` flake from [#151](https://github.com/moberghr/warp/pull/151).
- **`WarpServerRegistration.StopAsync` cleanup uses a fresh CTS** — the cancellation token passed to `StopAsync` is often already cancelled by the time host shutdown reaches the registration's stop method; reusing it left the cleanup queries (delete `Worker` / `WorkerGroup` / `Server` rows for this server id) in `(canceled)` state, leaving orphan rows for `ServerCleanup` to find on the next host's tick. Now uses a bounded fresh `CancellationTokenSource` (10s) so graceful cleanup completes regardless of upstream cancellation.
- **`ServerTaskLoop` survives transient SQL hiccups** — `EnsureRegisteredAsync` and the main iteration are now wrapped in `try/catch` with a short backoff. Previously a transient `SqlException` (e.g., a "session busy" / "severe error" under load) escaping these methods would crash the whole `ServerTaskHost` because `BackgroundServiceExceptionBehavior=StopHost` is the .NET default. Errors are logged and retried on the next iteration.
- **`MessageRouter` fires push notifications for routed children** — when a `Kind=Message` job fans out into N `Kind=Job` children, the router now captures the pending `JobEnqueued` notifications and fires them after `SaveChanges`. The dispatcher (when `UseDatabasePush()` is enabled) wakes immediately instead of waiting for the next poll. Idle-to-burst pickup latency for messages with many handlers drops from ~`PollingInterval` to &lt;50ms.
- **`DeleteJob` / `RequeueJob` no longer race the worker keep-alive** — `IWarpSqlQueries` previously had two lock-by-id variants: `LockJobByIdAsync` (with `READPAST` / `SKIP LOCKED`) for user commands and `LockJobByIdWaitAsync` (blocking) for orchestration. The `READPAST` variant raced the worker's brief keep-alive UPDATEs and would falsely return "job not found" while the row was being touched. The two methods are now unified onto the blocking `LockJobByIdWaitAsync`; the wait is bounded by the keep-alive's own commit, so it's microseconds in practice.
- **Per-host `DispatcherRegistry` replaces process-wide static** — `WarpDispatcher`'s notification-wakeup signal list was previously a `static List<>` on the class. With multiple `IHost`s in one process (typical in integration tests, possible in some embedded scenarios) the static cross-signaled dispatchers across host boundaries and held references to disposed dispatchers' semaphores. Now a DI singleton scoped to each host's container, registered automatically by `AddWarpWorker`.

### Test Suite Improvements

- **Per-class `IClassFixture` topology** — integration tests no longer share a single `WarpTestServer` per "shard" fixture; each test class gets its own database and each test boots its own server inside the test body. Eliminates whole categories of cross-test interference (leftover `Server` rows poisoning `ServerCleanup`, prior-test mid-flight jobs racing the next test's assertions, SQL Server connection-pool poisoning across server-replacement scenarios). The `[GenerateDatabaseTests(...)]` source generator now emits `[Xunit.IClassFixture<>]` per concrete subclass instead of `[Collection(...)]`.
- **Service Broker is opt-in** — SQL Server Service Broker is no longer always-on for every `_SqlServer` test. Tests that exercise DB push opt in via `[GenerateDatabaseTests(WithPush = true)]`, which routes them to the dedicated `SqlServerPushClassFixture`. Saves significant setup time for the polling-only suite.
- **Diagnostic dump on integration-test failure** — `FixtureDiagnostics.DumpAsync` runs on every failed integration test and prints stuck-job state, recent `ServerTask` / `ServerLog` rows, and a process-wide `ServerLifecycleTrace` of `IHost.StartAsync` / `StopAsync` events. The lifecycle trace is critical because `WarpServerRegistration.StopAsync` deletes its own `Server` row on graceful shutdown, so post-mortem queries against that row return nothing — the in-memory trace is the only source of truth for "did this server actually finish booting / shut down cleanly?" ([#147](https://github.com/moberghr/warp/pull/147), [#151](https://github.com/moberghr/warp/pull/151)).
- **`WarpTestServer.RunHeartbeatOnceAsync`** — test helper that resolves `Heartbeat<TestContext>` in a fresh scope and calls `ExecuteAsync` directly, sidestepping the `ServerTaskHost` auto-loop. Lets tests that disable `HealthCheckInterval` flip `PauseStateHolder` deterministically. Used by the rewritten `PauseServer_JobsStayEnqueued` / `PauseWorkerGroup_JobsStayEnqueued` tests.
- **Full suite stability** — 1,025 tests, ran 5 consecutive full-suite passes with zero flakes (1m 03s – 1m 11s) before tagging this release.

### Website & Docs

- **Enterprise landing page redesign** — new home page with a Moberg-aligned enterprise aesthetic, replacing the prior tagline-driven layout ([#148](https://github.com/moberghr/warp/pull/148), [#150](https://github.com/moberghr/warp/pull/150)).

---

## 0.10.0

*2026-04-27*

The library has been **renamed from Jobly to Warp**. NuGet package IDs change from `Moberg.Jobly.*` to `Moberg.Warp.*`, public types/namespaces from `Jobly.*` to `Warp.*`, default schema from `"jobly"` to `"warp"`, and the dashboard URL from `/jobly` to `/warp`. The old `Moberg.Jobly.*` packages will be deprecated on nuget.org with pointers to the new IDs.

### New Features

- **Auto handler registration** — Handlers and pipeline behaviors register themselves. The `Warp.SourceGenerator` now emits a `[ModuleInitializer]` per consumer assembly that pushes its handler / pipeline-behavior DI registrations into a process-level `WarpGeneratedHandlerRegistry` at assembly load. `AddWarp` replays the registry onto the user's `IServiceCollection` — so nothing else is needed.

  ```csharp
  // before
  services.AddHandlers(typeof(Program).Assembly);
  services.AddPipelineBehaviors(typeof(Program).Assembly);
  services.AddWarp<AppDbContext>(opt => opt.UsePostgreSql());

  // after
  services.AddWarp<AppDbContext>(opt => opt.UsePostgreSql());
  ```

  Works across solution boundaries — each assembly with handlers only needs the `Warp.SourceGenerator` analyzer reference (transitively present via `Moberg.Warp.Core`).

### Improvements

- **Generator covers all behavior kinds** — `IPipelineBehavior<,>` and `IStreamPipelineBehavior<,>` now flow through the source generator alongside `IPublishPipelineBehavior<>` (previously only publish behaviors did, the rest relied on the reflection scan). Open-generic implementations are emitted through the `services.AddTransient(typeof(IFace<>), typeof(Impl<>))` overload, matching the semantics the reflection path provided.
- **`IMessageHandler<T>` multi-handler fix** — The generator's per-message-type handler map was a `Dictionary` that silently overwrote earlier handlers when a message had multiple subscribers. Now it collects them all so pub/sub with N handlers registers N `AddTransient` entries and produces N child jobs — the behavior the reflection path already had, now available to the source-generated path.
- **Scoped behavior scan** — The generator's behavior scan is restricted to the *current* compilation (was walking referenced assemblies via `GetAllTypes`). Core's opt-in addon behaviors (`MutexPipelineBehavior<,>`, `RetryPipelineBehavior<,>`, `CircuitBreakerPipelineBehavior<,>`, `NoRestartPublishBehavior<>`) are still registered only by the explicit `AddMutex` / `AddRetry` / `AddCircuitBreaker` / `AddNoRestart` calls. Core's own compilation short-circuits generation entirely.

### Bug Fixes

- **SQL Server push setup stall is now cancellable** — `SqlServerNotificationTransport.PublishAsync` and `ListenAsync` now await the cached `_setup.Value` via `Task.WaitAsync(ct)` instead of a raw `await`. A stalled broker setup (e.g., schema lock contention on a busy SQL Server) no longer blocks every caller indefinitely — each caller's `CancellationToken` bails out of the wait without invalidating the cached setup task. The next caller with a live token re-awaits and proceeds. This closes the class of intermittent `"Test execution timed out after 30000 milliseconds"` failures in `SqlServerDatabasePushIntegrationTests` under heavy CI contention.
- **Race in `ServerTaskLoop.Signal` fixed** — `Signal()` had a check-then-act TOCTOU on its `SemaphoreSlim(0, 1)`: two threads could both pass `CurrentCount == 0` and both call `Release()`, second throwing `SemaphoreFullException`. Surfaced under dispatcher-mode contention (many workers calling `SignalJobFinalized` concurrently as batches flush) as `"Adding the specified count to the semaphore would cause it to exceed its maximum count"` and cascading downstream failures in the affected task loop. Fixed with a lock around the check-and-release — same pattern `WarpDispatcher.SignalAll` already used. Regression test runs 32 threads × 500 calls concurrently and asserts no exception.

### Test Suite Improvements

- **`TimedFact` default 30s → 10s** — Individual tests should finish in seconds; the 30s default was a band-aid for overly generous inner waits and could hide real hangs. Tests exercising deliberately slow behaviour (retry chains, multi-job integration workloads, two-server orchestration) opt in explicitly with `[TimedFact(N_000)]`. Twenty-three inner-wait timeouts (retry / cancellation / batch / continuation tests) were tightened from 15–30s to 5–10s to match actual runtime, with comfortable headroom for CI jitter.
- **Durability tests for "no job left unprocessed"** — Added three integration tests covering the core recovery guarantees that had no prior coverage:
  - `DispatcherShutdownIntegrationTests.GivenWorkInProgress_WhenServerReplaced_ThenAllJobsEventuallyComplete` — pod-rolling-restart scenario. Server A is disposed mid-flight, server B takes over the same queue; every enqueued job must reach `Completed` via whichever recovery path fires (`UnclaimUndelivered`, channel drain, or `StaleJobRecovery`).
  - `PushFailurePollingBackstopTests.GivenPushEnabledButTransportBroken_WhenJobEnqueued_ThenPollingStillPicksItUp` — proves that when the notification transport is completely broken (both `PublishAsync` and `ListenAsync` throw), polling still delivers the job within a small multiple of `PollingInterval`. Protects the "polling is the correctness backstop for push" invariant.
  - `ListenerReconnectDrainTests.GivenListenerAlwaysFails_WhenJobEnqueued_ThenReconnectDrainStillDelivers` — proves that `NotificationListenerTask.DrainSignals` fires on every reconnect iteration, waking the dispatcher even while the listener connection is permanently down. Jobs enqueued during the listener's offline window are not stranded.
- **`WorkerHostMode` lifecycle smoke tests deflaked** — two `*_CompletesLifecycleWithoutThrowing` tests were occasionally hitting the `TimedFact` budget on SQL Server CI under shared-container contention. Pre-cancel the token passed to `StartAsync` / `StopAsync` so each `BackgroundService.ExecuteAsync` short-circuits on its first `stoppingToken` check — DI wiring + constructors are still exercised, just without the polling loop. Local SQL Server: 360ms → 16ms.
- **SQL Server integration test deflakes** — two test-setup artifacts that surfaced as ~25–50% flake rates on shared SQL Server CI runners. (1) Per-test-server SqlConnection pool isolation: each `WarpTestServer` now gets a unique `Application Name` (which Microsoft.Data.SqlClient includes in the pool key) so a disposed server's cancel-poisoned connections never reach the replacement server's pool — this mirrors production pod-restart, where new process = new pool. Skipped on Npgsql to avoid blowing past `max_connections=100`. (2) The auto `CounterAggregator` 5s sweep was racing test assertions that read `Counter` rows directly; `CounterAggregationInterval` is now `null` in test defaults — every test that needs aggregation already triggers it explicitly via `TestTasks.CreateCounterAggregator`.
- **Full suite runtime** — now **~1m 30s** for 1,024 tests (was ~2m 40s for 947 in 0.8.0), down primarily from the inner-wait tightenings and the shorter `TimedFact` default.

### Migration

Breaking release because of both the rename and the removal of reflection-based registration helpers.

- **Switch to `Moberg.Warp.*` packages** — `Moberg.Jobly.Core` → `Moberg.Warp.Core`, `Moberg.Jobly.UI` → `Moberg.Warp.UI`, `Moberg.Jobly.Worker` → `Moberg.Warp.Worker`, `Moberg.Jobly.Provider.PostgreSql` → `Moberg.Warp.Provider.PostgreSql`, `Moberg.Jobly.Provider.SqlServer` → `Moberg.Warp.Provider.SqlServer`. The old IDs are deprecated on nuget.org but remain restorable.
- **Update namespaces, types, and method calls** — `Jobly.*` → `Warp.*` across all `using` directives and types. `AddJobly` → `AddWarp`, `IJoblyLockProvider` → `IWarpLockProvider`, `IJoblyCredentialValidator` → `IWarpCredentialValidator`, etc. A solution-wide find/replace of `Jobly` → `Warp` (case-sensitive) plus `jobly` → `warp` (lowercase, for env vars / strings) covers it.
- **Database schema** — default schema is now `"warp"` instead of `"jobly"`. Existing deployments must either rename the schema (`ALTER SCHEMA jobly RENAME TO warp;` on PostgreSQL; equivalent on SQL Server) or set `options.Schema = "jobly"` explicitly to keep the old name.
- **Dashboard URL** — `/jobly` → `/warp`. Update any reverse proxy / ingress rules and bookmarked links.
- **Drop the reflection calls** — if your `Program.cs` or test setup calls any of these, delete them:
  ```csharp
  services.AddHandlers(assembly);
  services.AddJobHandlers(assembly);
  services.AddMediatorHandlers(assembly);
  services.AddPipelineBehaviors(assembly);
  ```
  `AddWarp` replaces all of them for consumers that go through the normal DI path.
- **Test harnesses that build `ServiceCollection` manually** — if you construct an `IServiceCollection` directly (e.g. a unit test that doesn't go through `AddWarp`), call `services.AddWarpMediator()` from the generated `Warp.Core.Handlers.Generated` namespace. It stays public as an escape hatch and is idempotent with `AddWarp`.
- **Shared-handler assemblies need the analyzer** — if handlers live in a library that doesn't already reference `Moberg.Warp.Core`, add the source generator as an analyzer so its `[ModuleInitializer]` fires on load:
  ```xml
  <ProjectReference Include="path/to/Warp.SourceGenerator.csproj"
                    OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
  ```
  In practice most consumers already pick this up transitively from `Moberg.Warp.Core`.

---

## 0.9.1

*2026-04-21*

### Bug Fixes

- **Docs site build** — `website/docs/features/db-push.md` was referenced from the 0.9.0 release notes but never added, breaking the Docusaurus production build with `onBrokenLinks: 'throw'`. The page is now present and mirrors the DB Push section of the README.

### Maintenance

- **Dashboard UI dependencies** — `npm audit fix` in `src/ui` patches `vite`, `axios`, `hono`, `@hono/node-server`, and `follow-redirects` (5 advisories, 1 high + 4 moderate). No API-surface changes; dashboard bundle hashes change because Vite re-bundles with updated transitive deps.
- **Docs site dependencies** — Docusaurus 3.9.2 → 3.10.0, added `@docusaurus/faster` (required by 3.10 with `future.v4`), and pinned `serialize-javascript ^7.0.5` via `overrides` to close [GHSA-qj8w-gfj5-8c6v](https://github.com/advisories/GHSA-qj8w-gfj5-8c6v) — the build-chain DoS that Docusaurus's bundler transitively pulled in. `npm audit` now reports 0 vulnerabilities on both frontends.

---

## 0.9.0

*2026-04-21*

### New Features

- **Database Push** — Opt-in push notifications replace polling wake-up for the dispatcher, `MessageRoutingTask`, and `OrchestrationTask`. Uses PostgreSQL `LISTEN`/`NOTIFY` or SQL Server Service Broker natively. Enable via `opt.UseDatabasePush()` inside the `AddWarp` / `AddWarpWorker` lambda. Dispatcher pickup drops from ~500ms to &lt;50ms; burst-and-idle workloads roughly halve wall-clock time. Idle deployments see ~16% fewer SELECTs (empty `FOR UPDATE SKIP LOCKED` fetches during idle go away). Worker-fetch push only wires when `UseDispatcher = true` — individual-worker mode keeps polling to avoid thundering-herd. Zero overhead if you don't opt in. See [DB Push](/docs/features/db-push).
- **`State.Scheduled`** — Future-dated jobs (`Schedule(job, at)`) now land in `State.Scheduled` instead of `State.Enqueued` with a future `ScheduleTime`. `ScheduledJobActivationTask` flips them to `Enqueued` when due and fires a `JobEnqueued` push. Cleans up the worker fetch predicate to a pure `CurrentState = Enqueued` check. Pre-upgrade rows still execute correctly thanks to a defensive `ScheduleTime <= now` filter.
- **Per-database Provider Packages** — Provider-specific code moves out of `Warp.Core` / `Warp.Worker` into two new NuGet packages: `Moberg.Warp.Provider.PostgreSql` and `Moberg.Warp.Provider.SqlServer`. Install the one that matches your database and opt in via `opt.UsePostgreSql()` / `opt.UseSqlServer()` inside the registration lambda. Core now stays fully provider-agnostic — no `Npgsql` or `Microsoft.Data.SqlClient` references.
- **Builder-based DI API** — `AddWarp<TContext>(opt => ...)` / `AddWarpWorker<TContext>(opt => ...)` take a single lambda over `IWarpBuilder<TContext>`. Config fields live on the builder directly (inherits `WarpConfiguration`); addons chain as extension methods (`opt.AddRetry()`, `opt.AddMutex()`, `opt.AddCircuitBreaker()`, `opt.AddNoRestart()`, `opt.UseDatabasePush()`).

### Improvements

- **Server-task architecture refactor (`IServerTask`)** — Every background task (`Heartbeat`, `ServerCleanup`, `StaleJobRecovery`, `CounterAggregator`, `ExpirationCleanup`, `RecurringJobScheduler`, `ScheduledJobActivation`, `Orchestrator`, `MessageRouter`) is now a plain DI-registered `IServerTask` service driven by a single `ServerTaskHost<TContext>`. Previously each task was a `BackgroundService` subclass of `ServerTaskBase` — the split separates domain logic from hosting mechanics. Task inner methods that were `public static` for test access are now `internal` instance methods, closing a class of lock-bypass bugs where tests and operators could accidentally invoke work outside the distributed lock.
- **Disable cleanup tasks at the config level** — `CounterAggregationInterval`, `ServerCleanupInterval`, `StaleJobRecoveryInterval`, `ExpirationCleanupInterval`, and `RecurringJobSchedulerInterval` are now `TimeSpan?`. Set any to `null` and the host won't auto-run that task's loop. Useful for multi-tenant setups where you want only one server running cleanup.
- **Signal plumbing consolidated** — `Orchestrator` and `MessageRouter` wake on push events via a single `ServerTaskSignals<TContext>` singleton with named `SignalJobFinalized` / `SignalMessageEnqueued` methods. Tasks declare their subscriptions via `IServerTask.Signals`. Replaces the old static `_instances` lists. No user-visible behaviour change.
- **Heartbeat no longer writes a `ServerLog` row per run** — under the old `ServerTaskBase` default, every server wrote a heartbeat success row every 3s. Now explicit per task via `LogOnSuccess`; heartbeat opts out. Failed heartbeats still log.
- **Faster CI builds** — Test workflow builds only `tests/Warp.Tests/Warp.Tests.csproj` instead of the full solution. Benchmarks, mutation tests, and demo apps no longer built in the test job; they come in transitively only where tests reference them.
- **Atomic-claim fetch** — Worker and dispatcher fetch are now `UPDATE ... RETURNING` (PG) / `UPDATE ... OUTPUT INSERTED.*` (SQL Server) via new `IWarpSqlQueries<TContext>` implementations in the provider packages. Closes the SELECT→UPDATE race window that produced rare double-claims under concurrent-worker load. The old regex-based `RowLockInterceptor` is retired.
- **Shutdown safety** — Three races that could leave jobs as `State=Processing` orphans on shutdown are fixed: `WarpDispatcher` un-claims rows it fetched but didn't deliver; `WarpDispatcherWorker` drains its channel fully before exiting; post-handler bookkeeping uses `CancellationToken.None` so a job whose handler already ran can't be abandoned mid-finalize.
- **Retry / CircuitBreaker respect `State.Scheduled`** — Delayed retries and circuit-breaker reschedules land in `State.Scheduled` when the target time is in the future. New `JobOutcome.RescheduledState(scheduleTime, now)` helper is shared by both pipeline behaviors. No change for immediate retries.
- **Worker host split** — `WarpWorkerSetup` is replaced by three DI-registered `IHostedService`s: `WarpServerRegistration`, `WarpDispatcherHost`, `WarpSingleWorkerHost`. Each mode no-ops when the other is selected via `UseDispatcher`. State flows via a new `ServerRegistrationState` singleton instead of re-querying.
- **Source layout** — Libraries under `src/core/`, providers under `src/core/providers/`, tests in `src/tests/`, demo apps in `src/demo/`. `Directory.Build.props` scoped to match.

### Bug Fixes

- **Queue-name encoding collision (SQL Server)** — `JobHelper` now rejects queue names containing the unit-separator (`\x1f`) that SQL Server's `STRING_SPLIT` uses internally for encoding. Previously a job published to a `\x1f`-containing queue could be delivered to the wrong worker group.

### Migration

Breaking release because of the provider package split and the DI lambda API.

- **Install a provider NuGet**: add `Moberg.Warp.Provider.PostgreSql` or `Moberg.Warp.Provider.SqlServer` alongside `Moberg.Warp.Core`. The provider package registers the row-lock / atomic-claim queries, the exception classifier, and the notification-transport factory — all of which used to live in Core.
- **Wrap registration in a lambda + call the provider**:
  ```csharp
  // before
  services.AddWarp<MyContext>();
  services.AddWarpDatabasePush<MyContext>();   // if using push

  // after
  services.AddWarp<MyContext>(opt =>
  {
      opt.UsePostgreSql();                      // or UseSqlServer()
      opt.UseDatabasePush();                    // if using push, now chains on the builder
  });
  ```
- **`AddWarpDatabasePush<TContext>()` removed** — call `opt.UseDatabasePush()` on the builder instead. Must be after `UsePostgreSql` / `UseSqlServer`.
- **`State.Scheduled` is new** — Future-dated jobs existing from a previous version still execute correctly (worker fetch has a defensive `ScheduleTime <= now` predicate) but won't show in the dashboard's Scheduled list until their time arrives.
- **Retry / CircuitBreaker reschedules land in `State.Scheduled` when delayed** — dashboard filters and any external tooling that queried `Enqueued + future ScheduleTime` should now also look at `Scheduled`.

---

## 0.8.0

*2026-04-19*

### New Features

- **Exponential Polling Backoff** — Workers and the batch-fetch dispatcher now back off geometrically when queues are idle, reducing database load during quiet periods. Configure via `MaxPollingInterval` (default `30s`, ceiling) and `PollingIntervalFactor` (default `2.0`, multiplier). `PollingInterval` becomes the floor. On any processed job, the delay resets to the floor instantly, so throughput under load is unchanged. Paused workers stay at the floor (no compounding while paused). Available on both top-level `WarpWorkerConfiguration` and per-group `WorkerGroupConfiguration`. Set `PollingIntervalFactor = 1.0` to disable backoff. See [Operations → Configuration → Exponential Polling Backoff](/docs/operations/configuration#exponential-polling-backoff).
- **Retry Jitter** — New `JitterFactor` option on `RetryOptions` applies multiplicative random jitter to each computed retry delay: `delay * (1 + JitterFactor * rand(-1, 1))`. Clamped to `[0, 1]`. Global only — no per-job override. Defaults to `0.0` (no jitter) so existing behavior is unchanged. Use to spread retry attempts and avoid thundering herds when many jobs fail at once (e.g. downstream outage). The actual jittered `ScheduleTime` is recorded in the `Requeued` JobLog entry so operators can diagnose from the dashboard.
- **NoRestart (stale-recovery opt-out)** — New addon `AddWarpNoRestart()` lets specific job types stay `Failed` on worker crash instead of being auto-requeued. Apply with `[NoRestart]` / `[Restart]` attributes on the job class (inherits through the class hierarchy), `.WithRestart(bool)` per-enqueue, or flip the global default with `RestartStaleJobsByDefault = false`. Override chain: per-publish > attribute > global. For non-idempotent work (payments, emails, webhooks). See [NoRestart](/docs/features/no-restart).
- **Circuit Breaker** — New addon `AddWarpCircuitBreaker<TContext>()` stops hammering a failing downstream when failures cross a threshold. Opens after `Threshold` consecutive failures, stays open for `Duration`, then transitions to a **half-open state with an atomic probe gate** — exactly one worker probes while others reschedule, preventing thundering herd on recovery. Customise per-handler via `[CircuitBreaker(Group = "...", Threshold = N)]`. Adds a `CircuitBreakerState` entity to your DbContext (migration required). See [Circuit Breaker](/docs/features/circuit-breaker).
- **Batched Completions (Dispatcher Mode)** — When `UseDispatcher = true`, each worker now buffers job completions in memory and commits them as a single multi-row transaction, collapsing N per-job commits into one. Tune via `CompletionBatchSize` (default `50`) and `CompletionFlushInterval` (default `100ms`); set `CompletionBatchSize = 1` to opt out. Poison-entry isolation: a single bad row in a batch of 50 is dropped via recursive split; the other 49 still commit. SIGTERM mid-flush is safe — `FlushAsync` commits to completion using `CancellationToken.None` internally. Trade-off is at-least-once semantics; pair with `[NoRestart]` for non-idempotent handlers. See [Batched Completions](/docs/features/batched-completions).
- **Configurable Log Flush Interval** — New `LogFlushInterval` on `WarpWorkerConfiguration` (default `1s`) controls how often the job monitor drains handler `ILogger` output into the JobLog table. Lower values surface dashboard logs faster at the cost of more DB writes.

### Improvements

- **Resilient worker/dispatcher exception handling** — `WarpWorker` now catches transient exceptions in its poll loop (matching the dispatcher), so a single DB hiccup or handler pipeline fault no longer silently terminates the BackgroundService. The exception path uses a fixed floor delay instead of compounding the polling backoff, so jobs resume within `PollingInterval` of recovery instead of sitting at `MaxPollingInterval` for 30s.
- **`[NoRestart]` and `[Restart]` attributes are inherited** — Declare the policy on a base class (e.g. `PaymentJobBase`) and every derived concrete job inherits it. A derived class with its own attribute overrides the base — closest direct declaration wins.
- **Circuit breaker exception handling narrowed** — The `DbUpdateException` catch in `CircuitBreakerStore.RecordFailureAsync` now only suppresses unique-constraint violations (Npgsql `23505`, SqlClient `2627`/`2601`). CHECK, FK, and column-length violations propagate instead of being silently swallowed.
- **Test suite 48% faster, flake-free** — Disabled idle-polling backoff inside the test server, tuned `StaleJobRecoveryInterval` for tests only, made `LogFlushInterval` configurable, and bumped the `TimedFact` default from 10s to 30s. Full suite (947 tests) runs in ~2m 39s (was ~5m 05s). Eliminated the latent `TimedFact`/`WaitForJobState` timeout-mismatch class of flakes.

### Bug Fixes

- **Dispatcher SIGTERM completion-loss** — Pre-fix, `CompletionBatch.FlushAsync` drained its buffer before observing cancellation, and a shutdown mid-flush silently dropped the drained batch. Now commits with `CancellationToken.None` internally.
- **Circuit breaker thundering herd on half-open** — Without the new HalfOpen CAS, every worker polling when `OpenUntil` lapsed fired a concurrent probe against the recovering downstream. Fix guarantees exactly one probe fires per recovery window.
- **Retry jitter `ScheduleTime` not logged** — The `Requeued` JobLog entry now includes `(next attempt scheduled: <ISO timestamp>)` so operators can see the actual delay jitter applied.
- **MultiServer cross-test flakiness** — A too-aggressive `StaleJobRecoveryInterval` in the test fixture raced worker keep-alive refreshes under two-server SQL Server load, producing sporadic `DbUpdateConcurrencyException`.

### Migration

- **Circuit Breaker migration** — If you register `AddWarpCircuitBreaker<TContext>()`, a new `CircuitBreakerState` entity is added to your DbContext. Run an EF Core migration to create the table (`warp.circuit_breaker_state` under default schema + snake_case naming).
- **`CompletionBatch.FlushAsync` parameter removal** — If you were calling `CompletionBatch.FlushAsync(CancellationToken)` directly (rare — it's internal to `Warp.Worker`), the parameter has been removed. The method now always commits to completion.
- **`[NoRestart]` / `[Restart]` now inherit** — Behaviour change: a base class decorated with `[NoRestart]` now affects every derived concrete job. If you relied on the pre-release `Inherited = false` behaviour, add `[Restart]` on the specific derived types you want to opt back in.
- **`StaleJobRecoveryTask.RequeueStaleJobs` removed** — Replaced by `RecoverStaleJobs` returning `StaleJobRecoveryResult` (includes `Requeued`, `Failed`, `Deleted` counts). No `[Obsolete]` bridge; callers must migrate to the new signature.

---

## 0.7.0

*2026-04-17*

### New Features

- **Stream Requests** — New `IStreamRequest<TResponse>` pattern for lazy, item-by-item streaming via `IAsyncEnumerable<TResponse>`. Extends `IRequest<IAsyncEnumerable<TResponse>>` to preserve the unified type hierarchy — `IPipelineBehavior` applies automatically at the request level. New `IStreamPipelineBehavior<TRequest, TResponse>` wraps the actual enumeration for per-item concerns (timing, transforms). Resolved via `IMediator.CreateStream()`. Source generator provides zero-allocation dispatch.
- **Addon Architecture** — New `Outcome` on `IJobContext` (formerly `FailureOutcome`) lets pipeline behaviors control what happens on both success and failure. The worker is a generic state machine that applies the pipeline's decision. Combined with typed metadata and publish pipeline behaviors, this enables building composable addons (retry, mutex, dead letter queue, circuit breaker) entirely on top of Warp's public API. See [Building Addons](https://github.com/moberghr/warp/blob/main/docs/guides/building-addons.md).
- **Retry Addon** — Retry logic extracted from the worker into an opt-in module at `Warp.Core.Retry`. Declare retry policy with `[Retry(3)]` on either the handler or the job class, override per-enqueue with `new JobParameters().WithRetry(maxRetries: 5)`, or set global defaults via `services.AddWarpRetry(o => { o.MaxRetries = 3; o.Delays = [15, 60, 300]; })`. Priority: per-enqueue metadata > handler attribute > job attribute > global options.
- **Mutex Addon** — Mutex extracted from the worker hot path into an opt-in module at `Warp.Core.Mutex`. Register via `services.AddWarpMutex()`. Set keys with `new JobParameters().WithMutex("payment:123")` or `[Mutex("payment-processing")]` on the job class. Uses the new `IWarpLockProvider` abstraction for distributed locking.
- **Typed Metadata** — Access job metadata through strongly-typed interfaces. Define an interface extending `IJobMetadata`, and read it in handlers via `ctx.GetMetadata<IMyMetadata>()` or configure it at publish time with `new JobParameters().Configure<IMyMetadata>(m => m.CustomerName = "John")`. The source generator produces dictionary-backed implementations. `MetadataSerializer` uses native JSON deserialization for round-trip fidelity (integers stay as `long`, arrays as `List<object>`).
- **Recurring Job Enable/Disable** — Disable a recurring job to temporarily stop it from creating new jobs. The scheduler still fires on schedule but records a "Skipped" entry in the execution history. Re-enabling resumes from the next natural cron occurrence with no catchup burst. API: `POST /api/recurring/{id}/enable|disable`. Dashboard shows Enabled/Disabled badges and Skipped entries in history.
- **Worker Scope Isolation** — Worker and handler now use separate DI scopes. The handler's DbContext lives in its own scope — on failure, the scope is disposed and tracked entities are discarded. No partial handler work leaks into the worker's save. On success, handler changes are committed first (outbox pattern), then Warp state.
- **Extensible Dashboard UI** — New `IWarpUIExtension` interface lets external NuGet packages extend the dashboard without forking. Extensions ship an ES-module as an embedded resource, served at `/warp/_ext/{name}/`. The SPA dynamically imports each module and calls `install(warp)`. Extensions target `data-warp-slot` elements with `mount` / `append` / `insertBefore` / `insertAfter` operations, or register whole new pages via `addPage()`. React, ReactDOM, Axios, and shadcn components are exposed on `window.Warp` so extensions don't bundle them. The built-in `RetryUIExtension` is the reference implementation — renders a retry progress card with attempts/max and next-delay info on the job detail page.

### Improvements

- **Handler Registration Split** — `AddHandlers(assembly)` replaces the old `AddJobHandlers`. New granular methods: `AddJobHandlers` (job + message handlers only), `AddMediatorHandlers` (request + stream handlers only). `AddHandlers` calls both.
- **Dispatcher Split** — `JobDispatcher` (worker job execution) and `MediatorDispatcher` (in-memory request/stream dispatch) are now separate classes with independent method caches.
- **xUnit v3 + Microsoft Testing Platform** — Test suite migrated to xUnit v3 with `UseMicrosoftTestingPlatformRunner`. New `[TimedFact]` / `[TimedTheory]` attributes enforce a 10-second default timeout per test, surfacing deadlocks and hangs globally.
- **Server Memory Benchmarks** — New benchmark project at `src/benchmarks/Warp.ServerBenchmarks/` with four benchmarks (`ScopeMemoryBenchmark`, `WorkerMemoryBenchmark`, `ServerMemoryBenchmark`, `MemoryStressTest`) and a custom `TotalAllocatedDiagnoser` that tracks allocations across all threads. Baseline: ~50 KB per job regardless of scale; 100K-job stress test shows 0.3 MB retained growth (no leak) at 420–496 jobs/sec steady throughput. Documented in [Operations → Benchmarks](/docs/operations/benchmarks).
- **Mutation Testing** — New `Warp.Tests.Mutation` project with an in-memory SQLite fixture runs 293 tests in ~10 seconds, enabling a full Core mutation run in ~30 minutes via `dotnet-stryker`. Baseline scores: **Core 99.60%** (743 killed / 3 survived), Worker 51.53%. Fixed a `RecurringJobPublisher` race condition surfaced during mutation analysis — `AddOrUpdateRecurringJob` now uses `IWarpLockProvider` to prevent duplicate inserts on concurrent calls.
- **.slnx Solution Format** — Migrated from `src/Warp.sln` to `src/Warp.slnx` (XML-based solution format).

### Bug Fixes

- **Recurring job race on concurrent update** — `RecurringJobPublisher.AddOrUpdateRecurringJob` could insert duplicate rows when called concurrently with the same name. Now uses `IWarpLockProvider` for exclusive access during the upsert.
- **Trace page group node highlighting** — Fixed group node highlighting and edge behavior in the trace visualization page.

### Migration

This is a large release with several breaking changes. Plan the upgrade accordingly.

- **Retry is opt-in** — Add `services.AddWarpRetry()` to enable retries. Without it, failed jobs go directly to `Failed`. Replace the removed `maxRetries` publisher overloads and `WarpConfiguration.RetryCount` with `[Retry(n)]` attributes, `new JobParameters().WithRetry(n)`, or the global options callback.
- **Mutex is opt-in** — Add `services.AddWarpMutex()` to enable mutex enforcement. The `JobParameters.Mutex` property is removed — use `.WithMutex("key")` or `[Mutex("key")]` instead. The `ConcurrencyKey` column on the `Job` entity is removed (keys now live in metadata).
- **Typed metadata API** — `IJobContext<T>` / `JobContext<T>` are removed. Read typed metadata via `ctx.GetMetadata<IMyMetadata>()` and configure at publish via `new JobParameters().Configure<IMyMetadata>(m => ...)`.
- **Reduced public surface** — `JobHelper`, `JobDispatcher`, `MetadataSerializer`, and EF interceptors are now `internal`. The Retry and Mutex addons demonstrate that everything needed to build addons is available through the public API — no `InternalsVisibleTo` needed.
- **Database migration required** — New `DisabledAt` column on `RecurringJob`, `Skipped` column on `RecurringJobLog`, `ConcurrencyKey` dropped from `Job`. Run an EF Core migration after upgrading.
- **Solution file renamed** — Update build scripts and IDE shortcuts from `Warp.sln` to `Warp.slnx`.

### Stats

- ~770 tests (PostgreSQL + SQL Server) + 293 SQLite mutation tests
- Core mutation score: 99.60% (746 mutants, 743 killed)

---

## 0.6.1

*2026-04-13*

### Bug Fixes

- **Scoped service resolution in lock provider** — `IDistributedLockProvider` singleton factory resolved `DbContextOptions<TContext>` from the root provider, but `AddDbContext` registers it as scoped. This threw `InvalidOperationException` when scope validation is enabled (e.g. `WebApplication.CreateBuilder()` in Development). Fixed by creating a scope inside the factory.

### Code Quality

- **Fix all 401 build warnings** — Zero warnings across the entire solution. Fixes include: regex DoS hardening (NonBacktracking), collection expression simplification, constructor formatting, CancellationToken propagation, string.Equals usage, and async call consistency. All rule suppressions via `.editorconfig` — no `#pragma` or `[SuppressMessage]` attributes.

---

## 0.6.0

*2026-04-12*

### New Features

- **OpenTelemetry Distributed Tracing** — Every job execution creates a `System.Diagnostics.Activity` with W3C-format TraceId, SpanId, and ParentSpanId. Trace context is automatically propagated through job chains: when a handler enqueues a child job, the child's `ParentSpanId` links back to the handler's span. Message routing and batch creation also propagate span context. New `ParentSpanId` column on the Job entity stores the spawner's span ID.
- **Span Attributes** — Job execution spans include OTel semantic convention tags (`messaging.system`, `messaging.destination.name`, `messaging.operation.name`, `messaging.message.id`) and Warp-specific tags (`warp.job.type`, `warp.job.kind`, `warp.job.status`, `warp.job.duration_ms`, `warp.job.retry_count`). Failed spans are marked with `ActivityStatusCode.Error`.
- **Span Events** — Key lifecycle moments recorded as events on the span: `warp.job.completed`, `warp.job.failed` (with exception info), `warp.job.retried` (with retry/max counts), `warp.job.cancelled`.
- **OTel Metrics** — Four `System.Diagnostics.Metrics` instruments via a `Meter` named `"Warp"`: `warp.job.duration` (histogram, ms), `warp.job.active` (up-down counter), `warp.job.completed` (counter with status tag), `warp.job.enqueued` (counter with kind tag). All tagged by queue and type for filtering.
- **Automatic Log Correlation** — `AddWarpWorker` configures `ActivityTrackingOptions` so TraceId, SpanId, and ParentId appear in log output by default. No additional configuration needed.

### Integration

All features are on by default with zero configuration. To export to OTel backends:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource("Warp"))
    .WithMetrics(m => m.AddMeter("Warp"));
```

### Migration

This release adds a new nullable `ParentSpanId` column to the Job table. Run an EF Core migration after upgrading.

### Bug Fixes

- **Distributed lock credential stripping** — Connection string resolution now reads from `DbContextOptions` RelationalOptionsExtension instead of `Database.GetConnectionString()`, which strips passwords via Npgsql `PersistSecurityInfo=false`. Also handles `NpgsqlDataSource` configurations.
- **Server status indicators** — Dashboard servers page now shows heartbeat-based status dots: green (active), red (stale >30s), amber (paused). "Inactive" badge shown when heartbeat is stale.

### Stats

- 658 tests (310 PostgreSQL + 310 SQL Server + 38 unit)

---

## 0.5.0

*2026-04-09*

### New Features

- **Job Metadata** — Attach key-value metadata to jobs at publish time via `JobParameters.Metadata`. Metadata is inherited by child jobs, accessible in handlers via `IJobContext`, and visible in the dashboard. New `IPublishPipelineBehavior<T>` interface for cross-cutting metadata (e.g., adding tenant ID to every job automatically).
- **Pause / Resume** — Pause and resume job processing at the server or worker group level via dashboard or API. Paused workers stop picking up new jobs; in-progress jobs continue to completion.
- **Real-time Handler Logs** — Handler `ILogger` output is now flushed to the database every ~1 second during execution, instead of only after the handler completes. Logs are visible in the dashboard while the job is still processing.
- **Multi-server Integration Tests** — 16 new tests (8 per database) verify distributed coordination: row locks, advisory locks, orchestration, message routing, and mutex enforcement across two independent servers sharing one database.
- **Deterministic Query Ordering** — Job and message fetch queries now use explicit ordering by queue and schedule time, ensuring predictable behavior in multi-server deployments.
- **Naming Convention Support** — Entity configurations respect EF Core naming conventions (e.g., `UseSnakeCaseNamingConvention()`). All Warp tables default to the `warp` schema, configurable via `WarpConfiguration.Schema`.
- **Configurable Handler Logging** — `EnableHandlerLogging` option (default true) to suppress handler `ILogger` output from the JobLog table when not needed. Lifecycle events are always recorded.
- **AI-friendly Documentation** — Added `llms.txt` and `llms-full.txt` for LLM/agent consumption, following the llms.txt convention.

### Improvements

- Sidebar reorganized into logical groups: Patterns, Features, Operations, Dashboard
- Dashboard shows metadata alongside job payload as formatted JSON
- NuGet badges added to README
- Deterministic query ordering for predictable multi-server behavior

### Stats

- 632 tests (316 PostgreSQL + 316 SQL Server)

---

## 0.4.0

*2026-04-08*

### New Features

- **Source Generator** — Zero-allocation mediator and worker dispatch via compile-time source generation. Replaces runtime reflection in `JobDispatcher` for handler discovery and execution.

### Links

- [GitHub Release](https://github.com/moberghr/warp/releases/tag/0.4.0)

---

## 0.3.0

*2026-04-07*

### New Features

- Initial public release with core job processing, message queue, in-memory mediator, dashboard, recurring jobs, batches, cancellation, mutex, crash recovery, and tracing.

### Links

- [GitHub Release](https://github.com/moberghr/warp/releases/tag/0.3.0)
