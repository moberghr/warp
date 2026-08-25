---
sidebar_position: 17
---

import Screenshot from '@site/src/components/Screenshot';

# Queue Metrics (queue-wait & backlog)

Warp already tracks how long each job takes to *run* ([execution metrics](./observability-sinks.md) — `warp.job.execution.*`). Queue metrics track the other half of a job system's health: **how long jobs wait before a worker picks them up**, and **how deep each queue is backed up**.

- **Queue-wait latency** — the time a job spends eligible-but-unclaimed (from the moment it becomes `Enqueued` with `ScheduleTime ≤ now` to the moment a worker claims it). Recorded at the claim site, on the worker hot path, as a single batched Counter write — no extra database round-trip.
- **Backlog depth + oldest-age** — how many eligible jobs are waiting on each queue, and the age of the oldest one. Sampled periodically off the hot path by the `BacklogSampler` server task.

Both are **always on** — no addon opt-in. The dashboard surfaces them in the **Queues** family on the [Counters](/docs/dashboard/health/counters) page; the meters are emitted unconditionally for any OpenTelemetry collector.

<Screenshot light="/img/screenshots/39-counters-queues.png" dark="/img/screenshots/39-counters-queues-dark.png" alt="The Queues counter tab, showing per-queue backlog depth, oldest age and queue-wait percentiles" />

## What you get

### Meters (always emitted)

| Meter | Type | Unit | Tags |
|---|---|---|---|
| `warp.job.queue.wait` | Histogram | ms | `queue`, `application` (when set) |
| `warp.job.queue.depth` | ObservableGauge | `{job}` | `queue`, `application` (when set) |
| `warp.job.queue.oldest_age_seconds` | ObservableGauge | s | `queue`, `application` (when set) |

Meters follow the null-listener pattern — zero cost until a collector subscribes. Tags stay low-cardinality; group/PII is never a meter tag.

### Durable aggregates (dashboard)

Queue-wait folds into `Counter` → `Statistic` rows the same way execution and adapter metrics do (a count, a duration-sum, and a latency histogram per queue), so **avg / p95 / p99 survive raw-row cleanup**. Backlog depth and oldest-age are written as gauge `Statistic` rows by the sampler (overwritten each tick; drained queues reset to 0).

## Queue-wait on the hot path

Queue-wait is measured where a worker flips a job `Enqueued → Processing`. The wait is `claimTime − Job.ScheduleTime` (`ScheduleTime` advances on requeue, so a requeued job's wait is measured from its requeue, not its original enqueue). The Counter rows are added to the **same `SaveChanges` that already writes the "Processing" `JobLog`** — the hot path gains a Counter write, never a query or a round-trip (the worker fetch/execute path stays sacred).

## Backlog sampling (off the hot path)

`BacklogSampler` is an ordinary `IServerTask` (like `CounterAggregator`) that runs every `BacklogSampleInterval` (default 60s), takes a distributed lock, and runs one grouped query:

```
SELECT queue, COUNT(*), MIN(schedule_time)
FROM job
WHERE kind = Job AND current_state = Enqueued AND schedule_time <= now
GROUP BY queue
```

This is served by the existing `(Kind, CurrentState, Queue, ScheduleTime)` index — no new index, no hot-path cost. The result updates the always-on gauges and the `qbacklog:` `Statistic` rows.

## Scheduled jobs and the `(CurrentState, ScheduleTime)` index

Queue-wait is measured from a job's `ScheduleTime`, so for any job that was *scheduled* rather than enqueued immediately — a retry backoff, a webhook retry, a saga timeout, a rate-limit `Wait` reschedule — **the activation task's latency is part of the queue-wait you see on this page.** That makes the scheduling path worth understanding here.

`ScheduledJobActivation` runs one statement every `ScheduledActivationInterval` (default 10s):

```sql
UPDATE job SET current_state = 1
WHERE current_state = 7 AND schedule_time <= now()
RETURNING id, queue, schedule_time
```

### Why it does not filter on `Kind`

The backlog query above filters `kind = Job` and so rides the existing `(Kind, CurrentState, Queue, ScheduleTime)` index. The activation statement **cannot**, because it deliberately does not constrain `Kind`.

`Publisher` parks a delayed `ITimeoutMessage` as `Kind = Message` in `State.Scheduled` — that is how [saga timeouts](./sagas.md) fire. Narrowing the activation predicate to `Kind = Job` so it could reuse the existing index would **strand every saga timeout forever**, with no error and no log line: the rows would sit in `Scheduled` and never activate. A `Kind IN (Job, Message)` variant would work today but re-breaks the moment any future path parks a third kind in `Scheduled`, and it measured roughly 10× slower than the dedicated index anyway.

So the predicate stays broad — `CurrentState` and `ScheduleTime` only — and Warp carries an index shaped to it:

```csharp
job.HasIndex(p => new { p.CurrentState, p.ScheduleTime });
```

:::note Requires a migration

This index is picked up by a standard `dotnet ef migrations add` / `database update`. On a large `job` table, note that a plain `CREATE INDEX` blocks writes for the duration of the build — see the [release notes](../releases.md) for the `CONCURRENTLY` / `ONLINE = ON` workaround.

:::

The SQL Server provider's activation statement carries the identical predicate, so the same index serves both providers.

### What it costs and what it buys

Without a `CurrentState`-leading index the planner falls back to `(Kind, CurrentState, CreateTime)` on `current_state` alone, then heap-fetches **every** row in `Scheduled` and discards the future-dated ones. Measured on 250,000 job rows with 5,675 in `Scheduled`: **31.8 ms and 2,686 buffers per tick, versus 0.35 ms and 4 buffers** with the index, purely from eliminating 2,841 rows fetched-and-discarded.

The write side pays for it: **+17.8% on a bulk insert and +8.9% on a bulk state transition**, which over a job's whole life (one insert plus ~3 transitions) is **≈ 5 µs of database time per job**. Index size is 9.2 MB against 71 MB of existing `job` indexes. The worker's own claim query was re-checked with and without the index — identical plan, no regression.

### When it is *not* worth it

**There is no jobs/sec break-even.** Write cost scales with your job rate; read cost scales with how many rows are sitting in `Scheduled`, which is itself your job rate times the fraction delayed times the average delay. Throughput appears on both sides and cancels.

**The deciding variable is how long work sits in `Scheduled`** — not how fast it moves. Warp's own defaults keep that number high: retry delays are `[15, 60, 300]` seconds, the webhook retry schedule is `[1m, 10m, 1h, 6h]` (a delivery in its six-hour backoff holds a `Scheduled` row for six hours), and saga timeouts and rate-limit `Wait` reschedules park rows there too.

If you run **no retries, no webhooks, no sagas and no scheduled jobs**, nothing accumulates in `Scheduled`, the read cost the index removes was never being paid, and you are buying ~18% insert overhead for nothing. That is a legitimate reason to drop the index from your migration.

Full EXPLAIN plans, buffer counts and the A/B write measurements are in `docs/perf-results.md` in the repository.

## Sinks

Queue-wait respects `WarpConfiguration.JobMetricsSink` ([observability sinks](./observability-sinks.md)) exactly like execution metrics:

- `Database` (default) / `Both` — Counter rows are written; the dashboard's Queues counter family works.
- `Otel` — the Counter writes are **skipped** (the hot-path perf win); the `warp.job.queue.wait` meter still fires, carrying the data to your collector. Use Grafana/Prometheus for percentiles; the dashboard page will have no rows.

The backlog gauges always emit; the `qbacklog:` `Statistic` upsert is likewise skipped under `Otel`.

## Multi-application

When [`ApplicationName`](./applications.md) is set, **queue-wait** is additionally sliced by the **executor** application under a disjoint counter-key namespace (`qwait-app:`), and `application` becomes a meter tag. The per-application slice carries no latency histogram (to bound counter volume), so its percentiles read as 0 — averages and counts are still exact. Pass `?application=<name>` to the API to read a single app's queue-wait.

**Backlog is *not* sliced by application** — it's a queue-global signal. An eligible-but-unclaimed job has no executor yet, and slicing by the *publishing* app would be a per-creator metric, which Warp deliberately omits ([§8.23](./applications.md)). So the `?application=` view reports that app's queue-wait alongside the queue's *overall* backlog depth and oldest-age, and the backlog gauges carry only a `queue` tag.

## API

- `GET {prefix}/api/queues/metrics` — retained as public read API (the dashboard reads the counter aggregates directly). Per-queue rows: `claimedCount`, `avgWaitMs`, `p95WaitMs`, `p99WaitMs`, `backlogDepth`, `oldestAgeSeconds`. Optional `?application=` filter. Resolves in any `AddWarp` process (dashboard-only / publisher-only included) since it reads only the durable aggregates.

## Outcome stats: a metric records an event, current state is a query

The `stats:` counter family answers *what has happened*, and it is **append-only** — nothing rewrites it. A job that failed, was requeued, and then succeeded is counted in both `stats:failed` and `stats:succeeded`, because both events happened.

*What is happening right now* is a different question with a different source: the dashboard tiles and navigation badges count rows in the `Job` table. The two are not redundant. Completed jobs are swept at `JobExpirationTimeout`, so a query can never answer "how many ever succeeded"; and a cumulative counter can never answer "how many are failed at this moment".

This is why a requeue decrements nothing. Earlier versions decremented the source state's counter, which made a lifetime total disagree with the sum of its own hourly buckets (the decrement wrote no bucket row), let the dashboard throughput chart see a negative delta, and left the counter answering roughly the same question as the live query while losing the only thing it uniquely provides.

### The three levels

| Key | Level | Meaning |
|---|---|---|
| `stats:unsuccessful` | umbrella | Every terminal outcome that is not `Completed`. **Derived, not stored** — see below |
| `stats:succeeded`, `stats:failed`, `stats:deleted`, `stats:requeued` | state total | One row per outcome event |
| `stats:{state}-{reason}` | breakdown | Why it happened |
| `stats:retried-jobs` | standalone | **Distinct jobs** that entered retry, not retry events |

`stats:retried-jobs` sits outside the hierarchy on purpose — it counts jobs where every other key counts events. A job retried fifteen times contributes fifteen to `stats:requeued-retry` and one to `stats:retried-jobs`, so the pair answers "how much retrying is happening" and "how many jobs are thrashing" separately. It increments only on a job's **first** retry, detected from the retry attempt count the worker already reads before running the handler — so it costs no extra read and no schema column.

Each state total is written independently of its breakdown, so a reader never sums the parts to get a total. The breakdown usually totals *less* than its state key: an outcome with no attributable cause carries no reason, and that difference is the unattributed remainder — surfaced rather than hidden, since a breakdown that silently fails to add up reads like a bug.

**The umbrella is computed on read as `failed + deleted`, and no `stats:unsuccessful` row is ever written.** Ten different sites move those two keys — both worker paths, `DeleteJob`, `BulkDeleteJobs`, crash recovery's cancel and fail arms, worker cancellation — and a stored umbrella has to be maintained at every one of them or it under-reports. Deriving it puts the definition in one place, where it cannot drift from the totals it sums.

Only `failed` and `deleted` sit under the umbrella. A **requeue is not a terminal outcome** — the same job runs again and lands in one of the other totals — so it is a top-level key, not an unsuccessful one.

### Reasons

`OutcomeReason` is a closed enum, stamped on `JobOutcome` by the pipeline behaviour that made the decision — the only component that knows why. Its wire token is pinned by a test, so renaming an enum member cannot silently rename a live metric key and orphan its history.

| Reason | Token | Written when |
|---|---|---|
| `Retry` | `retry` | Retry backoff scheduled another attempt |
| `RetryExhausted` | `retry-exhausted` | The retry budget ran out; this failure is terminal |
| `Concurrency` | `concurrency` | Mutex / semaphore — `Wait` requeues, `Skip` deletes |
| `RateLimit` | `ratelimit` | Throttled, skipped, or bounced off lock contention |
| `Timeout` | `timeout` | Timeout in `Delete` mode |
| `Saga` | `saga` | Busy, version conflict, missing correlation, or a moot timeout |
| `Manual` | `manual` | Operator requeue from the dashboard |
| `Recovery` | `recovery` | Crash recovery re-queued work whose worker stopped responding |

A custom pipeline behaviour may set `Reason` on any outcome it constructs:

```csharp
_jobContext.Outcome = new JobOutcome
{
    State = State.Deleted,
    Reason = OutcomeReason.RateLimit,
    LogMessage = "Dropped — vendor quota exhausted",
};
```

It is an enum rather than a string on purpose. A free-form reason would let one caller mint a `Statistic` row per tenant, per key, or per URL; the bounded set is what keeps this family a fixed number of keys regardless of traffic.

`Manual` and `Recovery` are the two members not stamped on a `JobOutcome` — no pipeline runs for them. The dashboard requeue paths and crash recovery write their keys directly, using the same tokens so the breakdown reads uniformly.

### An unmapped reason degrades, it does not throw

The token lookup returns the literal `unknown` for a reason it has no mapping for, rather than throwing. This is not defensive habit — a throw there is genuinely unsafe:

Every caller is a finalization site running **inside the job's own `catch`**. Throwing from it would be laundered into a fake handler failure, re-enter finalization from that catch, throw again, and escape before the state is saved. The job would stay `Processing`, crash recovery would requeue it, and it would re-poison itself indefinitely — a metrics-lookup miss would have taken down job processing. Instrumentation never out-throws in Warp, the same rule adapters and the notifier seam follow.

The fallback is a fixed literal rather than anything derived from the unmapped value, so the miss costs one extra bounded key instead of a new key family. A guard test asserts every enum member maps to a distinct token other than `unknown`, so a member added without a token fails the test run — the literal exists for runtime safety, not as a substitute for the mapping.

### Meter

`warp.job.requeued` is a `Counter<long>` emitted unconditionally for **every** requeue — worker finalization (retry, mutex Wait, rate-limit Wait, circuit breaker), dashboard requeues (`reason=manual`), and crash recovery (`reason=recovery`) — tagged `job.type`, `queue`, `reason`, and `application` (when set). The manual and recovery paths emit **after** their transaction commits, so a rollback can never leave the meter above the `stats:requeued` counter it mirrors.

It closes a real gap: concurrency and rate limiting already emitted detailed **spans**, but spans are sampled, so "how many jobs bounced off this mutex in the last hour" was not answerable from telemetry at all. The concurrency and rate-limit **keys** are deliberately not tags — they are unbounded and PII-adjacent, and stay on the span where cardinality does not compound.

### Mutex and semaphore share one reason

Both go through the same behaviour and take the same lock, and the only local discriminator is whether the effective limit is 1 — which an admin limit override can invert. A label derived from it would be wrong exactly when someone is tuning limits, so both report `concurrency`. The distinction you usually want falls out of the outcome anyway: `Wait` mode requeues, `Skip` mode deletes.

### Cost

Flat keys do not scale with throughput — a million jobs a day and a hundred produce the same number of rows, only different values. Each key retains one lifetime row, 168 hourly buckets (7 days), and 83 daily buckets, so the whole family is a few thousand rows regardless of load. A completed job writes exactly what it wrote before this feature existed; only outcomes carrying a reason write the extra breakdown row.
