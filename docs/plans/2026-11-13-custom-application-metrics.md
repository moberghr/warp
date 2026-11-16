# Plan: Custom application metrics (`System.Diagnostics.Metrics` collector, `metric_point` store, metric SLOs)

- **Spec:** `docs/specs/2026-11-13-custom-application-metrics.md` (sidecar `.json`). It supersedes the 2026-11-12 spec and plan.
- **Branch:** `feat/custom-application-metrics`. One PR, shipping as 7.5.0. **No push** (§0.1).
- **Rigor:** MAX (score 38: 11 batches, 63 files that contain logic, `pii-exposure`, external contracts capped at +4), so the subagent path is used and every Stage 2 lane runs.
- **Verification commands** are quoted from `CLAUDE.md` § Tech Stack:
  - Build: `dotnet build src/Warp.slnx`
  - Full test: `dotnet test --project src/tests/Warp.Tests/Warp.Tests.csproj`
  - Trait filters: `-- --filter-trait "Category=NoDb|PostgreSql|SqlServer"`
  - Format: `dotnet format --verbosity quiet`
  - UI: `npm` scripts in `src/ui/package.json` (`build`, `test` = `vitest run`, `lint` = `eslint .`)
  - `--filter-class` (xUnit v3 MTP) is **[UNVERIFIED]**. Probe it at B1. If it's rejected, each SC runs its class's category (`NoDb`, or both `PostgreSql` and `SqlServer`) and the run output must show each listed class executed a non-zero number of tests.
  - The benchmark command, `dotnet run -c Release --project src/benchmarks/Warp.Benchmarks -- --filter …`, is **[UNVERIFIED]**. Check `src/benchmarks/Warp.Benchmarks/Program.cs` for BenchmarkSwitcher at B11.

## Wave schedule and execution

`W0: B1 · W1: B2 · W2: B3 · W3: B4 · W4: B5 · W5: B6 B7 · W6: B8 · W7: B9 B10 · W8: B11`

> **Execution:** waves only show independence. Batches run **serially** in this single worktree, so no two builds run at once. Every new NoDb test class carries `[Trait("Category", "NoDb")]`, and every gate must report a **non-zero** executed-test count. A gate that runs 0 tests is a fail.

**Serialize-if-touched chains:**
- `src/core/Warp.Worker/ServiceConfiguration.cs`: B1 → B3 → B4
- `src/core/Warp.Core/ServiceConfiguration.cs`: B3 → B5 → B7
- `src/core/Warp.Dashboard/Endpoints/WarpEndpoints.cs`: B5 → B8

---

## B1: Topology. The observability tasks run on any server

- **Files:** `src/core/Warp.Worker/ServiceConfiguration.cs`
- **Tests:** create `Core/ServiceRegistrationTopologyTests.cs`. Update `Admin/DeploymentShapeTests.cs`: `:275` asserts that `CounterAggregator` is absent on a service-only server; flip it to present, and keep the job-only tasks absent.
- **Pattern files:** the `AddServerHostCore` block at `ServiceConfiguration.cs:340-358`, where `StatisticRollup` is registered.
- **Acceptance:**
  - A `DisableWorker()` server resolves `CounterAggregator`, `ErrorGroupAggregator` and `SloEvaluator` as `IServerTask`. `StatisticRollup` is already registered there and is asserted only as a regression check.
  - The stale `DeploymentShapeTests` comments (`:231`, `:264-269`) are updated.
  - `BacklogSampler` and the scheduling and routing tasks stay worker-only.
  - A worker server's task set is unchanged as a set.
- **Verification:**
  1. `dotnet build src/Warp.slnx`
  2. `dotnet test --project src/tests/Warp.Tests/Warp.Tests.csproj` (the full suite)
  3. Probe `--filter-class` once here.
- **Boundary:**
  - Only the registration site of three cluster-locked tasks moves.
  - The worker hot path and the task bodies are untouched.
- **Depends:** none. It is registration-only and the first batch.
- **Governing constraints:** §0.2, §2.3, §2.13, §6.2.1

## B2: Collector

- **Files:**
  - `src/core/Warp.Core/Metrics/WarpMetricsOptions.cs`
  - `MetricsServiceConfiguration.cs`
  - `IMetricsMarker.cs`
  - `WarpMeterCollector.cs`
  - `MetricCollectorHost.cs`
  - `MetricAggregator.cs`
  - `MetricSeries.cs`
  - `MetricBuckets.cs`
  - `MetricCardinalityGuard.cs`
  - `src/core/Warp.Core/Data/Enums/MetricKind.cs`
  - `src/core/Warp.Core/Logging/WarpTelemetry.cs`
- **Tests:** create these NoDb classes, each with the trait and a **unique meter name per test** (lesson `tasks/lessons.md:159`):
  - `Metrics/MetricSeriesTests.cs`
  - `Metrics/MetricBucketsTests.cs`
  - `Metrics/MetricCardinalityTests.cs`
  - `Metrics/WarpMeterCollectorTests.cs`
  - `Metrics/MetricAggregatorTests.cs`
- **Pattern files:**
  - `src/core/Warp.Core/ClientObservability/ClientEventCardinality.cs` for the guard;
  - `src/core/Warp.Core/Services/WarpCounterBuffer.cs:53-83` for the lossless `TryRemove` drain;
  - `src/core/Warp.Core/ClientObservability/ClientObservabilityServiceConfiguration.cs` for an `IWarpBuilder` extension that registers singletons, a marker and a hosted service.
- **Acceptance:**
  - Spec R1–R9.
  - Each instrument kind aggregates per the spec §B table.
  - Observable counters record deltas, with no events on first sight.
  - Bounds precedence is config > advice > unit default.
  - Without `AddMetrics`, nothing is registered.
- **Verification:**
  1. `dotnet build src/Warp.slnx`
  2. `dotnet test --project src/tests/Warp.Tests/Warp.Tests.csproj -- --filter-trait "Category=NoDb"`
- **Boundary:**
  - The measurement callback does only in-memory work and never throws into host code.
  - The `Warp` meter is never enabled.
  - State is per process.
  - No DB type is referenced from the collector or aggregator.
- **Depends:** B1. It is ordered after B1 so B1's full-suite gate sees a stable baseline; the two share no files.
- **Governing constraints:** §1.2, §8.11, §8.19 (never out-throw), §8.27 (guard shape)

## B3: Storage and flush

- **Files:**
  - `src/core/Warp.Core/Data/Entities/MetricPoint.cs`
  - `MetricInstrument.cs`
  - `src/core/Warp.Core/Services/MetricTiers.cs` (promote the **existing** internal `MetricTier` to public; no second enum, which would hit CS0104)
  - `src/core/Warp.Core/ServiceConfiguration.cs`
  - `src/core/Warp.Core/Configuration.cs`
  - `src/core/Warp.Core/Metrics/IMetricPointSink.cs`
  - `ContextMetricPointSink.cs`
  - `MetricFlusher.cs`
  - `src/core/Warp.Worker/Services/ServerMetricPointSink.cs`
  - `src/core/Warp.Worker/ServiceConfiguration.cs`
  - `src/tests/Warp.Tests/Fixtures/WarpTestServer.cs` (null `MetricRollupInterval` beside the other periodic loops at `:300-317`)
  - `src/benchmarks/Warp.PerfTest/IdleLoopSwitches.cs`
- **Interval contract:** `MetricFlushInterval` is a non-null `TimeSpan` > 0, and the flusher exists only under `AddMetrics`. `MetricRollupInterval` is a `TimeSpan?`, where null disables the loop.
- **Tests:** create `Metrics/MetricFlusherTestsBase.cs` (`[GenerateDatabaseTests(FixtureKind.Default)]`).
- **Pattern files:**
  - `AddSloDefinitionEntity` (`src/core/Warp.Core/ServiceConfiguration.cs:1173-1198`) for EF config, `SetSchema` and enum conversion;
  - `CounterBufferFlusher` (`src/core/Warp.Worker/Services/CounterBufferFlusher.cs`) for the loop, final flush and re-add;
  - `DroppedRecordReporter` for the idle-free per-process reporter.
- **Acceptance:**
  - R10–R12.
  - The registry upsert runs as a separate `SaveChangesAsync`, and a PK race is tolerated.
  - Both entities are in the model and mirrored by `WarpServerContext`.
  - The server sink replaces the context sink.
- **Verification:**
  1. `dotnet build src/Warp.slnx`
  2. `dotnet test --project src/tests/Warp.Tests/Warp.Tests.csproj -- --filter-trait "Category=PostgreSql"`
  3. the same with `"Category=SqlServer"`
- **Boundary:**
  - One flusher per process.
  - An idle flush does no DB work.
  - A registry failure never fails or re-adds points.
- **Depends:** B2 (aggregator and kinds); B1 (`Warp.Worker/ServiceConfiguration.cs` chain).
- **Governing constraints:** §2.11, §2.14, §5.3, §5.4, §5.7, §8.13, §8.32

## B4: Rollup

- **Files:**
  - `src/core/Warp.Worker/Services/MetricRollup.cs`
  - `src/core/Warp.Worker/Services/ExpirationCleanup.cs`
  - `src/core/Warp.Worker/ServiceConfiguration.cs`
- **Tests:** create `Metrics/MetricRollupTestsBase.cs`. It covers:
  - compaction exactness;
  - each tier pass;
  - capped ticks deferring without loss, mirroring `StatisticRollupTestsBase`'s assertion style;
  - bounds mismatch kept apart;
  - an idle tick returning null;
  - the stale-registry sweep and its null-retention case.
- **Pattern files:** `src/core/Warp.Worker/Services/StatisticRollup.cs` and `src/tests/Warp.Tests/Metrics/StatisticRollupTestsBase.cs`.
- **Acceptance:**
  - R13 for `MetricRollup`: extend `Core/ServiceRegistrationTopologyTests.cs` so it is registered on both worker and `DisableWorker()` servers.
  - R15–R18.
  - A null `MetricRollupInterval` disables the loop.
- **Verification:**
  1. `dotnet build src/Warp.slnx`
  2. `dotnet test --project src/tests/Warp.Tests/Warp.Tests.csproj -- --filter-trait "Category=PostgreSql"`
  3. the same with `"Category=SqlServer"`
- **Boundary:**
  - Bounded per tick, under the task-host transaction lock, on `IWarpServerContext` only.
  - Sums before delete; a crash mid-tick neither loses nor double-counts.
- **Depends:** B3.
- **Governing constraints:** §2.3, §2.14, §2.16, §6.2.1, §8.30 contract, lesson "FindEntityType guard"

## B5: Query service and API

- **Files:**
  - `src/core/Warp.Core/Services/IMetricQueryService.cs`
  - `MetricQueryService.cs`
  - `src/core/Warp.Core/Models/MetricModels.cs`
  - `src/core/Warp.Core/ServiceConfiguration.cs`
  - `src/core/Warp.Dashboard/Endpoints/WarpEndpoints.cs`
  - `WarpAddonsInfo.cs`
  - `src/core/Warp.Dashboard/Hosting/WarpDashboardOptions.cs`
  - `WarpDashboardPage.cs` (enum member plus its exhaustive `WarpDashboardPageIds.For` arm)
  - `WarpDashboardUiOverrides.cs` (`bool? Metrics`)
- **Tests:** create `Metrics/MetricQueryServiceTestsBase.cs`.
- **Plus:** the **SC9 measurement** on PG, recorded in the PR body. If it is over 250 ms, the conditional escalation fires and work stops.
- **Pattern files:**
  - `src/core/Warp.Core/Services/EndpointQueryService.cs` for tier-aware reads and the percentile walk;
  - `GET slo/{id:int}` in `WarpEndpoints.cs:717-722`;
  - `ShowSlo()` in `WarpDashboardOptions.cs:76`.
- **Acceptance:**
  - R19–R21.
  - Partial fine rows are summed.
  - Responses are labelled with their resolution.
- **Verification:** the PG and SQL Server trait runs, as in B3.
- **Boundary:**
  - `AsNoTracking` reads grouped in SQL.
  - The list never scans points.
  - Nothing is written.
- **Depends:** B4 (compacted rows exist to read); B3 (`Warp.Core/ServiceConfiguration.cs` chain).
- **Governing constraints:** §1.5, §5.1, §5.2, §5.3, §6.4

## B6: Metrics UI

- **Files:**
  - `src/ui/src/pages/metrics/MetricsPage.tsx`
  - `MetricDetailPage.tsx`
  - `metricModel.ts`
  - `src/ui/src/types/metrics.ts`
  - `src/ui/src/api/index.ts`
  - `src/ui/src/App.tsx`
  - `src/ui/src/layouts/MainLayout.tsx`
  - `src/ui/src/demo/data.ts`
  - `src/ui/src/demo/data/metrics.ts` (create)
  - `src/ui/src/demo/adapter.ts` (`/api/metrics*` routes, `metrics` flag, `version: '7.5.0'`)
  - `src/ui/src/types/index.ts` (`Addons.metrics`)
  - `src/ui/src/layouts/navModel.ts`
- **Tests:** create `src/ui/src/pages/metrics/metricModel.test.ts`; update `layouts/navModel.test.ts` and `demo/demoFixtures.test.ts`.
- **Pattern files:** `src/ui/src/components/PerformanceChart.tsx`, `src/ui/src/pages/issues/`, and `counterModel` (pure model plus vitest).
- **Acceptance:**
  - Breakdown, filter chips and dashed 1d/7d overlays.
  - Percentiles for histograms; avg, min and max for gauges.
  - A ratio view and a collapse badge.
  - The demo build renders the page.
- **Verification:** in `src/ui/`, run `npm run build && npm test && npm run lint`, then a browser check with `npm run dev`.
- **Boundary:** the maths lives only in the pure `metricModel.ts`.
- **Depends:** B5.
- **Governing constraints:** none. This is UI only and no backend rule constrains it.

## B7: SLO model, maths and validation

- **Files:**
  - `src/core/Warp.Core/Data/Enums/SloEnums.cs`
  - `src/core/Warp.Core/Data/Entities/SloDefinition.cs`
  - `src/core/Warp.Core/ServiceConfiguration.cs`
  - `src/core/Warp.Core/Slo/SloValidation.cs`
  - `MetricFilter.cs`
  - `SloServiceConfiguration.cs`
  - `src/core/Warp.Core/Services/SloMath.cs`
- **Tests:** update `Slo/SloMathTests.cs` and `Slo/SloValidationTests.cs`; create `Slo/MetricFilterTests.cs`.
- **Acceptance:**
  - The spec §E table.
  - R29.
  - A minimum window of 600s.
  - Gauges rejected.
  - Existing kinds unchanged.
- **Verification:** the NoDb trait run.
- **Interim state** (same branch, never released): between B7 and B8 a metric objective is accepted, but the evaluator's existing unknown-kind arm reports NoData and logs Error. B8 closes this.
- **Boundary:**
  - `SloKind` 1–5 behaviour is byte-for-byte unchanged.
  - Enum values are append-only.
- **Depends:** B5 (`Warp.Core/ServiceConfiguration.cs` chain).
- **Governing constraints:** §8.11, §8.31, §2.11

## B8: SLO evaluation, read and API

- **Files:**
  - `src/core/Warp.Worker/Services/SloEvaluator.cs`
  - `src/core/Warp.Core/Services/SloCommandService.cs`
  - `SloQueryService.cs`
  - `src/core/Warp.Core/Models/SloModels.cs`
  - `src/core/Warp.Core/Notifiers/WarpOperationalEvent.cs`
  - `src/core/Warp.Dashboard/Endpoints/WarpEndpoints.cs`
- **Tests:** create `Slo/MetricSloEvaluatorTestsBase.cs` with a recording `IWarpNotifier`. Update `Slo/SloCommandQueryTestsBase.cs` so the fields round-trip through the `GetObjective`/`List` read-back.
- **Pattern files:** the existing `SloEvaluator` arms (`:143-295`) and the alert buffer (`:346-378`).
- **Acceptance:**
  - R22–R28.
  - Exactly one event per edge; NoData never dispatches.
  - The baseline is hour-resolution and prorated.
- **Verification:** the PG and SQL Server trait runs.
- **Boundary:**
  - Alerts are dispatched only from `OnCommittedAsync`.
  - Reads run on `IWarpServerContext`.
  - The classification machinery is unchanged.
- **Depends:** B7 (model and maths); B5 (the `WarpEndpoints.cs` chain and `metric_point` reads).
- **Governing constraints:** §2.14, §8.25, §8.31, §5.7

## B9: SLO UI

- **Files:**
  - `src/ui/src/pages/slo/SloPage.tsx`
  - `shared.tsx`
  - `src/ui/src/types/slo.ts` (`SloKindLabel` for kinds 6–9)
  - `src/ui/src/pages/slo/SloDetailPage.tsx` (`isThreshold` and the labels for metric kinds)
  - `src/ui/src/demo/data/slo.ts`
- **Acceptance:**
  - Each kind shows its own fields.
  - The detail page formats metric kinds correctly.
  - The instrument picker is fed by `/api/metrics`.
  - A 400 validation message is surfaced.
- **Verification:** in `src/ui/`, run `npm run build && npm test && npm run lint`.
- **Boundary:** the existing kinds' form is unchanged.
- **Depends:** B8 (API fields); B6 (the metrics API client).
- **Governing constraints:** none. UI only.

## B10: Coexistence and end-to-end

- **Files:** `src/tests/Warp.Tests/Warp.Tests.csproj`, adding `MediatR` **12.5.0**, pinned.
- **⛔ `checkpoint:human-verify` before the install:**
  1. Confirm `MediatR` 12.5.0 on nuget.org.
  2. Confirm the Apache-2.0 licence.
  
  The package is already known to the repo: `src/benchmarks/Warp.Benchmarks/Warp.Benchmarks.csproj:19` uses `12.*`.
- **Tests:**
  - create `Core/MediatRCoexistenceTests.cs` (NoDb);
  - create `Core/HostDbContextCoexistenceTestsBase.cs` (PG and SQL Server);
  - create `Metrics/CustomMetricsEndToEndTestsBase.cs` (Integration, `SerializeInCollection = "HeavyIntegration"`).
  
  The end-to-end test runs an `AddWarp`-only TestHost with a controller that uses `IMeterFactory`, plus `CollectAspNetCoreHttp()`. Data flushes to `metric_point`; a `DisableWorker()` server compacts and rolls it up and fires a metric SLO edge. A seeded `Enqueued` job stays `Enqueued`. It also asserts the built-in `http.server.request.duration` tags, which verifies the spec's [ASSUMED] tag names on the real stack. It uses the **default registration**.
- **Acceptance:** R14, R30, R31; SC1, SC5, SC6.
- **Verification:** `dotnet test --project src/tests/Warp.Tests/Warp.Tests.csproj` (the full suite).
- **Boundary:** test-only. No production change.
- **Depends:** B8, for the full pipeline including the SLO edge.
- **Governing constraints:** §4.2, §4.4, §4.7.1, user rule "pin exact NuGet versions"

## B11: Benchmark, docs and rules

- **Files:**
  - `src/benchmarks/Warp.Benchmarks/Benchmarks/MetricCollectorBenchmarks.cs`
  - `website/docs/features/custom-metrics.md` (create)
  - `slo-error-budget.md`
  - `adapters.md`
  - `background-services.md`
  - `website/docs/dashboard/health/applications.md`
  - `website/docs/operations/ef-core-integration.md`
  - `configuration.md`
  - `website/docs/releases.md`
  - `ROADMAP.md`
  - `.claude/rules/project-specific.md`
  - `architecture.md`
  - `CLAUDE.md`
- **Acceptance:**
  - SC8 and SC10.
  - The 7.5.0 notes call out the `DisableWorker()` behaviour change and the additive migration.
  - Every service-only task-set statement (§2.13, §8.18, `background-services.md`, `applications.md`) is updated.
  - §8.29 is corrected; §8.34 is added.
  - The docs carry the http 5xx and ratio SLO recipes.
- **Verification:**
  1. The benchmark command [UNVERIFIED].
  2. `grep -l PCI website/docs/features/custom-metrics.md website/docs/features/adapters.md`
- **Boundary:** docs describe shipped behaviour exactly, including stated limits (PII undetectable, hourly baselines, crash loss, built-in tag names).
- **Depends:** B1–B10.
- **Governing constraints:** §1.2, §0.1

---

## Post-implementation

- [ ] Full build has 0 warnings; full suite green against the Phase 2.9 baseline (SC7).
- [ ] Spec-drift check.
- [ ] Stage 1 compliance review; Stage 2 lanes: test, context-miner, architecture, silent-failure.
- [ ] `/simplify`
- [ ] Lessons captured.
- [ ] Ready for review. **No push.**
