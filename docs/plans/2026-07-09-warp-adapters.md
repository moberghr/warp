# Warp.Adapters v1 — Implementation Plan

Spec: `docs/specs/2026-07-09-warp-adapters.md` (+ `.json` sidecar — authoritative manifest/batches). Scope: new-feature. Security impact: pii-exposure (opt-in capture). Rigor: **MAX** (8 batches, ~61-file manifest, security_impact ≠ none, 16 public contracts) → subagent-per-batch, full Stage 2 review, no auto-proceed.

Out of scope (hard): webhooks spec (2026-07-10, depends on this), shared circuit breaker, replay, SOAPAction fallback, GraphQL generator, worker hot path (§0.2).

## Commands

- Build: `dotnet build src/Warp.slnx` (analyzer-clean mandatory, TreatWarningsAsErrors)
- Tests (NoDb): `dotnet test --project src/tests/Warp.Tests/Warp.Tests.csproj -- --filter-trait "Category=NoDb"`
- Tests (DB): `... -- --filter-trait "Category=PostgreSql"` / `"Category=SqlServer"`; scope with `--filter-namespace Warp.Tests.Adapters` (never `--nologo`/`-v` after `--`)
- Frontend: `cd src/ui && npm run build`
- Format: `dotnet format --verbosity quiet`

## Batches

### B1 — Core scope API + entities + telemetry
**Files:** `src/core/Warp.Core/Adapters/{IWarpAdapters, AdapterCallScope, WarpAdapterOptions, AdapterServiceConfiguration, IAdapterCallRecorder(internal), AdapterRateLimitedException}.cs`, `src/core/Warp.Core/Data/Entities/{AdapterDefinition, AdapterCallLog}.cs`, `src/core/Warp.Core/Data/Enums/{CallRecording, CaptureMode, AdapterCallOutcome, AdapterRateLimitOverflow}.cs`, `ServiceConfiguration.cs` (entity model methods), `WarpModelCustomizer.cs`, `Logging/WarpTelemetry.cs` (+Attributes), tests `AdapterScopeTests.cs`, `AdapterTelemetryTests.cs`, `AdapterGroupTests.cs` (NoDb).
**Key rules:** enums from 1 (§8.11); entities in `Warp.Core.Data.Entities` (§8.13), added unconditionally (§2.11), no `.ToTable()` (§5.4); telemetry unconditional via null-listener pattern; AsyncLocal-sentinel harness for telemetry tests (tasks/lessons.md 2026-05-07 — construct scoping in test ctor, not async init); group/operation cardinality guards in scope layer.
**Acceptance:** SC10, SC12 (guard logic), SC15/SC16 scope-level; build analyzer-clean; NoDb green.

### B2 — Recorder + flusher + counters + retention
**Files:** `Adapters/{DbAdapterCallRecorder, AdapterCallFlusher}.cs`, `Configuration.cs` (`AdapterCallLogRetention`, `AdapterDefinitionOrphanGrace`), `Warp.Worker/Services/ExpirationCleanup.cs`, tests `AdapterRecorderTestsBase.cs`, `AdapterCounterTestsBase.cs`, `AdapterCleanupTestsBase.cs` (`[GenerateDatabaseTests]`).
**Key rules:** bounded channel, drop + `records_dropped` counter, never block callers; `IServiceScopeFactory` only (§0.5); Counter rows never Statistic (§6.2); `TimeProvider` (§5.7); one SaveChanges per flush batch; lazy `LastSeenAt` (stale >5min); confirm `[ASSUMED]` counter-key format vs `CounterAggregator` grouping here.
**Acceptance:** SC1 (All + FailuresOnly), SC3 persisted shape, SC4, SC6, SC15 per-group counters incl. successes; PG + SQL Server green.

### B3 — Warp.Adapters.Http package
**Files:** `src/core/Warp.Adapters.Http/*` (csproj, `HttpAdapterServiceConfiguration`, `WarpAdapterHandler`, `WarpAdapterCall` ambient, `WarpAdapterHttpOptions`, `OperationNameResolver`, `HttpRequestMessageExtensions`), `src/Warp.slnx`, tests `OperationNameResolverTests.cs`, `CaptureRedactionTests.cs` (NoDb, stub `HttpMessageHandler` — no live network).
**Key rules:** handler ordering fixed (Warp outermost); BaseUrl optional (SC14); naming precedence option > ambient > heuristic (SC2); `WithWarpGroup`/`WithWarpOperation`/`SetCorrelation` request options; redaction defaults user-owned (add/remove/clear); resolve `[ASSUMED]` Polly attempt count here (fallback: Attempts=1, document).
**Acceptance:** SC2, SC3 handler-level, SC12, SC14; build analyzer-clean.

### B4 — Shared rate limiter
**Files:** `Adapters/AdapterRateLimiter.cs`, `Warp.Adapters.Http/WarpAdapterRateLimitHandler.cs`, `Data/Queries/IWarpSqlQueries.cs` + both provider impls (only if no existing bucket-lock fits — `[ASSUMED]`, verify first), `AdapterDefinition` shared-policy fields wiring, tests `AdapterRateLimitTestsBase.cs`.
**Key rules:** token leasing (chunk = max(1, limit/10)), keys `warp:adapter:{name}` (§8.6 disjoint namespaces); row locks via `IWarpSqlQueries` (§1.4), no raw SQL in core (§5.1); rate-limit handler innermost (per physical attempt); policy conflict: enforce persisted, warn, `config_conflicts` counter, `HasPolicyConflict`; own scope — do NOT reuse `RateLimitStore` handler-scope commit semantics; BarrierSignal N=2, no spray tests (§4.7), bare `[TimedFact]` only.
**Acceptance:** SC5, SC13; PG + SQL Server green.

### B5 — Warp.Adapters.Refit package
**Files:** `src/core/Warp.Adapters.Refit/*` (csproj, `RefitAdapterServiceConfiguration`, `RefitOperationNameReader`), `src/Warp.slnx`, tests `RefitAdapterTests.cs` (NoDb, stub handler).
**Key rules:** only this package references Refit; `RestMethodInfo` from `HttpRequestMessage.Options`; `RefitSettings` passthrough.
**Acceptance:** SC8; build analyzer-clean.

### B6 — Dashboard backend
**Files:** `Warp.Core/Services/{IAdapterQueryService, AdapterQueryService}.cs`, `Warp.UI/Endpoints/{WarpEndpoints, WarpAddonsInfo}.cs`, tests `AdapterEndpointTestsBase.cs`.
**Key rules:** query service on `TContext` (§2.14 stays-on-TContext), registered so dashboard-only processes resolve it; `AsNoTracking` + `.Select()` projections (§5.3/§6.4), no `_context.Set<>` in projections (§5.2); addons flag same pattern as sagas; groups table from per-group Statistics.
**Acceptance:** SC7; PG + SQL Server green.

### B7 — Frontend
**Files:** `src/ui/src/pages/adapters/{AdaptersPage, AdapterDetailPage}.tsx`, `MainLayout.tsx` (nav, gated), `App.tsx` (routes), `api/index.ts`, `types/adapters.ts`, `demo/data/adapters.ts` (note: existing `demo/adapter.ts` is the axios adapter — unrelated, do not touch).
**Key rules:** follow existing page patterns (DataTable, MetricCard, StateBadge, TanStack Query hooks); design per artifact mockups (health pills dot+label, neutral sparklines, groups table w/ GroupLabel, drawer with redacted panes); demo mode deterministic.
**Acceptance:** SC11; nav hidden when `addons.adapters=false`; demo renders list + detail.

### B8 — Docs + rules
**Files:** `website/docs/features/adapters.md` (new), `.claude/rules/architecture.md` (+§2.15), `.claude/rules/project-specific.md` (+§8.19), `CLAUDE.md` (ships-as + addon mentions), spec cross-link check.
**Content musts:** inbound vs outbound naming, capture/PII stance (§1.2 responsibility), shared limiter + conflict semantics, AddWarp-only adoption (no worker), observe-first rollout, granularity rule + operation-vs-group litmus test verbatim.
**Acceptance:** full suite green (both DBs), SC9 final.

## Verification map

SC1–SC16 → test files per spec test manifest; final gate = full suite + `npm run build` + behavioral diff. Every batch: `dotnet format` + analyzer-clean build before checkoff. No push without approval (§0.1).

## Deviations

- MTK workflow-artifact scripts absent in repo → progress tracked in `tasks/todo.md` checkboxes only.
- `silent-failure-hunter` agent type not installed → MAX-level silent-failure lens runs as a prompted general-purpose review agent.
