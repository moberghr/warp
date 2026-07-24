# Plan — Multi-Application Observability (shared-database Warp)

**Spec:** `docs/specs/2026-07-22-multi-app-observability.md`
**Branch:** `feat/multi-app-observability` (off `main`)
**Rigor:** MAX. **Order matters** — schema first, behavior next, dashboard/docs last. Each batch builds analyzer-clean and adds its own tests (TDD); a final batch covers cross-cutting integration + back-compat.

Representative file paths are given; a `[confirm]` tag means verify the exact site at that batch (see spec §9 assumptions). Every touched file must appear here.

---

## Batch 1 — Schema foundation (entities, columns, model, server-context mirror)

**Files:**
- `src/core/Warp.Core/Data/Entities/ApplicationInstance.cs` (new)
- `src/core/Warp.Core/Data/Entities/ApplicationInstanceLog.cs` (new)
- `src/core/Warp.Core/Enums/ApplicationInstanceEventType.cs` (new, from 1) `[confirm enums location]`
- `src/core/Warp.Core/Data/Entities/Server.cs` (+`Application`, `Version`, `Environment`)
- `src/core/Warp.Core/Entities/Job.cs` (+`Application`) — note `Warp.Core.Entities` namespace (§8.13)
- `src/core/Warp.Core/Data/Entities/AdapterCallLog.cs`, `EndpointCallLog.cs`, `WebhookDelivery.cs` (+`Application`)
- `WarpModelCustomizer` — entity configs for 2 new entities + indexes; new columns need no config if convention-mapped `[confirm path]`
- `IWarpServerModelNames` + `WarpServerModelNames<TContext>` — table/column names for the 2 new tables `[confirm path]`

**Acceptance:** solution builds analyzer-clean; both entities are in `TContext` model and mirrored by `WarpServerContext`; new columns present and nullable. No runtime behavior yet.
**Boundary:** schema only — no writers, no readers, no config.
**Tests:** NoDb — model has the 2 tables + 7 columns; enum values start at 1. (Persistence asserted in later batches.)

## Batch 2 — Config + shared CPU/RAM sampler

**Files:**
- `src/core/Warp.Core/Configuration.cs` — `ApplicationName`, `ApplicationVersion`, `ApplicationEnvironment`, `ApplicationHeartbeatInterval`, `ApplicationInstanceStaleGrace`, `ApplicationInstanceLogRetention`, `ApplicationInstanceLogRetentionCount`
- Move `src/core/Warp.Worker/Services/ProcessCpuTracker.cs` → `src/core/Warp.Core/...` (namespace update; update the server `Heartbeat` reference) `[confirm no Warp.Worker-only deps]`

**Acceptance:** builds; server `Heartbeat` still fills `Server.CpuUsagePercent`/`MemoryWorkingSetBytes` via the moved sampler (no behavior change); config defaults preserve today's behavior (`ApplicationName == null`).
**Boundary:** config + sampler move only.
**Tests:** NoDb — `ProcessCpuTracker` samples a non-negative working set; config defaults null/sane.

## Batch 3 — Registry + heartbeat host + cleanup

**Files:**
- `src/core/Warp.Core/.../ApplicationHeartbeatHost.cs` (new `IHostedService`) — registers/heartbeats/deregisters a non-server `ApplicationInstance`; uses `IServiceScopeFactory` (§0.5), `TimeProvider` (§5.7); no provider/lock
- `ServiceConfiguration` (`AddWarp`) — register the host **only when `ApplicationName` set**; register `IApplicationQueryService` unconditionally `[confirm path]`
- Server `Heartbeat` server task — stamp `Server.Application/Version/Environment`; write lifecycle events (`Registered`/`HeartbeatLost`/`Recovered`) `[confirm path]`
- `src/core/Warp.Worker/Services/ExpirationCleanup.cs` — stale `ApplicationInstance` sweep (past `ApplicationInstanceStaleGrace`, `StaleSwept` log) + `ApplicationInstanceLog` retention (age + count §8.22)

**Acceptance:** an `AddWarp`-only process with `ApplicationName` set registers an instance, heartbeats CPU/RAM, deregisters on graceful stop; server processes stamp their `Server` row; stale instances swept; lifecycle events recorded. `ApplicationName == null` ⇒ nothing runs.
**Boundary:** registry lifecycle only — no provenance, no metrics, no dashboard.
**Tests (DB, `HeavyIntegration`):** register/heartbeat/deregister; stale-sweep; server stamped; log retention. NoDb: host no-ops when `ApplicationName` null.

## Batch 4 — Provenance stamping

**Files:**
- `Publisher` / `BatchPublisher` — stamp `Job.Application` at publish; **preserve on requeue** (`RequeueJob` path) `[confirm paths]`
- Adapter recorder/flusher — stamp `AdapterCallLog.Application` `[confirm]`
- Endpoint middleware/recorder/flusher — stamp `EndpointCallLog.Application` `[confirm]`
- Webhook dispatcher `SendAsync` — stamp `WebhookDelivery.Application` `[confirm]`

**Acceptance:** rows carry the producing app's name; `null` when unset; requeue preserves `Job.Application`.
**Boundary:** write-side stamping only — no reads/metrics/UI.
**Tests (DB):** stamped on job (incl. requeue-preserved), adapter, endpoint, webhook; null when unset.

## Batch 5 — Per-app adapter + endpoint metrics (disjoint keys)

**Files:**
- `AdapterCounterKeys` + `AdapterCallFlusher` — disjoint app-keyed family; existing keys unchanged `[confirm]`
- `EndpointCounterKeys` + `EndpointCallFlusher` — disjoint app family + `Application` in identity `[confirm]`
- `AdapterQueryService`, `EndpointQueryService` — per-app reads; old-key back-compat parse

**Acceptance:** per-app adapter/endpoint metrics accrue under new keys; existing keys byte-for-byte unchanged; same route in two apps stays distinct; old-format keys still parse (app = unassigned).
**Boundary:** metrics keys + reads; no dashboard wiring beyond the query service.
**Tests (DB + NoDb):** disjoint-key formatting/parse incl. old keys; endpoint identity split; per-app aggregates survive log deletion.

## Batch 6 — Per-job-type + per-handler execution metrics

**Files:**
- Job-stats counter producer (finalization/completion path) — add `type`/`handler` segments, `dur` duration-sum, optional latency buckets, executor-`app` slice `[confirm exact site — spec §9]`
- Job-stats reader / new `JobStatsQueryService` (or extend `JobQueryService`) — per-type/handler count/avg+p95/p99/error from `Statistic`

**Acceptance:** finalization writes the extended keys (Counter-only, hot path untouched §0.2/§6.1); metrics fold → `Statistic`, keep hourly history, prune at `HourlyStatisticsRetention`, lifetime totals persist; readable by type and by handler, sliceable by executor app.
**Boundary:** job execution metrics only.
**Tests (DB):** fold + history + prune + lifetime-persist; by-type and by-handler; executor-app slice. NoDb: key formatting.

## Batch 7 — Application query service + dashboard API

**Files:**
- `IApplicationQueryService` + `ApplicationQueryService<TContext>` (new) — list (instances ∪ servers → `InstanceView`, group rollups), detail, per-app rolled-up activity `[confirm registration in AddWarp]`
- `src/core/Warp.UI/Endpoints/WarpEndpoints.cs` — `GET /api/applications`, `/api/applications/{id}`, `/api/applications/{id}/instances/{instanceId}`; `application` filter param on jobs/adapters/endpoints lists; per-type/handler job-stats endpoint
- `WarpAddonsInfo` — `Applications` flag (gated on `ApplicationName` set) `[confirm]`

**Acceptance:** endpoints return unified instances + rollups; app filter works across surfaces; resolves in an `AddWarp`-only (dashboard/publisher) process; addon flag correct.
**Boundary:** API + query layer; no frontend.
**Tests (DB):** query list/detail; unified `InstanceView`; app filter; resolves without a server.

## Batch 8 — Tracing

**Files:**
- Telemetry attributes + activity creation for job / adapter / endpoint — add `warp.application` span/resource attribute `[confirm sites]`

**Acceptance:** activities carry `warp.application` when set; absent when null.
**Boundary:** OTel attribute only.
**Tests (NoDb, mirror `OTelMetricsTests`):** attribute present/absent.

## Batch 9 — Frontend (Applications page + details + Jobs-by-Type metrics)

**Files (`src/ui/src/...`):**
- `pages/servers/*` → `pages/applications/*` — `ApplicationsPage` (grouped instances + rollups), `ApplicationDetailPage`, light instance detail
- `ServerDetailPage` — relabel the `ServerLog` section to "Server tasks"
- `JobsByTypePage` — metrics header (throughput, avg + p95/p99, error rate) + by-type/by-handler toggle + app filter
- `types/applications.ts`, `api/index.ts` (+endpoints, +app filter params), `MainLayout.tsx` (nav rename, gated on `addons.applications`), route `/servers` → `/applications` + redirect

**Acceptance:** one Applications page (server ∪ non-server) with drill-in; server → existing Server detail; non-server → light detail; Jobs-by-Type shows historical metrics; nav gated; degrades to server list when no app set; responsive.
**Boundary:** frontend only.
**Tests:** UI unit tests mirroring existing patterns; Playwright at 390px for responsiveness.

## Batch 10 — Docs + rules

**Files:**
- `website/docs/features/applications.md` (new)
- `website/docs/getting-started.md` — additive-migration note
- `website/docs/features/adapters.md`, `endpoint-observability.md`, `webhooks.md` — app-dimension note
- `.claude/rules/project-specific.md` — new § (multi-app observability) `[confirm §number]`

**Acceptance:** docs describe opt-in, migration, per-app metrics, Applications page; rules § added.
**Boundary:** docs only.

## Batch 11 — Cross-cutting integration + backward-compat

**Files:** `src/tests/Warp.Tests/Applications/*` (new, `[GenerateDatabaseTests]`, `HeavyIntegration` §4.7.1) + a full-stack extension.

**Acceptance:** end-to-end — two "apps" (loopback) on one schema: instances visible, provenance + per-app metrics + per-job-type/handler metrics + lifecycle log all correct and filterable; migration additive; old-shape null-application rows read as "(unassigned)"; old-format counter keys parse.
**Boundary:** tests only.

---

## Gate sequence

11 batches → Phase 3.5 drift check → Stage 1 `compliance-reviewer` → Stage 2 [`test-reviewer` + `architecture-reviewer` + `silent-failure-hunter`] (MAX) → Phase 6 cleanup → Phase 7 compound.

## PR

Land on `feat/multi-app-observability`. **Do not push, open a PR, merge, or tag without explicit per-action approval.**
