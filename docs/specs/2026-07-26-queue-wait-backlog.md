# Spec — Queue-wait latency + backlog SLIs

**Date:** 2026-07-26 · **Slug:** `queue-wait-backlog` · **Target release:** `3.7.0`
**Status:** proposed (plan-only; not implemented). Feature 1 of 4 in the observability program — its own slice, does not touch the other three.

## Summary

Warp tracks per-job *execution* duration (`warp.job.execution.*` + `jobstat` Counters, §8.23/§8.24) but not **time-in-queue** or **backlog** — the leading health signals for a job system (a starved/paused/over-subscribed worker pool shows up as rising queue-wait and backlog long before execution metrics move). Add two SLIs:

- **Queue-wait latency** — time a job spent eligible-but-unclaimed (`claimTime − Job.ScheduleTime`), measured at the claim site. Always-on meter + (sink-gated) `Counter→Statistic` fold for avg + p90/p95/p99 that survive raw-row cleanup.
- **Backlog depth + oldest-job age** — per-queue count of `Enqueued` jobs and age of the oldest, sampled by a periodic server task (off the hot path).

Both slice by executor **application** (§8.23) and honour **`JobMetricsSink`** (`Database`/`Otel`/`Both`, §8.24).

## Scope classification

**Feature** — new external surface (`GET {prefix}/api/queues/metrics`, a Queues dashboard page + addon flag, `WarpConfiguration.BacklogSampleInterval`), a new `IServerTask`, hot-path claim-site instrumentation, ~5 batches. `security_impact = none` (no auth/financial surface; meter tags are low-cardinality — `queue` is bounded, no PII, §1.2).

## Design decisions (resolved from investigation)

- **Eligibility timestamp = `Job.ScheduleTime`** (`Job.cs:20`), not `CreateTime`. `ScheduleTime` advances on every requeue/backoff (`ScheduledJobActivation`, retry, rate-limit, mutex), so queue-wait measures *actual wait in the ready state*, not total lifetime. `waitMs = max(0, claimTime − ScheduleTime)`.
- **Claim-site instrumentation mirrors the `jobstat` finalization triad exactly** (`WarpWorkerService.FinalizeJobState:541-553`): (1) always-on meter `WarpTelemetry.RecordQueueWait(...)` — never gated; (2) DB `Counter` rows gated by `_configuration.JobMetricsSink is Database or Both`; (3) keys built by a construction-only `QueueWaitKeys.Build(...)` — no reads, hot-path-safe (§0.2/§6.1). Sites: `WarpWorkerService.cs:96` (single-worker, at the Processing JobLog) and `WarpDispatcherWorker.cs:461` (`MarkWorkerOwnership`).
- **`QueueWaitKeys` mirrors `JobStatsKeys`** (same `Buckets`, `dur` sum, `pct:{bucket}` histogram; disjoint top-level prefix `qwait:` / per-app `qwait-app:{app}:`, §8.6/§8.19 first-segment-equality). Reader reuses the existing `ExecutionAccumulator` + `Percentiles()` bucket-walk in `JobQueryService` (`:676-706`). Per-app slice omits the histogram (count+dur only), like `jobstat-app`.
- **Backlog = a new server task**, not an `ObservableGauge` (unused in the codebase; avoid a new pattern). `BacklogSampler<TContext> : IServerTask` — `LockKey="warp:backlog-sample"` (one server samples), `DefaultInterval => _configuration.BacklogSampleInterval` (new, default 15s). One EF-LINQ grouped query (`CurrentState==Enqueued && ScheduleTime<=now` GROUP BY `Queue` → `Count`, `Min(ScheduleTime)`), then per queue: emit always-on meters (`warp.job.queue.depth`, `warp.job.queue.oldest_age_seconds`) AND (gated `Database`/`Both`) upsert a point-in-time `Statistic` row per queue for the dashboard. Off hot path (§2.3). No raw SQL (§5.1).
- **No new index** — the existing `{Kind, CurrentState, Queue, ScheduleTime}` composite (`ServiceConfiguration.cs:460`) already serves `COUNT`+`MIN(ScheduleTime)` per queue (leading equality on Kind/CurrentState, Queue group key, ScheduleTime trailing-sorted). Confirm the EF LINQ generates a GROUP BY that uses it during impl.
- **Dashboard = a new "Queues" page** (`/warp/queues`), consistent with the adapters/endpoints/applications one-page-per-surface convention: a per-queue table (depth, oldest-age, wait avg + p90/p95/p99). Addon flag `WarpAddonsInfo.Queues`, always-shown (queue metrics are core, like Applications). *(Alternative: a section on the existing Dashboard page — lighter, but breaks the per-surface-page consistency. Flagged for the gate.)*
- **`JobMetricsSink` honoured throughout:** `Otel` skips both the queue-wait `Counter` writes (hot path) and the backlog `Statistic` writes (server task) — the meters carry the data; the dashboard pages then have no DB rows (Grafana), consistent with §8.24. `Database`/`Both` write both.

## Meters (always-on, null-listener, low-cardinality tags only — `group`/PII off, §1.2)

| Instrument | Kind | Tags |
|---|---|---|
| `warp.job.queue.wait` | Histogram (ms) | `queue`, `application` (when set) |
| `warp.job.queue.depth` | Counter or gauge-value (see impl note) | `queue`, `application` |
| `warp.job.queue.oldest_age_seconds` | gauge-value | `queue`, `application` |

Backlog "gauge" values are emitted by the sampler as a plain metric `Record` each tick (avoiding `ObservableGauge`); new tag constant `QueueMeterQueue = "queue"` in `WarpTelemetryAttributes`.

## Change manifest

**Core:**
- `Warp.Core/Services/QueueWaitKeys.cs` (new) — mirror `JobStatsKeys` (`qwait:` / `qwait-app:`).
- `Warp.Core/Logging/WarpTelemetry.cs` + `WarpTelemetryAttributes.cs` — `warp.job.queue.*` instruments, `RecordQueueWait(...)` helper, `QueueMeterQueue`.
- `Warp.Core/Configuration.cs` — `BacklogSampleInterval` (`TimeSpan?`, default 15s).
- `Warp.Core/Services/JobQueryService.cs` (+ `IJobQueryService`) — `GetQueueMetrics(application)` returning per-queue wait percentiles + latest backlog depth/oldest-age (reuse `ExecutionAccumulator`/`Percentiles`; read `qwait:`/`qwait-app:` + the backlog `Statistic` keys).
- Models: `QueueMetricsModel` (`Warp.Core.Models`).
**Worker:**
- `Warp.Worker/WarpWorkerService.cs` + `WarpDispatcherWorker.cs` — claim-site queue-wait write (triad).
- `Warp.Worker/Services/BacklogSampler.cs` (new `IServerTask`) + registration in `Warp.Worker/ServiceConfiguration.cs:93` neighbourhood.
**UI:**
- `Warp.UI/Endpoints/WarpEndpoints.cs` — `MapGet("queues/metrics")`; `WarpAddonsInfo.Queues`.
- `src/ui/src/pages/queues/QueuesPage.tsx` + `types/` + `api/index.ts` + nav in `MainLayout.tsx` + `App.tsx` route.
**Docs:** `website/docs/features/queue-metrics.md`, rules §8.26, `CLAUDE.md`, `releases.md` 3.7.0.

## Test manifest (both providers, `[GenerateDatabaseTests]` + NoDb)

- **NoDb:** `QueueWaitKeys` build/parse round-trip (count/dur/pct buckets; app vs app-agnostic prefixes; §8.16-style); percentile walk on synthetic buckets.
- **DB:** queue-wait `Counter` written at claim with `waitMs = claim − ScheduleTime` (seed a job with a past `ScheduleTime`, run one claim, assert `qwait:` counters); `JobMetricsSink=Otel` skips the queue-wait Counters while the meter still fires (meter-listener harness); metrics survive raw-`Job`-row cleanup (fold to `Statistic`, delete jobs, assert query still returns).
- **DB (backlog):** `BacklogSampler` computes correct per-queue depth + oldest-age (seed N Enqueued across 2 queues with known oldest `ScheduleTime`); `Scheduled`/future-`ScheduleTime` rows excluded; `JobMetricsSink=Otel` skips the Statistic write.
- **Hot path unchanged (§0.2/§6.1):** claim path gains only the meter/Counter write — assert no new query/round-trip added (review + a claim-path test that the fetch behaviour is unchanged).
- **Query service:** `GetQueueMetrics` returns per-queue wait percentiles + backlog, app-agnostic and per-app.

## Assumptions & risks

- **[VERIFIED]** claim site + `jobstat` triad + `QueueWaitKeys` template + no-new-index all confirmed by investigation (file:line in Design).
- **[ASSUMED]** the EF-LINQ backlog GROUP BY uses the existing composite index efficiently on both providers — verify the generated SQL during impl; add a dedicated filtered index only if profiling shows contention with the hot claim path.
- Dispatcher path: the group fetch sets `Processing`; `MarkWorkerOwnership` is the per-worker receipt point where `now` + `job` are in scope — measure wait there (sub-second skew vs the group claim is acceptable for an SLI).
- Risk: queue-wait `Counter` volume — one count+dur+one bucket per claimed job, same shape/volume as `jobstat` (already accepted); `FailuresOnly`-style suppression is N/A (queue-wait applies to every claim). `CounterAggregator` folds it.

## Out of scope (this slice)

- Exception fingerprinting, trace waterfall, SLO/error-budget (features 2–4).
- Per-queue historical charts beyond the hourly `Statistic` the fold already yields.
- Alerting on backlog thresholds (that's the SLO feature #4, which will consume these metrics + the notifier seam).
