# Warp Roadmap

Forward-looking feature ideas only. Shipped features live in the [release notes](website/docs/releases.md) and feature docs, not here.

## Observability intelligence

The candidate arc for the next releases. Warp already owns every health signal it needs — per-job execution, queue-wait, backlog, error groups (Issues), adapter/circuit-breaker health, SLOs, and the unified trace view. The next step is turning that into intelligence a generic APM structurally can't provide, because it doesn't own all the signals and doesn't understand jobs.

### Custom application metrics — front-runner

Let handlers and endpoints emit their **own** metrics — counters, sums, histograms — through the same `Counter → Statistic` fold Warp already runs, so they get dashboards, retention tiers, and SLOs for free.

- **Ad-hoc emit** (StatsD-style, no upfront declaration): `ctx.Metrics.Increment("card.authorizations", new { outcome, network })`, `.Add("card.authorized_amount", value, …)`, `.Record("card.auth_latency_ms", ms, …)`. The metric type is implied by the method, not a config block.
- **Self-protecting cardinality**: a runaway tag collapses to `{other}` (the same guard client-event names use, §8.27) — necessary because the fold writes to the user's own operational DB, unlike a purpose-built TSDB.
- **Auto-rendered dashboard**: a Metrics page (list → detail) with a "break down by tag" selector and a filter — Warp draws it, the user writes no UI.
- **Feeds the rest**: a metric's success/fail convention lets an **SLO** target it; a metric spike shows up in the correlated incident view.
- **Optional declaration** only for strict fail-fast validation, units, or the SLO success-fail convention.
- **Honest boundary**: an auto-rendered explorer, not Grafana (no PromQL / custom dashboards) — power users still use the OTel sink.
- **Driving example**: card-authorization metrics — authorizations/hour by outcome (approved/declined/error), total authorized amount, and auth-latency percentiles.

### Adaptive / anomaly SLOs

Learn each job type's normal range (p95 / failure-rate / queue-wait) from the tiered history and flag deviations — no hand-set threshold. Ship **advisory-only** first (a "looks anomalous" annotation, not a pager) to avoid the false-positive alert-fatigue trap; paging on learned baselines can come once it has earned trust.

### Correlated incident view

Automatically tie signals that overlap in time and share an entity — an adapter / circuit-breaker failure → error-group spike → backlog surge → SLO burn — into one timeline. Ship as **automatic cross-linking + a shared incident timeline**, not a confident root-cause engine (a wrong causal claim *during* an incident is worse than none).

### Capacity forecasting (stretch)

Little's Law on arrival vs. service rate → "at the current rate this queue grows unbounded, add N workers" / "backlog clears in ~X min." Naturally feeds autoscaling — e.g. a KEDA scaler on the backlog metrics Warp already emits.

## Concurrency & Flow Control

### Unique Jobs

Don't enqueue if an identical job is already pending (Enqueued/Processing). Dedup by type + serialized payload hash, with an optional dedup window.

### Job Priority

Explicit priority levels within a queue; higher-priority jobs fetched first (not just queue ordering). Touches the sacred worker fetch path (§0.2/§6.1) — needs a matching `(Queue, CurrentState, Priority, ScheduleTime)` index so it stays a single indexed read.

## Performance & Compilation

### Native AOT Support

Make Warp compatible with Native AOT compilation. Replace reflection-based handler discovery (JobDispatcher) with source generators. Eliminates startup cost and enables trimming.

### Source Generators

Generate handler registration, type mappings, and serialization code at compile time. Replaces runtime reflection in JobDispatcher (DiscoverJobHandler, DiscoverMessageHandlers, ExecuteHandler). Enables AOT and improves startup performance.

## Infrastructure

### Runtime Schema Migration Helper

Optional `MigrateWarpSchemaAsync()` for users who don't use EF migrations. Diffs the EF model against the database at runtime, generates and executes only Warp table DDL. Respects naming conventions. Lower priority — EF migrations cover most users.

## Considered and deferred

**In-box platform primitives** — feature flags, distributed locks / leader election, event bus / pub-sub, idempotency keys, cache: "concerns every app solves with an external system, brought in-box on the database you already have." Stress-tested and set aside for now — they're a different product from a job engine, and correctness-critical primitives (locks, event consumers) are risky to co-locate on the operational DB, unlike observability which is lossy-tolerant. The strongest survivor was a **transactional in-app event bus** (essentially the outbox Warp already has, with no dual-write) — revisit if there's real demand. A DB-backed cache was rejected outright (it defeats the purpose of a cache).
