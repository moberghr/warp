# Plan — Full metric-source program: logical catalog + Prometheus read-back

*2026-08-05* · supersedes the "Phase 2/3 deferred" framing in `2026-08-05-pluggable-metric-source.md` · spec: `docs/specs/2026-08-05-pluggable-metric-source.md`

## Goal (locked with the user)

**Anything Warp exports to OTel, Warp's own UI can read back and render** — via a `PrometheusMetricSource` behind the existing `IMetricSource` seam. Decisions taken:

1. **One branch.** The whole program lands together on `plan/metric-source` (Phase-1 batches 1–4 already committed there), not as a stacked stream of PRs.
2. **Full parity.** All metric families become readable from Prometheus, including the four that need *new* OTel export: deadline `count` denominator, SLO gauges, error-group occurrences (by fingerprint), and the client-event **name** breakdown — accepting the Prometheus label-cardinality cost on the last two.

## The crux — a logical metric catalog that mirrors the OTel meters

The Phase-1 seam passed **colon storage keys** as `MetricRef.Name`. That is a dead end for Prometheus: a colon key like `jobstat:type:MyJob` carries no family identity, and its `:count`/`:dur`/outcome tokens don't map to a single Prometheus series. The investigation (see spec) showed every remaining reader is a *fan-out* prefix scan that folds *multiple tokens* per key and reads a *lifetime `:pct:`* histogram inline — none of which the colon-key `MetricRef` can express to a second backend.

**Resolution: `MetricRef.Name` becomes a logical metric name from a fixed catalog that mirrors the OTel meter model** (`src/core/Warp.Core/Logging/WarpTelemetry.cs`). The catalog is the single Rosetta Stone; each backend owns its own translation *from* it:

| Logical metric | Kind | Tags | OTel instrument | Local colon translation |
|---|---|---|---|---|
| `job.execution` | counter | type, handler, outcome, application | `warp.job.execution.total` | `jobstat[-app]:type\|handler:{id}:{outcome}` fold |
| `job.execution.duration` | histogram | type, handler, outcome, application | `warp.job.execution.duration` | `jobstat…:dur` (sum) + `…:pct/pcth` (percentile) |
| `job.queue.wait` | histogram | queue, application | `warp.job.queue.wait` | `qwait[-app]:{queue}:count\|dur\|pct\|pcth` |
| `job.queue.depth` | gauge | queue, application | `warp.job.queue.depth` | `qbacklog:{queue}:depth` |
| `job.queue.oldest_age` | gauge | queue, application | `warp.job.queue.oldest_age_seconds` | `qbacklog:{queue}:oldest_age_seconds` |
| `job.deadline` | counter | type, queue, application | **NEW** `warp.job.deadline.total` | `deadline[-app]:{type}:count` |
| `job.deadline.miss` | counter | type, queue, application | `warp.job.deadline.miss` | `deadline[-app]:{type}:miss` |
| `adapter.calls` | counter | adapter, operation, group, outcome, application | `warp.adapter.calls` | `adapter[-app]…:{outcome}` fold |
| `adapter.duration` | histogram | adapter, operation, group, outcome, application | `warp.adapter.duration` | `adapter…:dur` + `:pct/pcth` |
| `endpoint.calls` | counter | route, group, outcome, application | `warp.endpoint.calls` | `endpoint[-app]…:{outcome}` |
| `endpoint.duration` | histogram | route, group, outcome, application | `warp.endpoint.duration` | `endpoint…:dur` + `:pct/pcth` |
| `client.events` | counter | type, application | `warp.client.events` | `clientevent[-app]:total:{type}` |
| `client.events.named` | counter | type, name, application | **NEW** `warp.client.events.named` | `clientevent:name:{type}:{name}` |
| `client.vitals` | histogram | vital, application | `warp.client.vitals` | `clientevent:vital:{vital}:*` |
| `errorgroup.occurrences` | counter | fingerprint, application | **NEW** `warp.errorgroup.occurrences` | `errorgroup[-app]:{fp}:{hour}` |
| `slo.attainment` / `slo.budget` | gauge | name, kind, dimension, application | **NEW** `warp.slo.attainment` / `.budget` | `SloEvaluation` rows (gauge-read) |

`count` and `duration` are **separate logical metrics**; `outcome`/`group` are **tags**, not key tokens. Avg latency = `sum(duration) / count`. Percentile has **one** windowed concept (no separate lifetime method) — Local maps a bounded window to the tiered `:pcth:` ladder and an unbounded window to the lifetime `:pct:` ladder; Prometheus maps both to `histogram_quantile` over the range.

## Extended `IMetricSource` contract

Phase-1 methods stay; add the primitives the table-builders need. All are expressible as one SQL grouping and one PromQL query:

```csharp
// existing: GetTotalAsync, GetSeriesAsync, GetPercentileAsync, GetGaugeAsync

// NEW — grouped total over a window (null = lifetime), split by one or more tags.
// Local: merged read grouped by the parsed tag tokens. Prometheus: sum by (groupBy)(increase(name{tags}[range])).
Task<IReadOnlyList<BreakdownRow>> GetBreakdownAsync(
    MetricRef metric, IReadOnlyList<string> groupBy, MetricWindow? window, CancellationToken ct);

// NEW — grouped percentile of a histogram metric, split by one or more tags.
// Local: per-group pct walk. Prometheus: histogram_quantile(p, sum by (groupBy, le)(rate(name_bucket{tags}[range]))).
Task<IReadOnlyList<PercentileRow>> GetPercentileBreakdownAsync(
    MetricRef metric, int percentile, IReadOnlyList<string> groupBy, MetricWindow? window, CancellationToken ct);

// NEW — enumerate the distinct values a tag takes (the entity list: adapters, routes, queues, job types…).
// Local: distinct parsed token from the key scan. Prometheus: a label_values / series query.
Task<IReadOnlyList<string>> GetTagValuesAsync(
    MetricRef metric, string tag, MetricWindow? window, CancellationToken ct);

// records
public sealed record BreakdownRow(IReadOnlyDictionary<string,string> Tags, long Value);
public sealed record PercentileRow(IReadOnlyDictionary<string,string> Tags, double Value);
```

`GetBreakdownAsync` with an empty `groupBy` == `GetTotalAsync`. The per-hour history the detail pages draw is `GetSeriesAsync` with `BreakdownBy` — which `LocalMetricSource` must now actually honor (today it returns `TagValue = null`).

The raw-log reads (recent calls, last-failure timestamp, session joins, related-jobs) are **not metrics** — they stay direct entity reads and are explicitly out of the seam.

## Work batches (ordered; all on `plan/metric-source`)

**A. Logical catalog + seam extension (contract).** Add `WarpMetricCatalog` (the logical names + tag-key constants, one place, referenced by both backends and the readers). Extend `IMetricSource` + `FakeMetricSource` + the NoDb contract tests with the three new methods and the honored `BreakdownBy`. Re-express the Phase-1 `MetricRef`s (DashboardStatsService, SloEvaluator) from colon keys to logical names.

**B. `LocalMetricSource` translation layer.** Give it the logical→colon translator per family (wrapping the existing `*Keys` builders / `*.TryParse*`), implement the three new methods + real `BreakdownBy`, and the bounded-vs-lifetime percentile split. Parity tests (both providers) extended to cover breakdown/tag-values/grouped-percentile against the pre-refactor reader numbers.

**C. Route the readers (the 11 sites).** `AdapterQueryService`, `JobQueryService` (queue-metrics + job-execution), `EndpointQueryService`, `ClientEventQueryService`, `ErrorGroupQueryService` — replace each private `LoadStats/LoadHistory/LoadMerged` with seam calls. Each service's suite stays green on both providers (behavior-preserving for the local backend).

**D. OTel export gaps + histogram Views.** Add `warp.job.deadline.total`, `warp.client.events.named`, `warp.errorgroup.occurrences`, `warp.slo.attainment`/`.budget`. Add histogram bucket **Views** matching the DB `:pcth:` ladder (to 300 s) so `histogram_quantile` can serve the 30 s/60 s latency SLO targets. Normalize adapter outcome casing (`Failed`→`failed`) so the tag matches across backends. Keep all emission always-on / null-listener (§0.2 hot-path safe).

**E. `PrometheusMetricSource` + Refit.** `IPrometheusQueryApi` (Refit) for instant + range queries; per-logical-metric PromQL generation driven by `WarpMetricCatalog`; `histogram_quantile` for percentiles; `MetricRef`→`metric_name{label=…}` translation. Mocked-Refit unit tests for every family's query shape. A `MetricSourceBackend` config selector (`Local` default | `Prometheus`) choosing the registration in `AddWarp`.

**F. Demo + integration.** Prometheus (OTLP-receiver enabled) resource in `Warp.Demo.AppHost`; wire OTel export → Prometheus; flip the demo to the `Prometheus` backend; verify every dashboard page renders from Prometheus. A Prometheus **Testcontainer** integration test (seed via OTLP or the API, read through `PrometheusMetricSource`).

**G. Docs.** Observability/metric-source doc page, rules §, releases note; contrast local vs Prometheus backends, the export-gap families, the cardinality note for error-group/client-name.

## Verification

- `dotnet build src/Warp.slnx` analyzer-clean at every batch.
- Both providers green for each touched suite after C (`Metrics`, `Slo`, `Adapters`, `Endpoints`, `ClientObservability`, `Applications`, `Observability`, job/queue-metrics).
- Prometheus unit (mocked Refit) + the Testcontainer integration green after E/F.
- End-to-end: demo on the Prometheus backend renders adapters/endpoints/queues/clients/SLO/error-groups with the same shape as the local backend.

## Risks

- **[RISK] Contract leakage.** Mitigated by designing every new method's PromQL and SQL side-by-side in batch A before locking, and by `FakeMetricSource` proving backend-agnosticism.
- **[RISK] Parity drift** across 11 sites. The both-provider suites are the gate for batch C; no site merges until its suite is green unchanged.
- **[RISK] Cardinality** on `errorgroup.occurrences{fingerprint}` and `client.events.named{name}`. Accepted by the user (full parity); documented in G with a note that a deployment can disable those exports.
- **[RISK] Histogram bucket mismatch.** Without Views, Prometheus percentiles saturate at ~10 s. Batch D Views are a hard prerequisite for latency SLOs on the Prometheus backend.
