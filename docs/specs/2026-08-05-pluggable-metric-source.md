# Spec — Pluggable metric source (`IMetricSource`)

*2026-08-05*

## Goal

Let Warp read its **own** metrics from a pluggable storage backend instead of only the local `Statistic`/`Counter` fold — so a deployment can export metrics via OTel to an external TSDB (Prometheus first) and have Warp's dashboard + SLO evaluator **read them back**, keeping the operational DB lean while every view stays in the Warp UI.

The data is **never arbitrary** — it is always Warp's own, known-shape metrics (job stats, queue-wait, backlog, deadline, SLO inputs, and later custom metrics). So Warp keeps auto-rendering its purpose-built views and **generates the queries itself**; there is no graph editor and no user-facing query language. This is explicitly **not** Grafana-style federation of arbitrary series.

## Scope classification

**Feature — internal contract + refactor.** Adds one new internal seam (`IMetricSource`) and routes existing metric reads through it. Not a breaking change (no external/wire contract changes in Phase 1; the OTel export shape already exists). `security_impact`: none for Phase 1 (read-side over the local DB). Phase 2 introduces outbound credentials to an external store (config-only, treated then).

## Design — the `IMetricSource` contract (the crux)

Every metric read Warp performs reduces to a small, backend-neutral set of queries. Getting this boundary right — neither SQL-shaped nor Prometheus-shaped — is 90% of the feature; the second backend (Phase 2) is what proves it.

The queries Warp actually issues today (from `DashboardStatsService`, the queue-metrics/adapter/endpoint/client readers, and `SloEvaluator`):

| Need | Example today | Contract method |
|---|---|---|
| Lifetime / window total for a key | `stats:succeeded` combined Statistic+Counter | `GetTotalAsync` |
| Time-bucketed series, down-binned to a resolution | `GetStatsHistory` / `GetCountersHistory` (tier-aware) | `GetSeriesAsync` |
| Breakdown of a metric by one tag | authorizations by `outcome` | `GetSeriesAsync` with `BreakdownBy` |
| Percentile from a latency histogram | adapter/endpoint/job/qwait p95 | `GetPercentileAsync` |
| Current gauge | `qbacklog:default:depth`, oldest-age | `GetGaugeAsync` |

Proposed shape (illustrative — final naming lands in the plan):

```csharp
public interface IMetricSource
{
    Task<long> GetTotalAsync(MetricRef metric, DateRange? window, CancellationToken ct);
    Task<IReadOnlyList<SeriesBucket>> GetSeriesAsync(SeriesQuery query, CancellationToken ct);
    Task<double> GetPercentileAsync(MetricRef metric, int percentile, DateRange window, CancellationToken ct);
    Task<double?> GetGaugeAsync(MetricRef metric, CancellationToken ct);
}
// MetricRef      = { string Name; IReadOnlyDictionary<string,string>? Tags }
// SeriesQuery    = { MetricRef Metric; DateRange Window; MetricResolution Resolution; SeriesAgg Agg; string? BreakdownBy }
// SeriesBucket   = { DateTime BucketStart; string? TagValue; long Value }   // TagValue set when BreakdownBy given
// MetricResolution = Fine | Hourly | Daily   (mirrors §8.30 tiers)
// SeriesAgg      = Sum | Last                 (Sum for counters, Last for gauges)
```

**Naming lives inside each backend — no shared mapping table.** `MetricRef.Name` is an **abstract logical name**, never a storage key. Each `IMetricSource` implementation privately translates it to its own store's naming: `LocalMetricSource` → colon keys (wrapping the existing `*Keys` builders), a later `PrometheusMetricSource` → OTel metric name + label matchers. So a new backend is a genuine drop-in with its own translator — no persisted-format change, no OTel-name change, no lookup table anyone has to keep in sync. (In Phase 1, where the local names and the logical names largely coincide, `Name` may be the colon base directly; the per-family logical→colon mapping is filled in as SLO/jobstat reads are routed.)

- The **local backend** (`LocalMetricSource`) translates each method to the current merged `Statistic`+`Counter` reads and `MetricTiers` classification — i.e. it *is* the existing read logic, moved behind the interface, with the down-bin/tier semantics preserved bit-for-bit.
- The **Prometheus backend** (Phase 2) translates each method to PromQL over Warp's OTel-exported metric names (`GetSeriesAsync` → `sum by (<breakdownBy>)(increase(<name>[<step>]))`, `GetPercentileAsync` → `histogram_quantile(...)`, `GetGaugeAsync` → an instant query). It reads through a Refit `IPrometheusQueryApi`.

## Change manifest — Phase 1 (local seam)

| Path | Change | Mechanical? |
|---|---|---|
| `src/core/Warp.Core/Metrics/IMetricSource.cs` (new) | The interface + `MetricRef`/`SeriesQuery`/`SeriesBucket`/enums | no (new contract) |
| `src/core/Warp.Core/Metrics/LocalMetricSource.cs` (new) | Implements `IMetricSource` over `TContext`; hosts the extracted merged-read + tier-classify logic | no |
| `src/core/Warp.Core/Services/DashboardStatsService.cs` | Route `GetStatsHistory`/`GetCountersHistory`/`GetCombinedStatValue`/dropped-record reads through `IMetricSource` | no |
| `src/core/Warp.Worker/Services/SloEvaluator.cs` | Route the merged aggregate read through `IMetricSource` (keep the fast-burn/tier logic on top) | no |
| Queue-metrics / adapter / endpoint / client query services | Route their history/percentile reads through `IMetricSource` (may be phased across batches) | no |
| `src/core/Warp.Core/ServiceConfiguration.cs` | Register `IMetricSource` → `LocalMetricSource<TContext>` (`TryAddScoped`, resolves in any `AddWarp` process) | no |
| Tests (see manifest) | in-memory fake + local-backend parity tests | no |

**Explicitly out of scope for Phase 1:** the Prometheus backend, OTel-name alignment, the Aspire demo wiring, and any config to *select* a backend (Phase 1 registers only the local one). Custom-metric emit/ingest is a separate feature and not required here — the seam wraps existing metric reads.

## Test manifest

- **NoDb** — `FakeMetricSource` (in-memory) proving `DashboardStatsService` + `SloEvaluator` render/evaluate correctly against a non-local source (proves backend-agnosticism).
- **`[GenerateDatabaseTests]` (both providers)** — `LocalMetricSource` **parity**: for a seeded fold, `GetSeriesAsync`/`GetTotalAsync`/`GetPercentileAsync`/`GetGaugeAsync` return the *same* numbers the pre-refactor readers produced (down-bin, tier-classification, merged Statistic+Counter, dropped-record windows). This is the regression net for the refactor.
- Existing dashboard/SLO/queue-metrics suites must stay green unchanged (the refactor is behavior-preserving).

## Implementation batches

**Phase 1 — local seam (this slice):**
1. `IMetricSource` contract + `MetricRef`/`SeriesQuery`/`SeriesBucket`/enums; `FakeMetricSource` + NoDb tests that pin the contract's expected semantics.
2. `LocalMetricSource<TContext>` — extract the merged-read + `MetricTiers` classify + down-bin logic from `DashboardStatsService` into it; parity tests (both providers).
3. Route `DashboardStatsService` (stats/counters history, combined totals, dropped-record windows) through `IMetricSource`; register in `AddWarp`; suite green.
4. Route `SloEvaluator` through `IMetricSource` (preserve fast-burn/tier windowing on top); SLO suite green both providers.
5. (Optional, same slice or follow-on) route queue-metrics / adapter / endpoint / client history+percentile reads through the seam.

**Follow-on slices (separate specs, noted for context — NOT in this plan's batches):**
- **Phase 2 — Prometheus backend:** `PrometheusMetricSource` + Refit `IPrometheusQueryApi`; PromQL generation; OTel-name/label alignment; a `MetricSourceBackend` config selector (`Local` default | `Prometheus`); mocked-Refit unit tests + a Prometheus Testcontainer integration test.
- **Phase 3 — Aspire demo end-to-end:** wire OTel export → Prometheus in the demo AppHost; flip the demo to `Prometheus` source; confirm the dashboard renders from Prometheus.

## Assumptions & risks

- **[RISK] Contract leakage.** The seam could accidentally encode local-DB assumptions (colon-key parsing, merged Statistic+Counter). Mitigation: `FakeMetricSource` + designing the two backends' translation on paper in batch 1 before finalizing the interface.
- **[RISK] Parity drift.** The refactor must reproduce the tier down-bin/merged-read numbers exactly. Mitigation: the both-provider parity tests are the acceptance gate for batches 2–4.
- **[ASSUMED] No hot-path impact.** All routed reads are dashboard/SLO-evaluator (off the worker fetch/execute path, §0.2/§6.1). To verify: confirm no in-scope read sits on the claim/finalize path.
- **[ASSUMED] Custom metrics independent.** The seam wraps *existing* metric reads; it does not require the custom-metrics feature. To verify against current `DashboardStatsService` read sites.
- **[DEFERRED] External-store availability.** When Phase 2 lands, dashboard reads depend on Prometheus uptime/latency; a degraded-source UX is a Phase-2 concern, not Phase 1.
