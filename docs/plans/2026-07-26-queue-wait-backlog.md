# Plan — Queue-wait latency + backlog SLIs

**Date:** 2026-07-26 · **Slug:** `queue-wait-backlog` · **Spec:** `docs/specs/2026-07-26-queue-wait-backlog.md` · **Release:** 3.7.0

Rigor: **HIGH** (5 batches; new external contract — API endpoint, dashboard page, config; hot-path touch). Plan-only until the Phase 2.5 gate.

## Batch 1 — Core metrics primitives (no wiring)
- `QueueWaitKeys.cs` — mirror `JobStatsKeys`: `qwait:` (count + `dur` sum + `pct:{bucket}` histogram) and `qwait-app:{app}:` (count + dur, no histogram). Same `Buckets`/`Pct`/`BucketFor`.
- `WarpTelemetry`: `warp.job.queue.wait` Histogram + `warp.job.queue.depth` / `warp.job.queue.oldest_age_seconds` instruments + `RecordQueueWait(queue, ms, app)` + backlog record helper; `WarpTelemetryAttributes.QueueMeterQueue`.
- `Configuration.cs`: `BacklogSampleInterval` (default 15s).
- NoDb tests: `QueueWaitKeys` build/parse round-trip + percentile walk.
- **Boundary:** no claim-site, no server task, no dashboard. Build analyzer-clean.

## Batch 2 — Claim-site queue-wait (hot path)
- `WarpWorkerService.cs:96` + `WarpDispatcherWorker.cs:461` (`MarkWorkerOwnership`): compute `waitMs = max(0, now − job.ScheduleTime)`; always-on `RecordQueueWait`; gated (`JobMetricsSink is Database or Both`) `QueueWaitKeys.Build` Counter writes (dispatcher accumulates into its `PendingCompletion`/list like `jobstat`).
- DB tests (both providers): counter written at claim; `Otel` skips Counters, meter fires; survives raw-`Job` cleanup.
- **Boundary:** only the meter/Counter write added to the claim path — no new query/round-trip (§0.2/§6.1).

## Batch 3 — Backlog sampler server task
- `BacklogSampler<TContext> : IServerTask` — `LockKey="warp:backlog-sample"`, `DefaultInterval => BacklogSampleInterval`; one EF-LINQ grouped query (`Enqueued && ScheduleTime<=now` GROUP BY Queue → Count, Min(ScheduleTime)); per queue emit always-on backlog meters + (gated) upsert a per-queue backlog `Statistic`. Register alongside `CounterAggregator` (`ServiceConfiguration.cs:93`).
- DB tests: depth + oldest-age correct; Scheduled/future excluded; `Otel` skips Statistic; verify generated GROUP BY SQL uses the existing index.
- **Boundary:** off hot path; no raw SQL; no new index unless profiling demands.

## Batch 4 — Query service + API + dashboard
- `IJobQueryService.GetQueueMetrics(application)` → `QueueMetricsModel` (per-queue wait avg/p90/p95/p99 from `qwait:`/`qwait-app:` via `ExecutionAccumulator`; latest backlog depth/oldest-age from the backlog `Statistic`). Reuse existing merged-stats reader.
- `WarpEndpoints.cs`: `MapGet("queues/metrics")` (+ per-app `applications/{id}/queues` if cheap); `WarpAddonsInfo.Queues`.
- Frontend: `pages/queues/QueuesPage.tsx` (per-queue table, JobsByTypePage card pattern), `types/`, `api/index.ts`, nav in `MainLayout.tsx`, route in `App.tsx`. Always-shown nav (core metric).
- Tests: query-service metrics (app-agnostic + per-app); addons flag; regenerate dashboard screenshots (memory rule).
- **Boundary:** read-only surfaces; no change to write paths.

## Batch 5 — Docs
- `website/docs/features/queue-metrics.md`; rules §8.26; `CLAUDE.md` mention; `releases.md` 3.7.0 entry.

## Sequencing & verification
Batches 1→5. Full suite both providers after batch 3 and again after 4. Analyzer-clean throughout. Two-stage review (Stage 1 compliance; Stage 2 test + architecture — new server task + hot-path touch + new external contract). Behavioral diff before review.

## Deferred to later features (not this PR)
- SLO/error-budget consuming these metrics + notifier breach alerts (feature 4).
- Backlog-threshold `BacklogBreached` operational event (reserved `WarpEventType` value, §8.25) — wired in feature 4.
