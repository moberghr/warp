# Spec — Multi-Application Observability (shared-database Warp)

**Date:** 2026-07-22
**Branch:** `feat/multi-app-observability` (off `main`)
**Scope classification:** feature — multi-file, new public contracts, new entities. **Additive / non-breaking** (no drops, renames, retypes, or new NOT-NULL-without-default).
**Rigor:** MAX (see plan) — ~11 batches, >20 non-mechanical files, new public contracts, but `security_impact = none` (no auth/secret/financial surface; PII stance unchanged).

---

## 1. Problem

Multiple independent applications share one Warp schema (one database). All registered servers pick up all jobs — that is by design and stays unchanged. But today every process's identity is only `Server.ServerName` (= machine name), and there is **no way to tell which application** created a job, made an adapter call, or owns an endpoint. Non-server processes (publisher-only / API-only / dashboard-only, i.e. `AddWarp` without `AddWarpServer`) are **completely invisible** — no row, no heartbeat, no liveness.

## 2. Goal

Add an **opt-in** "application" origin/identity dimension so that in a shared-DB deployment you can:

- see every process (server or not) live/dead in one overview, with CPU/RAM, version, environment;
- attribute each job (creator), adapter call, endpoint request, and webhook to the application that produced it, and **filter** every list by application;
- get **per-application** adapter/endpoint metrics and **per-job-type / per-handler** execution metrics (count, duration, error rate) that **fold to durable stats and survive raw-row cleanup**;
- correlate across apps in traces (`warp.application` OTel attribute).

All without changing job execution/routing, and with **zero impact** when the feature is not enabled.

## 3. Non-goals (explicitly out of scope)

- Changing execution/routing — a shared queue still means any worker runs any job.
- Isolation between apps (that is a *separate schema* per app; documented, not built here).
- Per-**creator** job *metrics* (jobs execute on a worker app; creator-metrics are meaningless — creator is provenance only).
- Full application **log capture** (the app's own `ILogger` output, `BackgroundServiceLog`-style). Deferred to a later PR. We add only lightweight instance **lifecycle** events.
- `Database.Migrate()`-on-boot (Warp deliberately never touches the schema itself).
- A durable app-level `FirstSeenAt` (derivable from the earliest `Statistic` bucket if ever needed).

## 4. Design decisions (agreed — do not re-litigate)

1. **Opt-in trigger:** `WarpConfiguration.ApplicationName` (`string?`, default `null`) + optional `ApplicationVersion`, `ApplicationEnvironment`. `null` ⇒ today's behavior byte-for-byte. `ApplicationName` gates **all** new runtime behavior.
2. **Registry (sibling-table model):** "every server *is* an application instance; not every instance is a server."
   - New `ApplicationInstance` entity for **non-server** processes only.
   - `Server` gains nullable `Application`, `Version`, `Environment` — it remains the instance record for server processes.
   - Each process writes exactly **one** physical row (server → `Server`; non-server → `ApplicationInstance`). No double-writes.
   - New lightweight heartbeat `IHostedService` registered by `AddWarp` **when `ApplicationName` is set** (deliberate change to the "AddWarp is passive" contract, §2.13). No provider, no distributed lock (each instance owns its row by `Id`). Register → heartbeat CPU/RAM → deregister on graceful `StopAsync`. Server processes keep piggybacking the existing `Heartbeat` server task (no new loop for them).
3. **Provenance** (nullable columns; filter/display only): `Job.Application` (stamped at publish, preserved on requeue), `AdapterCallLog.Application`, `EndpointCallLog.Application`, `WebhookDelivery.Application`.
4. **Per-app metrics** on adapters + endpoints, as a **disjoint counter-key namespace** (new prefix; existing keys byte-for-byte unchanged — cross-version-safe, per §8.6/§8.19). `Application` also becomes part of **endpoint identity** so the same route in two apps stays distinct.
5. **Per-job-type + per-handler execution metrics:** extend the existing job-stats counter keys (`stats:{outcome}` + hourly `stats:{outcome}:{yyyy-MM-dd-HH}`) with `type` and `handler` segments, a `dur` duration-sum token, optional latency buckets (p95/p99), and the **executor** application slice. Rides the existing `Counter → CounterAggregator → Statistic` fold; hourly history auto-pruned at `HourlyStatisticsRetention` (7 d); lifetime totals persist. No new aggregation or cleanup path. Worker hot path untouched (Counter-writes only).
6. **Unified lifecycle log:** new `ApplicationInstanceLog` for **all** instances (soft `InstanceId` ref, no hard FK). `ServerLog` stays server-task-specific and is relabeled "Server tasks" in the UI.
7. **Dashboard:** rename Servers page → **Applications** (route + label + redirect); one page grouping instances (server ∪ non-server) via a unified `InstanceView`; app detail; global app filter on Jobs/Adapters/Endpoints; Jobs-by-Type gains historical metrics with by-type and by-handler views.
8. **Tracing:** `warp.application` OTel span/resource attribute.

## 5. Public contracts (new / changed)

| Contract | Surface | Change |
|---|---|---|
| `WarpConfiguration.ApplicationName/Version/Environment` + `ApplicationHeartbeatInterval`, `ApplicationInstanceStaleGrace`, `ApplicationInstanceLogRetention`, `ApplicationInstanceLogRetentionCount` | external | new config props (nullable / defaulted) |
| `ApplicationInstance`, `ApplicationInstanceLog` entities | external | new persisted schema (always-in-schema) |
| `Server.Application/Version/Environment`, `Job.Application`, `AdapterCallLog.Application`, `EndpointCallLog.Application`, `WebhookDelivery.Application` | external | new nullable columns on persisted schema |
| `ApplicationInstanceEventType` enum (from 1) | external | new enum |
| `IApplicationQueryService` + `ApplicationQueryService<TContext>` | external | new query service, registered by `AddWarp` |
| `GET {prefix}/api/applications`, `/api/applications/{id}`, `/api/applications/{id}/instances/{instanceId}`; `application` filter param on jobs/adapters/endpoints; per-type/handler job-stats endpoint | external | new/changed dashboard API |
| `WarpAddonsInfo.Applications` flag | external | new addon flag |
| `warp.application` OTel attribute | external | new telemetry attribute |
| `ProcessCpuTracker` moved `Warp.Worker` → `Warp.Core` | internal-tooling | namespace move (shared sampler) |

## 6. Data model & migration

**New tables** (`Warp.Core.Data.Entities`; always-in-schema via `WarpModelCustomizer` §2.11; mirrored by `WarpServerContext` via `ApplyWarpModel`, names in `IWarpServerModelNames` §2.14):

- `application_instance`: `Id` (Guid PK), `ApplicationName` (idx), `MachineName`, `StartedAt`, `LastHeartbeatAt` (idx), `CpuUsagePercent` (double?), `MemoryWorkingSetBytes` (long?), `Version?`, `Environment?`.
- `application_instance_log`: `Id` (PK), `InstanceId` (idx, soft ref — no FK), `ApplicationName` (idx), `Timestamp` (idx), `EventType` (`ApplicationInstanceEventType`: `Registered=1, HeartbeatLost=2, Recovered=3, Stopped=4, StaleSwept=5`), `Message?`, `ExpireAt?` (idx).

**New nullable columns:** `server` (+`application`, `version`, `environment`), `job` (+`application`), `adapter_call_log` (+`application`), `endpoint_call_log` (+`application`), `webhook_delivery` (+`application`).

**Migration path:** Warp ships no migrations. The user's standard `dotnet ef migrations add / database update` picks up the additive delta (2 tables + 7 nullable columns) because `WarpModelCustomizer` contributes it unconditionally. **100% additive** — no destructive ops, no backfill, no downtime. Legacy rows have `application = null` → surfaced as an "(unassigned)" bucket. Rolling-deploy safe: old-version processes ignore the new columns/tables (EF never `SELECT *`), write `null`, don't register instances. See §9 for the disjoint counter-key rule that keeps the metrics **read** path cross-version-safe.

## 7. Counter-key design (backward-compatible metrics)

- **Adapters/Endpoints per-app:** new **disjoint** key family under its own prefix (e.g. `endpoint:app:{app}:{route}:{token}`), leaving existing `endpoint:...` / adapter keys byte-for-byte unchanged. New code writes existing keys (app-agnostic totals, keeps old readers whole) **plus** the app-scoped keys; old readers never see the new prefix. `Application` joins the endpoint **identity** (counter key) so the same route in two apps stays distinct.
- **Jobs per-type/handler:** extend the existing job-stats family with `type` / `handler` segments, a `dur` duration-sum token, optional latency buckets, and an executor-`app` segment — all under the established hourly-bucket format (`yyyy-MM-dd-HH`) so the generic hourly-stat prune (7 d) and the `Counter → Statistic` fold apply with no new machinery. Lifetime cumulative totals (non-bucketed) persist.

## 8. Test manifest (both providers via `[GenerateDatabaseTests]`; heavy classes join `HeavyIntegration` §4.7.1; `BarrierSignal` not spray-N §4.7; bare `[TimedFact]`)

- **Registry (DB):** register/heartbeat/deregister; stale-sweep past grace; server row stamped with app/version/env; non-server writes `ApplicationInstance`; CPU/RAM populated.
- **Provenance (DB):** `Job.Application` stamped at publish and **preserved on requeue**; adapter/endpoint/webhook stamped.
- **Metrics (DB):** per-app adapter+endpoint counters under disjoint keys; endpoint identity split (same route, two apps → two aggregates); per-type **and** per-handler job-execution metrics fold → `Statistic`, keep hourly history, **prune** at retention while lifetime totals persist.
- **Lifecycle log (DB):** events written for server and non-server instances; retention (age + count) sweeps.
- **Dashboard (DB):** `IApplicationQueryService` list/detail; unified `InstanceView` (server ∪ non-server); `application` filter on jobs/adapters/endpoints; resolves in an `AddWarp`-only process.
- **Migration/back-compat (DB):** additive-only assertion; old-shape `null`-application rows read as "(unassigned)"; old-format counter keys still parse.
- **NoDb:** counter-key formatting/parsing incl. old-key back-compat; `ProcessCpuTracker`; `InstanceView` projection; `EndpointCounterKeys`/`AdapterCounterKeys` disjoint-namespace formatting; enum-from-1.

## 9. Assumptions & risks

- **[ASSUMED]** the job-stats counter producer is in the finalization path (`FinalizationLogs`/completion) and writes `stats:{outcome}` keys today (observed `stats:requeued` + `stats:requeued:{hour}` in a dump). **Confirm exact site at Batch 5** before extending keys.
- **[ASSUMED]** `ProcessCpuTracker` (in `Warp.Worker`) has no `Warp.Worker`-only dependency preventing a move into `Warp.Core`. **Verify at Batch 2.**
- **[ASSUMED]** exact file sites for `WarpModelCustomizer`, `IWarpServerModelNames`, adapter/endpoint flushers, Publisher/BatchPublisher, `WarpEndpoints`, `WarpAddonsInfo`, the frontend pages — representative paths listed in the plan; **confirm at each batch**.
- **Risk — AddWarp contract change:** adding a heartbeat host to passive `AddWarp` processes. Mitigated: gated on `ApplicationName`, no provider/lock, deregisters on shutdown; documented as an intentional contract change.
- **Risk — endpoint identity change:** making `Application` part of endpoint identity changes new counter keys. Mitigated by the disjoint-namespace rule (§7) — existing keys untouched, old readers unaffected.
- **Risk — worker hot path (§0.2/§6.1):** per-type/handler metrics must be Counter-writes only, at the existing finalization site — **no** new fetch/execute logic.

## 10. Security impact: **none**

No auth/secret/financial/infra surface. `ApplicationName`/`Version`/`Environment` are operator-set, non-secret. No new PII (machine name already captured on `Server`/logs; §1.2 stance unchanged). Lifecycle-log messages must not include payloads (§1.2).

## 11. Out-of-scope / follow-ups

- Full app `ILogger` capture (`BackgroundServiceLog`-style).
- Opt-in `Database.Migrate()`-on-boot.
- Per-executor-app job *metrics* beyond the tag (the tag ships; richer executor-app dashboards can follow).
