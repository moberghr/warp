# Plan — Pluggable metric source (`IMetricSource`), Phase 1

*2026-08-05* · spec: `docs/specs/2026-08-05-pluggable-metric-source.md`

This plan covers **Phase 1 only** — the `IMetricSource` seam + the local backend, a behavior-preserving refactor that establishes the abstraction. Prometheus (Phase 2) and the Aspire demo (Phase 3) are separate slices with their own specs; they are the reason the contract must stay backend-neutral, but no Phase-2/3 code is in these batches.

## Guiding constraints (from the codebase rules)

- **Behavior-preserving.** Every routed read must reproduce today's numbers exactly (tier down-bin, merged Statistic+Counter, dropped-record windows). The both-provider parity tests are the gate.
- **Off the hot path.** Only dashboard/SLO-evaluator reads are touched (§0.2/§6.1 untouched). Confirm in batch 1.
- **Registered in `AddWarp`**, `TryAddScoped`, so a dashboard-only process resolves it (§2.14).
- **Inject specific deps** — `LocalMetricSource` takes the context, not `IServiceProvider`; no static fallbacks.

## Batches

### Batch 1 — Contract + fake + pinned semantics
- **Files:** `src/core/Warp.Core/Metrics/IMetricSource.cs` (new), `src/tests/Warp.Tests/Metrics/FakeMetricSource.cs` (new), `src/tests/Warp.Tests/Metrics/MetricSourceContractTests.cs` (new, NoDb).
- **Do:** define `IMetricSource` + `MetricRef`/`SeriesQuery`/`SeriesBucket`/`MetricResolution`/`SeriesAgg`. Write `FakeMetricSource` (dictionary-backed). Pin the contract's semantics in NoDb tests (breakdown grouping, resolution buckets, empty→zero). Sketch the PromQL translation for each method **in a comment** to validate the shape is backend-neutral before locking it.
- **Acceptance:** contract compiles; NoDb tests green; a one-paragraph note in the spec/plan confirming each method maps cleanly to both a SQL read and a PromQL query.
- **Boundary:** no production read site changed yet.

### Batch 2 — `LocalMetricSource<TContext>` + parity
- **Files:** `src/core/Warp.Core/Metrics/LocalMetricSource.cs` (new); `src/tests/Warp.Tests/Metrics/LocalMetricSourceTestsBase.cs` (new, `[GenerateDatabaseTests]`).
- **Do:** move the merged-read + `MetricTiers.TryClassifyKey` + down-bin logic out of `DashboardStatsService` into `LocalMetricSource`, implementing all four methods. Parity tests: seed a fold, assert each method equals the numbers the current readers produce.
- **Acceptance:** parity tests green on **both** providers; `LocalMetricSource` has no dependency on dashboard-specific types.
- **Boundary:** `DashboardStatsService` may temporarily delegate to the new class but its public output is unchanged.

### Batch 3 — Route `DashboardStatsService` through the seam
- **Files:** `src/core/Warp.Core/Services/DashboardStatsService.cs`; `src/core/Warp.Core/ServiceConfiguration.cs`.
- **Do:** replace the inlined stats/counters-history, combined-total, and dropped-record-window reads with `IMetricSource` calls. Register `IMetricSource → LocalMetricSource<TContext>` (`TryAddScoped`) in `AddWarp`.
- **Acceptance:** the existing dashboard/metrics/observability test suites pass **unchanged** on both providers; no snapshot/number differences.
- **Boundary:** rendering, endpoints, and models unchanged.

### Batch 4 — Route `SloEvaluator` through the seam
- **Files:** `src/core/Warp.Worker/Services/SloEvaluator.cs`.
- **Do:** replace the merged aggregate read with `IMetricSource` (keeping the fast-burn short-window + tier windowing logic on top). The evaluator asks the source for windowed series/percentiles rather than scanning `Statistic`/`Counter` directly.
- **Acceptance:** the full `Warp.Tests.Slo` suite passes unchanged on both providers.
- **Boundary:** SLO math, notifier, entities untouched.

### Batch 5 *(optional — may defer to a follow-on)* — Route the remaining query services
- **Files:** queue-metrics / adapter / endpoint / client query services' history + percentile reads.
- **Do:** route through the seam for full coverage. If it inflates the slice, defer to a follow-on and note it.
- **Acceptance:** each service's suite green both providers.

## Verification

- `dotnet build src/Warp.slnx` analyzer-clean.
- `dotnet test` — the new `Warp.Tests.Metrics` namespace + the touched suites (`Slo`, `Metrics`, `Applications`, `Adapters`, `Endpoints`, `ClientObservability`) on **both** providers, unchanged where behavior-preserving.
- Behavioral diff: "no observable change; metric reads now flow through `IMetricSource`, local backend identical to before."

## Follow-on (not this plan)

- **Phase 2:** `PrometheusMetricSource` + Refit `IPrometheusQueryApi` + PromQL gen + OTel-name alignment + `MetricSourceBackend` selector + mocked-Refit unit tests + Prometheus Testcontainer.
- **Phase 3:** OTel→Prometheus in the demo AppHost; demo flipped to the Prometheus source; end-to-end render check.
