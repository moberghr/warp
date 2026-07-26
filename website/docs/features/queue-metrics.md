---
sidebar_position: 17
---

import Screenshot from '@site/src/components/Screenshot';

# Queue Metrics (queue-wait & backlog)

Warp already tracks how long each job takes to *run* ([execution metrics](./observability-sinks.md) — `warp.job.execution.*`). Queue metrics track the other half of a job system's health: **how long jobs wait before a worker picks them up**, and **how deep each queue is backed up**.

- **Queue-wait latency** — the time a job spends eligible-but-unclaimed (from the moment it becomes `Enqueued` with `ScheduleTime ≤ now` to the moment a worker claims it). Recorded at the claim site, on the worker hot path, as a single batched Counter write — no extra database round-trip.
- **Backlog depth + oldest-age** — how many eligible jobs are waiting on each queue, and the age of the oldest one. Sampled periodically off the hot path by the `BacklogSampler` server task.

Both are **always on** — no addon opt-in. A per-queue **Queues** page in the dashboard surfaces them; the meters are emitted unconditionally for any OpenTelemetry collector.

<Screenshot light="/img/screenshots/25-queues.png" dark="/img/screenshots/25-queues-dark.png" alt="Queues page showing per-queue backlog depth, oldest age and queue-wait percentiles" />

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

`BacklogSampler` is an ordinary `IServerTask` (like `CounterAggregator`) that runs every `BacklogSampleInterval` (default 15s), takes a distributed lock, and runs one grouped query:

```
SELECT queue, COUNT(*), MIN(schedule_time)
FROM job
WHERE kind = Job AND current_state = Enqueued AND schedule_time <= now
GROUP BY queue
```

This is served by the existing `(Kind, CurrentState, Queue, ScheduleTime)` index — no new index, no hot-path cost. The result updates the always-on gauges and the `qbacklog:` `Statistic` rows.

## Sinks

Queue-wait respects `WarpConfiguration.JobMetricsSink` ([observability sinks](./observability-sinks.md)) exactly like execution metrics:

- `Database` (default) / `Both` — Counter rows are written; the dashboard Queues page works.
- `Otel` — the Counter writes are **skipped** (the hot-path perf win); the `warp.job.queue.wait` meter still fires, carrying the data to your collector. Use Grafana/Prometheus for percentiles; the dashboard page will have no rows.

The backlog gauges always emit; the `qbacklog:` `Statistic` upsert is likewise skipped under `Otel`.

## Multi-application

When [`ApplicationName`](./applications.md) is set, **queue-wait** is additionally sliced by the **executor** application under a disjoint counter-key namespace (`qwait-app:`), and `application` becomes a meter tag. The per-application slice carries no latency histogram (to bound counter volume), so its percentiles read as 0 — averages and counts are still exact. Pass `?application=<name>` to the API to read a single app's queue-wait.

**Backlog is *not* sliced by application** — it's a queue-global signal. An eligible-but-unclaimed job has no executor yet, and slicing by the *publishing* app would be a per-creator metric, which Warp deliberately omits ([§8.23](./applications.md)). So the `?application=` view reports that app's queue-wait alongside the queue's *overall* backlog depth and oldest-age, and the backlog gauges carry only a `queue` tag.

## API

- `GET {prefix}/api/queues/metrics` — per-queue rows: `claimedCount`, `avgWaitMs`, `p95WaitMs`, `p99WaitMs`, `backlogDepth`, `oldestAgeSeconds`. Optional `?application=` filter. Resolves in any `AddWarp` process (dashboard-only / publisher-only included) since it reads only the durable aggregates.
