# Custom application metrics: `System.Diagnostics.Metrics` collector, metric storage and metric SLOs

- **Date:** 2026-11-13
- **Supersedes:** `docs/specs/2026-11-12-custom-application-metrics.md`. That draft was never approved, and it used a Warp-specific `IWarpMetrics` emit API on top of `Counter → Statistic`.
- **Spec mode:** extended (rigor MAX)
- **Scope classification:** new-feature, additive and opt-in. Nothing is collected unless `opt.AddMetrics(...)` names a meter.
- **Security impact:** `pii-exposure`. Tag values from collected instruments are persisted to the operational database. Every default is "collect nothing"; see Security and compliance.
- **Schema impact:** additive. Two new tables, `metric_point` and `metric_instrument`, plus 7 nullable columns on `slo_definition`. Hosts pick them up through their normal `dotnet ef migrations add`.
- **Delivery:** one branch (`feat/custom-application-metrics`), one PR, shipping as **7.5.0**.
- **Source brief:** a post-deploy monitoring brief from the first consuming application (a payment service), 2026-11-12.
- **Workflow:** `wf-20261112T180940Z-3c6da5`
- **Plan-gap review (v2):** 4 blocking and 5 advisory findings, all resolved in this revision: the reused `MetricTier`; `WarpTestServer` and `IdleLoopSwitches` loop switches; dashboard page, overrides, nav model and demo wiring; `SloDetailPage` and SLO demo fixtures; B1 wording; stale test comments; `MetricRollup` topology coverage; the version literal; the `--filter-class` fallback.

## Engineer decisions (ambiguity gate, 2026-11-12 and 2026-11-13)

| Decision | Choice |
|---|---|
| Delivery | One spec, one PR |
| Emit API | **The standard .NET `System.Diagnostics.Metrics` API** (`IMeterFactory`, `Counter<T>`, `Histogram<T>`, …). Warp is a **collector** (`MeterListener`), not an emit API, so host code takes no Warp dependency. |
| Storage | **A dedicated typed table** (`metric_point`) with its own downsampling task, not the `Counter → Statistic` key-value fold |
| Controller observability | **The built-in meter only.** Collect ASP.NET Core's `http.server.request.duration` through a preset. There is no Warp middleware, no per-request rows, and no Issues integration for host endpoints. |
| Deploy markers and post-deploy verdict | **Dropped** ("we don't need deploys for now"), and with them the CI authentication question |

---

## Summary

1. **Collector.** `opt.AddMetrics(m => m.CollectMeter("Shop.Payments").CollectAspNetCoreHttp().CollectHttpClient())` starts one `MeterListener` per process. The listener:
   - enables only the instruments of opted-in meters;
   - projects their tags through an optional per-instrument allowlist;
   - aggregates each measurement **in memory** into a per-(series, 5-min bucket) aggregate: count, sum, min, max, histogram buckets.
   
   It does no I/O on the measuring thread. This works in **any** `AddWarp` process, server or not.
2. **Storage.** A per-process `MetricFlusher` writes partial aggregates as `metric_point` rows every `MetricFlushInterval` (30s) and once more on graceful shutdown. A new `MetricRollup` server task:
   - compacts the partial rows of each closed 5-minute bucket into one row;
   - rolls fine rows into hourly and hourly into daily on the existing retention settings;
   - prunes the daily tier.
   
   A `metric_instrument` registry records each instrument's kind, unit, bucket bounds, observed tag keys and collapse state.
3. **Fold without a worker.** `CounterAggregator`, `ErrorGroupAggregator` and `SloEvaluator` move from the worker-only registration block to `AddServerHostCore`, beside `StatisticRollup` and the new `MetricRollup`. A `DisableWorker()` server then runs every observability task and still never claims a job.
4. **Metrics page.** A list page and a detail page, with break-down-by-tag, a filter, 1-day and 7-day compare overlays, a ratio view and histogram percentiles.
5. **Metric SLOs.** `MetricRatio`, `MetricThreshold`, `MetricVolumeFloor` and `MetricGap` read `metric_point` and use the existing burn, NoData, edge-alert and acknowledge machinery.
6. **Coexistence.** Tests prove that a host `SaveChanges` interceptor and MediatR 12.5 behave identically before and after `AddWarp`, and that a service-only server never claims jobs. The caveats are documented.

---

## Context: what exists today (verified)

| Fact | Evidence |
|---|---|
| `System.Diagnostics.Metrics` (net10.0) ships the following: `MeterListener` (`Start`, `EnableMeasurementEvents`, `SetMeasurementEventCallback<T>`, `RecordObservableInstruments`, `DisableMeasurementEvents`); the instruments `Counter`, `UpDownCounter`, `Histogram`, `Gauge`, `ObservableCounter`, `ObservableUpDownCounter`, `ObservableGauge`; and `Instrument<T>.Advice` with `InstrumentAdvice<T>.HistogramBucketBoundaries` | `C:/Program Files/dotnet/packs/Microsoft.NETCore.App.Ref/10.0.12/ref/net10.0/System.Diagnostics.DiagnosticSource.xml` |
| ASP.NET Core references `http.server.request.duration` (meter `Microsoft.AspNetCore.Hosting`) | `Microsoft.AspNetCore.App.Ref/10.0.12/ref/net10.0/Microsoft.AspNetCore.Diagnostics.xml` |
| `CounterAggregator`, `ErrorGroupAggregator`, `SloEvaluator` and `BacklogSampler` are registered after `if (!runWorker) return services;`, so a `DisableWorker()` server does not run them | `src/core/Warp.Worker/ServiceConfiguration.cs:156-191` |
| `StatisticRollup`, `Heartbeat`, `ServerCleanup` and `ExpirationCleanup` run on every server | `src/core/Warp.Worker/ServiceConfiguration.cs:340-358` |
| An existing test asserts `CounterAggregator` is **absent** on a service-only server | `src/tests/Warp.Tests/Admin/DeploymentShapeTests.cs:275` |
| The tier settings already exist: `FineResolutionMinutes` 5, `FineResolutionRetention` 6h, `HourlyStatisticsRetention` 7d, `DailyStatisticsRetention` 90d (null keeps forever). The bounded, deferring sums-then-delete rollup pattern also exists. | `src/core/Warp.Core/Configuration.cs:267-289`; `src/core/Warp.Worker/Services/StatisticRollup.cs` |
| `SloKind` is 1–5, `SloState` is 1–5, and `SloDefinition` has no column for a metric, tags or a baseline. `SloQueryService.Project()` maps fields one by one. | `src/core/Warp.Core/Data/Enums/SloEnums.cs:9-52`; `src/core/Warp.Core/Data/Entities/SloDefinition.cs`; `src/core/Warp.Core/Services/SloQueryService.cs:52-71` |
| `SloEvaluator` alerts on the edge into Breaching, post-commit from `OnCommittedAsync`, with ack suppression | `src/core/Warp.Worker/Services/SloEvaluator.cs:82-116` |
| The per-process `BackgroundService` reporter precedent, which does no DB work when idle | `DroppedRecordReporter` (§8.32) |
| The cardinality-guard precedent | `src/core/Warp.Core/ClientObservability/ClientEventCardinality.cs` (§8.27) |
| `MeterListener` is process-global, so tests must isolate by a unique tag or meter (lesson 2026-08-05) | `tasks/lessons.md:159` |
| `Warp.Core` grants `InternalsVisibleTo` to `Warp.Worker`, so seams between them can be internal | `src/core/Warp.Core/Warp.Core.csproj:12` |

---

## Architecture and design

### A. Topology

```
any AddWarp process with AddMetrics(...)                any AddWarpServer (worker on OR DisableWorker())
─────────────────────────────────────────                ───────────────────────────────────────────────
host code: meter.CreateCounter<long>("payment.results")  MetricRollup          (new, AddServerHostCore)
ASP.NET Core: http.server.request.duration               SloEvaluator          (moved to AddServerHostCore)
HttpClient:   http.client.request.duration               CounterAggregator     (moved)
        │ measurement callback (caller's thread)         ErrorGroupAggregator  (moved)
        ▼                                                StatisticRollup       (already there)
WarpMeterCollector (MeterListener, singleton)
        │ tag projection + cardinality guard
        ▼
MetricAggregator: series × 5-min bucket → {count, sum, min, max, buckets[]}   (in memory)
        │ every MetricFlushInterval (30s): RecordObservableInstruments(), then drain
        ▼ + final drain on StopAsync
MetricFlusher (BackgroundService) ── scope → IMetricPointSink
        ├─ AddWarp-only: ContextMetricPointSink<TContext>
        └─ server:       ServerMetricPointSink (IWarpServerContext; replaces the above)
        ▼
metric_point (partial fine rows) ── MetricRollup ──► compacted m5 → h1 → d1
metric_instrument (registry)
```

- **Opt-in.** Without `AddMetrics`, or with no meter collected, no listener is started and no flusher does any work, so behaviour is byte-for-byte unchanged. The tables are always in the schema (§2.11).
- **The worker hot path is untouched** (§0.2/§6.1): no worker file is in the manifest.
- **Warp's own `Warp` meter is never collected.** Its data is already persisted through its own pipelines, so collecting it would double-count.
- **Crash loss:** an ungraceful exit loses at most one flush interval. Graceful shutdown drains on `StopAsync`, inside the host `ShutdownTimeout` (default 30s, the same as ECS's SIGTERM grace).
- **Not transactional:** a measurement recorded inside a request that later rolls back still counts. Documented. These are diagnostics, not an audit trail.
- **Coexistence with the OTel SDK:** several `MeterListener`s receive the same measurements independently, so an OTel exporter keeps working unchanged. Documented.
- **Moving the three tasks:** `CounterAggregator`, `ErrorGroupAggregator` and `SloEvaluator` move to `AddServerHostCore`. All three are cluster-locked (`LockKey`), so a mixed worker and service-only cluster still runs each tick once. Existing `DisableWorker()` servers start running them, which is called out in the release notes. `BacklogSampler` stays worker-only because it is job-specific.

### B. Collector (`Warp.Core.Metrics`)

```csharp
opt.AddMetrics(m =>
{
    m.CollectMeter("Shop.Payments");                       // all instruments, all tags (capped)
    m.CollectMeter("Shop.Webhooks", meter => meter
        .Instrument("adyen.notifications.received", i => i.KeepTags("type", "event_code", "outcome")));
    m.CollectAspNetCoreHttp();   // preset: Microsoft.AspNetCore.Hosting / http.server.request.duration,
                                 //   keep http.route, http.request.method, http.response.status_code
    m.CollectHttpClient();       // preset: System.Net.Http / http.client.request.duration,
                                 //   keep http.request.method, server.address, http.response.status_code, error.type
    m.Instrument("payment.latency", i => i.Buckets(5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000)); // override advice
});
```

- **Registration:** `AddMetrics(this IWarpBuilder, Action<WarpMetricsOptions>)`, the non-generic receiver from §2.13. It registers the following:
  - `WarpMeterCollector` as a singleton, started by `MetricCollectorHost` (an `IHostedService`) so the listener lives exactly as long as the host;
  - the `MetricAggregator` singleton;
  - `MetricFlusher`;
  - `ContextMetricPointSink<TContext>` (`TryAddScoped`);
  - the `IMetricsMarker` that gates the dashboard flag.
- **Presets** are configuration strings only (meter name, instrument name, tag allowlist). `Warp.Core` gains **no** ASP.NET Core dependency.
- **Instrument enabling:**
  - In `InstrumentPublished`, call `EnableMeasurementEvents` only when `instrument.Meter.Name` is collected, the meter is not `Warp`, and (if an instrument filter is set) the instrument is listed.
  - One `SetMeasurementEventCallback<T>` is registered per numeric type (`byte`, `short`, `int`, `long`, `float`, `double`, `decimal`). Every value is converted to `double`.
- **Instrument kind → aggregate:**

| Instrument | `MetricKind` | Per-bucket aggregate | "Value" for SLOs and charts |
|---|---|---|---|
| `Counter<T>` | `Counter = 1` | count (measurements), sum | sum |
| `ObservableCounter<T>` | `Counter = 1` | per-series delta from the last observed cumulative value in this process (the first observation records a baseline only) | sum |
| `UpDownCounter<T>` / `ObservableUpDownCounter<T>` | `UpDownCounter = 2` | sum of deltas | sum |
| `Histogram<T>` | `Histogram = 3` | count, sum, min, max, bucket counts | count (rates); percentiles from buckets |
| `Gauge<T>` / `ObservableGauge<T>` | `Gauge = 4` | count, sum, min, max (avg = sum/count) | avg / min / max |

- **Histogram bounds**, in order of precedence:
  1. the configured `i.Buckets(...)`;
  2. `Instrument<T>.Advice.HistogramBucketBoundaries`, which ASP.NET Core and HttpClient set;
  3. a default chosen by unit: `s` gets the OTel seconds ladder (0.005 … 10); `ms` gets the millisecond ladder (5 … 10000); anything else gets the OTel default explicit ladder.
  
  There are at most 20 bounds, strictly increasing. The overflow bucket is implicit. Bounds are stored on the instrument registry and on each histogram row as text.
- **Series identity:** (instrument name, application, canonical tag set).
  - Application is the sanitized `ApplicationName`, or null. It is added automatically as a dimension and is never read from a tag.
  - The canonical tag set is `k1=v1,k2=v2`, sorted by key in ordinal order and projected through the allowlist.
  - Tag value handling:
    - values are formatted invariant (`int` and `bool` with `ToString(CultureInfo.InvariantCulture)`);
    - values are trimmed and truncated to 64 characters;
    - null or empty becomes `{none}`;
    - `,` and `=` become `-`, and control characters are stripped.
  - At most `MaxTagsPerInstrument` tags (default **6**). Extra keys are dropped in ordinal order and reported.
- **Cardinality guard** (`MetricCardinalityGuard`; in memory, per process; the §8.27 shape):
  - **Tag values:** at most `MaxValuesPerTag` (default **100**) distinct values per (instrument, tag key). Later values become `{other}`.
  - **Series:** at most `MaxSeriesPerInstrument` (default **500**) per instrument. Beyond that the tag set becomes `{other}`.
  - **Instruments:** at most `MaxInstruments` (default **300**) per process. Beyond that the instrument is **disabled** (`DisableMeasurementEvents`) and reported.
  - **Reporting** happens at three levels, and **never includes a tag value** (§1.2):
    - a Warning log once per (instrument, tag key, reason);
    - the always-on meter `warp.metrics.collapsed{instrument, reason}`;
    - a `metric_instrument.CollapsedAt` / `CollapsedTags` badge.
- **Callback cost:** the callback runs on the caller's thread, so it must stay in-memory work. It projects tags into a pooled buffer, looks up a `ConcurrentDictionary` keyed by a precomputed series key, and updates the aggregate (`Interlocked` for count and sum; a small per-aggregate lock for min, max and buckets).
  - **SC10** benchmarks it in `Warp.Benchmarks` with a budget of ≤ 1 µs and ≤ 1 allocation per measurement for an already-seen series.
  - Callback exceptions are caught, counted on `warp.metrics.collapsed{reason=error}`, and never propagate into the host's request (§8.19 "instrumentation never out-throws").

### C. Storage

**`MetricPoint`** (`Warp.Core.Data.Entities`, always in the schema per §2.11, mirrored by `WarpServerContext` per §2.14):

| Column | Type | Notes |
|---|---|---|
| `Id` | long, identity | PK |
| `Instrument` | string(128) | instrument name |
| `Application` | string(128)? | |
| `Tags` | string(512) | canonical tag set, `-` when there are no tags |
| `Tier` | the existing `Warp.Core.Services.MetricTier` (Fine=1, Hourly=2, Daily=3), promoted to public | `HasConversion<int>` (§8.11) |
| `BucketStart` | DateTime (UTC) | aligned to the tier |
| `Count` | long | |
| `Sum` | double | |
| `Min`, `Max` | double? | histograms and gauges |
| `Buckets` | string(512)? | histograms: CSV of counts aligned to `Bounds`, plus overflow |
| `Bounds` | string(256)? | histograms: CSV of upper bounds |
| `Compacted` | bool | true once the row is the single compacted row for its key |

Two indexes:
- `(Instrument, Tier, BucketStart)` for reads;
- `(Tier, Compacted, BucketStart)` for rollup scans.

**`MetricInstrument`** (registry): `Name` (PK, 128), `Meter` (128), `Kind` (`MetricKind`), `Unit?` (32), `Description?` (256), `Bounds?` (256), `TagKeys?` (256), `FirstSeenAt`, `LastSeenAt`, `CollapsedAt?`, `CollapsedTags?` (256). If two meters publish the same instrument name, the first one wins and a Warning is logged.

- **Flush** (`MetricFlusher`, Core, per-process `BackgroundService`; not a locked `IServerTask` because state is per process, as in §8.32). Every `MetricFlushInterval` (default 30s, configured on `WarpConfiguration`), and once more on `StopAsync`:
  1. call `RecordObservableInstruments()`;
  2. drain the aggregator, swapping each aggregate out by `TryRemove`, which is the drain proven lossless under concurrency in `WarpCounterBuffer:53-62`;
  3. insert one `metric_point` row per drained (series, fine bucket) with `Compacted = false`;
  4. then, as a **separate** `SaveChangesAsync`, upsert `metric_instrument` rows that are new to this process, not refreshed in the last hour, or newly collapsed.
  
  Failure handling:
  - A failed point write merges the drained aggregates back. The re-add is additive, so nothing is lost or double-counted.
  - A registry PK race (two processes inserting) is logged at Debug and retried as an update on the next flush. It never fails or re-adds the points.
  - An idle aggregator does no DB work.
- **Sinks:** an internal `IMetricPointSink`, which also writes registry rows.
  - `ContextMetricPointSink<TContext>` is used in `AddWarp`-only processes. That is the user-side `TContext`, like the adapter, endpoint and client flushers (§2.15).
  - `ServerMetricPointSink` (Warp.Worker, `IWarpServerContext`) replaces it in `AddServerHostCore`.
  - Either way there is exactly one flusher per process.
- **Rollup** (`MetricRollup<TContext>`, `IServerTask`):
  - `LockKey = "warp:metric-rollup"`, cadence `MetricRollupInterval` (default **1 min**).
  - Registered in `AddServerHostCore`. Runs under the transaction lock (§2.16).
  - Each tick does three bounded passes, sharing `MaxMetricRollupRowsPerTick` = 20,000 source rows, with `RerunImmediately` and a null return when idle (§6.2.1):
  - The passes:
    1. **Compact:** fine rows of closed buckets (`BucketStart + FineResolutionMinutes + 2 × MetricFlushInterval < now`) that are not yet compacted are grouped by (Instrument, Application, Tags, BucketStart, Bounds). Each group becomes one `Compacted = true` row: summed count, sum and buckets, min of mins, max of maxes. The sources are deleted.
    2. **Fine → hourly**, for compacted fine rows older than `FineResolutionRetention`.
    3. **Hourly → daily**, for hourly rows older than `HourlyStatisticsRetention`, then prune daily rows older than `DailyStatisticsRetention`.
  - Every pass sums into the parent row (insert, or update if it exists) and then deletes the sources. A capped pass **defers rather than drops**: the same crash-idempotent, sums-then-delete contract as `StatisticRollup` (§8.30), reproduced and asserted by the same style of test.
  - Rows whose `Bounds` differ never merge.
- **Retention:** `ExpirationCleanup` gains one bounded delete of `metric_instrument` rows whose `LastSeenAt` is older than `DailyStatisticsRetention`, or never when that is null. It is guarded with `FindEntityType` (lesson).
- **Readers sum on read.** Fine rows of the current or just-closed bucket may still be partial, with several rows per series, so every read sums by key. Compaction is an optimisation, never a correctness requirement.

### D. Read API and Metrics page

- **`IMetricQueryService`** (`Warp.Core.Services`) is registered by `AddWarp` itself, so dashboard-only hosts resolve it.
  - `ListInstruments()` reads the registry and never scans points.
  - `GetSeries(name, MetricSeriesQuery)` takes `From`/`To`, `Application?`, `Breakdown?` (one tag key), `Filters` (exact match, or a trailing `*` prefix such as `http.response.status_code=5*`), `Compare` (`1d`/`7d`) and `Percentile?`.
    - **Display resolution** by range length: ≤ 6h is fine, ≤ 7d hourly, otherwise daily.
    - **Amended 2026-11-13** (engineer-approved; was "read one tier by range"): the read covers **every tier that overlaps `[From, To)`** and re-bins each row into the display resolution. Each row's `BucketStart` is floored to the display bucket. A row coarser than the display bucket keeps its own bucket and is labelled by its own resolution.
      - **Why:** fine rows only roll to hourly after `FineResolutionRetention` (6h), and hourly rows to daily after `HourlyStatisticsRetention`. A single-tier read therefore misses the newest data: a "last 24h" hourly read would lack the last ~6h.
      - **No double counting:** rollup deletes each source row in the same transaction that adds it to its parent, so a value lives in exactly one tier at any moment.
    - The query is `AsNoTracking` on `(Instrument, Tier IN overlapping tiers, BucketStart range[, Application])`, projected and grouped **in SQL** by (`Tier`, `BucketStart`, `Tags`, `Bounds`). Tag filtering, breakdown, re-binning, percentile walks and bucket merging happen in memory on that grouped set.
    - Compare overlays are the same range shifted back, read the same way (all overlapping tiers, re-binned). Each series is labelled with the coarsest resolution it contains.
    - Breakdown returns the top 10 values by volume, with the rest summed into `(rest)`.
- **API** (minimal API in `WarpEndpoints.cs`, inheriting group auth per §1.5):
  - `GET {prefix}/api/metrics`
  - `GET {prefix}/api/metrics/{id}/series?...`, where `{id}` is the `UrlSafeId` of the instrument name.
  - `WarpAddonsInfo.Metrics` is true when `IMetricsMarker` is registered, so the nav shows only where `AddMetrics` ran in the dashboard process. `ShowMetrics()` forces it on for dashboard-only hosts, mirroring `ShowSlo()`.
- **UI** under `src/ui/src/pages/metrics/`:
  - `MetricsPage.tsx`: the list, with kind, unit, tag keys, last seen and a collapse badge.
  - `MetricDetailPage.tsx`, which has:
    - a range picker, an application picker and a breakdown selector;
    - filter chips;
    - 1d/7d overlay toggles drawn as dashed Chart.js datasets;
    - a percentile selector for histograms; avg, min and max for gauges;
    - a **ratio view** where the user picks the numerator values of the breakdown tag, for example status `5*` over total.
  - The maths lives in a pure `metricModel.ts` with vitest coverage (the `counterModel` precedent, §8.33). Demo-mode fixtures are added.

### E. SLOs on custom metrics

- **`SloKind` additions** (append-only): `MetricRatio = 6`, `MetricThreshold = 7`, `MetricVolumeFloor = 8`, `MetricGap = 9`.
- **For metric kinds, `SloDefinition.Dimension` holds the instrument name.** `Application` scopes the series; null means all applications.
- **New nullable columns:**
  - `TagFilter` (512)
  - `SecondaryMetric` (128)
  - `SecondaryTagFilter` (512)
  - `RatioTag` (64)
  - `GoodValues` (512, CSV; trailing `*` allowed)
  - `BadValues` (512, CSV; trailing `*` allowed)
  - `BaselineDays` (`int?`, 1 or 7)
- **Event quantity:** the per-bucket "events" an objective counts are `Sum` for counters and up-down counters, and `Count` for histograms. Gauges are rejected by validation for these kinds.
- **Filter grammar:** `k=v[,k=v…]`, where a value may end in `*`. Parsed once per tick by `MetricFilter` (pure, unit-tested).

| Kind | Observed over (now − W, now] | Breaching when | Budget (feeds `SloMath.Classify`) | NoData when |
|---|---|---|---|---|
| `MetricRatio` | good = events where `RatioTag` matches `GoodValues`; bad = events matching `BadValues`; every other value ignored (e.g. `refused`) | good/(good+bad) < target, via the existing `EvaluateRate` | the existing rate budget | good + bad = 0 |
| `MetricRatio` + `BaselineDays` | as above, plus the same ratio over the shifted window | baseline ratio − current ratio > target, where target is the max drop in ratio points (0 < t < 1) | `(target − drop) / target` | current or baseline good + bad = 0 |
| `MetricThreshold` | events matching `TagFilter` | observed > N (N ≥ 0, **0 allowed**) | N = 0: `obs == 0 ? 1 : −obs`; else `(N − obs) / N` | the instrument has **no rows at all** in the window. Traffic with zero matches is Healthy at 0, so "5xx = 0" is not a false NoData, while a typo'd instrument still reads NoData. |
| `MetricVolumeFloor` | matching events, now vs the same window `BaselineDays` earlier (read at hourly resolution, prorated by overlap) | obs / baseline < target (0 < t < 1) | `(obs/baseline − target) / (1 − target)` | baseline = 0 |
| `MetricGap` | events(primary, `TagFilter`) − events(secondary, `SecondaryTagFilter`) | gap > N (N ≥ 0) | as `MetricThreshold` | neither instrument has rows in the window |

- **Windows:** the short window is `max(5 min, W/12)`, as today. The **minimum `WindowSeconds` for metric kinds is 600**, so the window always spans at least one closed bucket plus the flush lag. The window's trailing edge includes partial fine rows, which is what makes fast burn react within about one flush interval.
- **Validation** (`SloValidation`, shared by the 400 response and the throw):
  - `Dimension` must be a valid instrument name.
  - Each kind's required fields must be present.
  - `MetricRatio` needs non-empty `GoodValues` and `BadValues` that don't overlap.
  - Target ranges are per kind. This relaxes today's "threshold > 0" for metric kinds only; existing kinds keep it.
  - **A 7-day baseline requires `HourlyStatisticsRetention` ≥ 7 days + window + 1 hour.** The hourly baseline would otherwise already be daily. The validation message names the setting.
- **Builder sugar** on the existing `AddSlo` options: `AddMetricRatio`, `AddMetricThreshold`, `AddMetricVolumeFloor`, `AddMetricGap`. For metric kinds the seeder's insert-if-absent match key adds `TagFilter`, `SecondaryMetric` and `RatioTag`.
- **Evaluator:** new arms in `SloEvaluator`.
  - Reads are `metric_point` grouped by (`Tier`, `BucketStart`, `Tags`) for the window's instruments, cached per tick per instrument, on `IWarpServerContext` (§2.14).
  - **Amended 2026-11-13:** every window and baseline read covers **all tiers overlapping the window** (fine, hourly and daily), the same rule as §D. A row is counted when its bucket overlaps the window, and a coarser bucket that only partly overlaps is prorated by overlap, as the baseline already is. A single-tier read would miss the newest data that has not yet rolled up.
  - There is **no** change to classification, NoData handling, edge alerting (`SloBreached`, once per edge, ack-suppressed), post-commit dispatch (§8.25) or acknowledgement.
  - `SloBreachedEvent` gains a nullable `Metric` property (additive).
- **Read and UI:**
  - `SloQueryService.Project()` and `SloModels` expose the new fields.
  - The `SloUpsertRequest` DTO accepts them.
  - The SLO form shows kind-specific fields, with an instrument picker fed by `/api/metrics`.

### F. Controllers (engineer decision: built-in meter only)

There is no Warp code for controllers. `m.CollectAspNetCoreHttp()` collects `http.server.request.duration`, which gives count, status code, a latency histogram per `http.route` and the application. That covers routed controllers and minimal APIs alike. The docs show:
- **5xx count ≤ 0:** `MetricThreshold` on `http.server.request.duration`, filter `http.route=/api/adyen/*,http.response.status_code=5*`.
- **Success ratio:** `MetricRatio` with `RatioTag = http.response.status_code`, good `2*,3*`, bad `5*`.

Issues for host-endpoint 5xx is **out of scope**. The existing `Warp.Http` endpoint observability is unchanged.

### G. Coexistence (tests and docs)

- **Host DbContext:** a test host context with its own `SaveChangesInterceptor` (sync and async, stamping an audit column), `UseSnakeCaseNamingConvention()` and an auditable entity. The test asserts:
  - the host interceptor fires the same number of times with the same stamps, with and without `AddWarp` plus `AddMetrics`;
  - Warp's interceptor leaves a non-`IConcurrencyToken` entity untouched;
  - EF's `IMigrationsModelDiffer` from an empty model yields create-table operations for every `warp`-schema table, including `metric_point` and `metric_instrument`.
- **Documented caveats** (`ef-core-integration.md`):
  - `AddDbContext` must precede `AddWarp` (otherwise the hook silently no-ops; `src/core/Warp.Core/ServiceConfiguration.cs:392-395`).
  - Warp replaces any custom `IModelCustomizer`; use `ApplyWarpModel` in `OnModelCreating` instead.
  - MediatR and `Warp.Core.Handlers` share type names, so a file importing both needs aliases (CS0104).
- **MediatR 12.5.0:** a test-project reference, **pinned**. A NoDb test registers `AddMediatR` (scanning the test assembly) plus `AddWarp`, then asserts:
  - a MediatR request runs through a MediatR `IPipelineBehavior` exactly once;
  - a Warp `IRequest` runs through Warp's `IMediator`;
  - Warp's generated registry has no MediatR type, and MediatR resolved no Warp handler.
- **Hangfire or another job runner:** documented as a supported shape. Run `AddWarp` plus `AddMetrics` everywhere, plus one `AddWarpServer(o => o.DisableWorker())`. The end-to-end test proves the service-only server rolls up metrics, evaluates SLOs, and leaves a seeded `Enqueued` job `Enqueued`.
- **PCI guidance** (`custom-metrics.md`, `adapters.md`):
  - tag values must never carry PII, PAN or customer identifiers;
  - use `KeepTags` allowlists on any instrument whose tags you don't control;
  - adapter capture must stay `None` for payment processors, and `ForceCapture` must not be used on them.

---

## Requirements

### Collector and topology

- R1 — Where `AddMetrics` collects at least one meter, the system shall enable measurement events only for instruments of collected meters, and never for the meter named `Warp`.
- R2 — When a collected instrument records a measurement, the system shall aggregate it in memory without opening a database connection or performing I/O on the measuring thread.
- R3 — If the measurement callback throws, then the system shall catch the exception, count it on `warp.metrics.collapsed`, and return normally to the measuring code.
- R4 — The system shall identify a series by instrument name, `ApplicationName` and the tag set sorted by key in ordinal order, after projection through the instrument's tag allowlist.
- R5 — When an `ObservableCounter` is observed, the system shall record the delta from the previous cumulative value of that series in this process, and shall record no events for the first observation.
- R6 — Where a histogram instrument has configured buckets, the system shall use them; otherwise, where it carries `InstrumentAdvice` bucket boundaries, the system shall use those; otherwise the system shall use the default ladder for its unit.
- R7 — If a tag key's distinct values for an instrument in a process exceed `MaxValuesPerTag`, then the system shall record further new values as `{other}`, log one Warning naming the instrument and tag key without the value, and increment `warp.metrics.collapsed`.
- R8 — If an instrument's distinct series in a process exceed `MaxSeriesPerInstrument`, then the system shall record the measurement under the `{other}` tag set and report the collapse.
- R9 — If a process exceeds `MaxInstruments` collected instruments, then the system shall disable measurement events for each further instrument and report it.
- R10 — The system shall flush aggregated points every `MetricFlushInterval` and once more during graceful host shutdown.
- R11 — While a process has registered `AddWarp` but not `AddWarpServer`, the system shall write metric points on the host's `TContext`; while it runs `AddWarpServer`, the system shall write them on `IWarpServerContext`.
- R12 — If a point write fails, then the system shall merge the drained aggregates back into the aggregator so the next flush writes them.
- R13 — While a server runs with or without the worker, the system shall run `MetricRollup`, `SloEvaluator`, `CounterAggregator`, `ErrorGroupAggregator` and `StatisticRollup`.
- R14 — While a server is configured with `DisableWorker()`, the system shall not claim any job.

### Storage and rollup

- R15 — When a fine bucket has been closed for longer than twice `MetricFlushInterval`, the system shall compact its partial rows per series into a single row whose count, sum and bucket counts equal the sums of the sources and whose min and max equal the extremes of the sources.
- R16 — When a compacted fine row is older than `FineResolutionRetention`, the system shall add it into its hourly row and delete it; when an hourly row is older than `HourlyStatisticsRetention`, the system shall add it into its daily row and delete it.
- R17 — If a rollup tick reaches `MaxMetricRollupRowsPerTick`, then the system shall leave the remaining rows for later ticks without losing or double-counting any value.
- R18 — The system shall never merge histogram rows whose bucket bounds differ.

### Read API

- R19 — The system shall serve `GET {prefix}/api/metrics` and `GET {prefix}/api/metrics/{id}/series` from any process that calls `AddWarp`.
- R20 — When a series request asks for a breakdown tag, the system shall return one series per tag value for the top 10 values by volume and one `(rest)` series for the remainder.
- R21 — When a series request asks for `compare=1d` or `compare=7d`, the system shall return the same range shifted back by that offset, labelled with the resolution it was served at.
- R21a — When a series request covers a range that spans more than one storage tier, the system shall read every overlapping tier and re-bin it into the display resolution, so that data not yet rolled up is included exactly once. (Amended 2026-11-13.)

### Metric SLOs

- R22 — When a `MetricRatio` objective is evaluated, the system shall count only events whose ratio tag matches `GoodValues` or `BadValues`.
- R23 — When a `MetricThreshold` objective's instrument has rows in the window and the matching event count exceeds the target, the system shall classify the objective as Breaching.
- R24 — While a `MetricThreshold` objective's instrument has rows in the window but none match the filter, the system shall classify the objective as Healthy with an observed value of 0.
- R25 — When a `MetricVolumeFloor` objective's current count divided by its baseline count is below the target, the system shall classify the objective as Breaching.
- R26 — When a `MetricGap` objective's primary count minus secondary count exceeds the target, the system shall classify the objective as Breaching.
- R27 — If a metric objective has no data under its kind's NoData rule, then the system shall classify it as NoData and shall not dispatch an alert.
- R28 — When a metric objective transitions into Breaching from any non-Breaching state without an active acknowledgement, the system shall dispatch exactly one `SloBreached` event post-commit through `IWarpNotifier`.
- R29 — If a metric objective uses `BaselineDays = 7` while `HourlyStatisticsRetention` is shorter than 7 days plus the window plus 1 hour, then the system shall reject the objective at validation.

### Coexistence

- R30 — When a host context with its own `SaveChangesInterceptor` saves changes, the system shall leave that interceptor's invocation count and effects unchanged by `AddWarp`.
- R31 — The system shall not register or wrap any MediatR request, handler or pipeline-behavior type.

---

## Security and compliance impact

**Level: `pii-exposure`.** Tag values of collected instruments are persisted to the operational database in plain text.

| Surface | Risk | Control |
|---|---|---|
| Custom instrument tags | A host tags a measurement with a PAN, email or customer id | Opt-in per meter. `KeepTags` allowlists. 64-character truncation. Cardinality collapse limits the blast radius. Values are never logged (§1.2). Docs state plainly that Warp cannot detect PII in a free string. |
| Built-in ASP.NET Core and HttpClient instruments | Extra tags (`url.scheme`, `network.*`, `server.address`) inflate storage or leak hostnames | Presets keep only the listed tags. `http.route` is the template, not the raw path. |
| Adapter capture | `ForceCapture` overrides `None` on a payment adapter | Docs-only warning. Behaviour unchanged. |
| New read API | `/api/metrics*` exposes instrument names and tag values | Inherits dashboard group authorization (§1.5). No new auth path. |

---

## Change manifest

Every path is relative to `warp/`.

**Manifest pre-flight:** MP004 ("new directory") warnings are answered by the feature-folder convention: `src/core/Warp.Core/Metrics/` follows `Warp.Core/ClientObservability/` and `ErrorGrouping/`, and `src/ui/src/pages/metrics/` follows `pages/issues/` and `pages/slo/`.

### B1: topology

| Path | Action | Purpose |
|---|---|---|
| `src/core/Warp.Worker/ServiceConfiguration.cs` | modify | Move `CounterAggregator`, `ErrorGroupAggregator` and `SloEvaluator` into `AddServerHostCore` |

### B2: collector

| Path | Action | Purpose |
|---|---|---|
| `src/core/Warp.Core/Metrics/WarpMetricsOptions.cs` | create | `CollectMeter`, `Instrument`, `KeepTags`, `Buckets`, presets, caps |
| `src/core/Warp.Core/Metrics/MetricsServiceConfiguration.cs` | create | `AddMetrics(IWarpBuilder, …)` and registrations |
| `src/core/Warp.Core/Metrics/IMetricsMarker.cs` | create | Dashboard flag marker |
| `src/core/Warp.Core/Metrics/WarpMeterCollector.cs` | create | `MeterListener`: enable, callbacks, observable deltas |
| `src/core/Warp.Core/Metrics/MetricCollectorHost.cs` | create | `IHostedService` start and dispose |
| `src/core/Warp.Core/Metrics/MetricAggregator.cs` | create | In-memory series × bucket aggregates; lossless drain and merge-back |
| `src/core/Warp.Core/Metrics/MetricSeries.cs` | create | Tag projection, canonical tag set, sanitization |
| `src/core/Warp.Core/Metrics/MetricBuckets.cs` | create | Bounds resolution (config > advice > unit default); bucket index |
| `src/core/Warp.Core/Metrics/MetricCardinalityGuard.cs` | create | Caps and reporting |
| `src/core/Warp.Core/Data/Enums/MetricKind.cs` | create | `Counter=1, UpDownCounter=2, Histogram=3, Gauge=4` |
| `src/core/Warp.Core/Logging/WarpTelemetry.cs` | modify | `warp.metrics.collapsed` |

### B3: storage and flush

| Path | Action | Purpose |
|---|---|---|
| `src/core/Warp.Core/Data/Entities/MetricPoint.cs` | create | Point entity |
| `src/core/Warp.Core/Data/Entities/MetricInstrument.cs` | create | Registry entity |
| `src/core/Warp.Core/Services/MetricTiers.cs` | modify | Promote the **existing** `internal enum MetricTier { Fine=1, Hourly=2, Daily=3 }` to `public`, because the public entity exposes it. No value or namespace change, and no second enum (which would hit CS0104 against `StatisticRollup` and the worker's users). |
| `src/core/Warp.Core/ServiceConfiguration.cs` | modify | EF config for both entities and indexes |
| `src/core/Warp.Core/Configuration.cs` | modify | `MetricFlushInterval` (`TimeSpan`, 30s, > 0; the flusher exists only under `AddMetrics`) and `MetricRollupInterval` (`TimeSpan?`, 1m, **null disables the loop**, like `StatisticRollupInterval`) |
| `src/core/Warp.Core/Metrics/IMetricPointSink.cs` | create | Internal sink seam |
| `src/core/Warp.Core/Metrics/ContextMetricPointSink.cs` | create | `TContext` sink |
| `src/core/Warp.Core/Metrics/MetricFlusher.cs` | create | Per-process flush plus registry upsert |
| `src/core/Warp.Worker/Services/ServerMetricPointSink.cs` | create | `IWarpServerContext` sink |
| `src/tests/Warp.Tests/Fixtures/WarpTestServer.cs` | modify | Null `MetricRollupInterval` beside the other periodic loops (`:300-317`, SqlException 3980 neighbour contention). Feature tests drive the task directly. |
| `src/benchmarks/Warp.PerfTest/IdleLoopSwitches.cs` | modify | Add `MetricRollupInterval` to the per-loop idle switches |
| `src/core/Warp.Worker/ServiceConfiguration.cs` | modify | Replace the sink in `AddServerHostCore` |

### B4: rollup

| Path | Action | Purpose |
|---|---|---|
| `src/core/Warp.Worker/Services/MetricRollup.cs` | create | Compact, fine → hourly, hourly → daily, prune; bounded |
| `src/core/Warp.Worker/ServiceConfiguration.cs` | modify | Register `MetricRollup` in `AddServerHostCore` |
| `src/core/Warp.Worker/Services/ExpirationCleanup.cs` | modify | Stale `metric_instrument` sweep |

### B5: query and API

| Path | Action | Purpose |
|---|---|---|
| `src/core/Warp.Core/Services/IMetricQueryService.cs` | create | Read interface |
| `src/core/Warp.Core/Services/MetricQueryService.cs` | create | List and series reads |
| `src/core/Warp.Core/Models/MetricModels.cs` | create | Read models |
| `src/core/Warp.Core/ServiceConfiguration.cs` | modify | Register the query service in `AddWarp` |
| `src/core/Warp.Dashboard/Endpoints/WarpEndpoints.cs` | modify | `/api/metrics` routes |
| `src/core/Warp.Dashboard/Endpoints/WarpAddonsInfo.cs` | modify | `Metrics` flag |
| `src/core/Warp.Dashboard/Hosting/WarpDashboardOptions.cs` | modify | `ShowMetrics()` |
| `src/core/Warp.Dashboard/Hosting/WarpDashboardPage.cs` | modify | `WarpDashboardPage.Metrics` plus its `WarpDashboardPageIds.For` arm (the switch is exhaustive and throws on unknown) |
| `src/core/Warp.Dashboard/Hosting/WarpDashboardUiOverrides.cs` | modify | `bool? Metrics` override, read beside `options.Ui.Slo` |

### B6: Metrics UI

| Path | Action | Purpose |
|---|---|---|
| `src/ui/src/pages/metrics/MetricsPage.tsx` | create | List page |
| `src/ui/src/pages/metrics/MetricDetailPage.tsx` | create | Detail page |
| `src/ui/src/pages/metrics/metricModel.ts` | create | Pure ratio, overlay, rest and percentile maths |
| `src/ui/src/types/metrics.ts` | create | Types |
| `src/ui/src/api/index.ts` | modify | API calls |
| `src/ui/src/App.tsx` | modify | Routes |
| `src/ui/src/layouts/MainLayout.tsx` | modify | Nav entry |
| `src/ui/src/demo/data.ts` | modify | Demo fixture barrel |
| `src/ui/src/demo/data/metrics.ts` | create | Demo metrics fixtures |
| `src/ui/src/demo/adapter.ts` | modify | Demo routes for `/api/metrics*`, the `metrics` addon flag, and `version: '7.5.0'` (the only version literal in the source tree; the package version itself comes from the release tag) |
| `src/ui/src/types/index.ts` | modify | `Addons` gains `metrics` |
| `src/ui/src/layouts/navModel.ts` | modify | `metrics` addon union member and nav entry |

### B7: SLO model, maths and validation

| Path | Action | Purpose |
|---|---|---|
| `src/core/Warp.Core/Data/Enums/SloEnums.cs` | modify | Kinds 6–9 |
| `src/core/Warp.Core/Data/Entities/SloDefinition.cs` | modify | 7 nullable columns |
| `src/core/Warp.Core/ServiceConfiguration.cs` | modify | Column facets |
| `src/core/Warp.Core/Slo/SloValidation.cs` | modify | Per-kind rules and the retention check |
| `src/core/Warp.Core/Slo/MetricFilter.cs` | create | Filter grammar |
| `src/core/Warp.Core/Services/SloMath.cs` | modify | Count threshold, floor, gap and baseline-drop maths |
| `src/core/Warp.Core/Slo/SloServiceConfiguration.cs` | modify | Builder sugar; seeder match key |

### B8: SLO evaluation, read and API

| Path | Action | Purpose |
|---|---|---|
| `src/core/Warp.Worker/Services/SloEvaluator.cs` | modify | Metric arms |
| `src/core/Warp.Core/Services/SloCommandService.cs` | modify | Persist the new fields |
| `src/core/Warp.Core/Services/SloQueryService.cs` | modify | `Project()` maps the new fields |
| `src/core/Warp.Core/Models/SloModels.cs` | modify | Expose the new fields |
| `src/core/Warp.Core/Notifiers/WarpOperationalEvent.cs` | modify | `SloBreachedEvent.Metric` |
| `src/core/Warp.Dashboard/Endpoints/WarpEndpoints.cs` | modify | `SloUpsertRequest` fields |

### B9: SLO UI

| Path | Action | Purpose |
|---|---|---|
| `src/ui/src/pages/slo/SloPage.tsx` | modify | Kind-specific form |
| `src/ui/src/pages/slo/shared.tsx` | modify | Labels and formatting |
| `src/ui/src/types/slo.ts` | modify | Types; `SloKindLabel` for kinds 6–9 |
| `src/ui/src/pages/slo/SloDetailPage.tsx` | modify | Metric-kind formatting (`isThreshold`, labels, filter and baseline display) |
| `src/ui/src/demo/data/slo.ts` | modify | Demo metric SLO fixtures |

### B10: coexistence tests

| Path | Action | Purpose |
|---|---|---|
| `src/tests/Warp.Tests/Warp.Tests.csproj` | modify | `MediatR` **12.5.0** pinned (checkpoint: human-verify the package and licence before install) |

### B11: benchmark, docs and rules

| Path | Action | Purpose |
|---|---|---|
| `src/benchmarks/Warp.Benchmarks/Benchmarks/MetricCollectorBenchmarks.cs` | create | SC10 callback cost |
| `website/docs/features/custom-metrics.md` | create | Feature doc: collector, presets, topology, PCI tag guidance, write-volume table, crash loss |
| `website/docs/features/slo-error-budget.md` | modify | Metric kinds, including the http 5xx and ratio recipes |
| `website/docs/features/adapters.md` | modify | PCI: capture `None`, no `ForceCapture` on payment adapters |
| `website/docs/features/background-services.md` | modify | Service-only server task set |
| `website/docs/dashboard/health/applications.md` | modify | Service-only server task set |
| `website/docs/operations/ef-core-integration.md` | modify | Coexistence caveats |
| `website/docs/operations/configuration.md` | modify | New options |
| `website/docs/releases.md` | modify | 7.5.0 notes, including the `DisableWorker` behaviour change |
| `ROADMAP.md` | modify | Mark custom application metrics shipped, built on `System.Diagnostics.Metrics` |
| `.claude/rules/project-specific.md` | modify | New §8.34 (metrics); §8.31 kinds; §8.18 service-only task set; §8.29 correction (the endpoint occurrence is written by the flusher) |
| `.claude/rules/architecture.md` | modify | §2.3 and §2.13 task sets |
| `CLAUDE.md` | modify | Project-profile line |

---

## Test manifest

| Path | Kind | Covers |
|---|---|---|
| `src/tests/Warp.Tests/Core/ServiceRegistrationTopologyTests.cs` (create) | NoDb | R13 registration shape: AddWarp-only, AddWarp + AddMetrics, worker server, DisableWorker server |
| `src/tests/Warp.Tests/Admin/DeploymentShapeTests.cs` (modify) | NoDb | Flip `:275` so the three moved tasks are present on a service-only server and the job-only tasks still absent; update the stale comments at `:231` and `:264-269`. `MetricRollup` is asserted in B4 via the topology test. |
| `src/tests/Warp.Tests/Metrics/MetricSeriesTests.cs` (create) | NoDb | R4: canonical order, allowlist projection, sanitization, invariant formatting |
| `src/tests/Warp.Tests/Metrics/MetricBucketsTests.cs` (create) | NoDb | R6: precedence, unit defaults, bucket index, overflow |
| `src/tests/Warp.Tests/Metrics/MetricCardinalityTests.cs` (create) | NoDb | R7–R9; fuzz (10,000 random values × 6 tags → distinct series ≤ bound); report-once |
| `src/tests/Warp.Tests/Metrics/WarpMeterCollectorTests.cs` (create) | NoDb | R1–R3, R5; each kind's aggregate; `Warp` meter ignored; N=2 concurrent `BarrierSignal`; **unique meter name per test** (lesson) |
| `src/tests/Warp.Tests/Metrics/MetricAggregatorTests.cs` (create) | NoDb | Drain is lossless under concurrent adds; merge-back is additive |
| `src/tests/Warp.Tests/Metrics/MetricFlusherTestsBase.cs` (create) | PG + SQL Server | R10–R12: interval and StopAsync flush, context vs server sink, merge-back on failure, registry upsert and PK-race tolerance |
| `src/tests/Warp.Tests/Metrics/MetricRollupTestsBase.cs` (create) | PG + SQL Server | R15–R18: compaction exactness, tier passes, cap defers without loss, bounds-mismatch kept apart, idle tick returns null |
| `src/tests/Warp.Tests/Metrics/MetricQueryServiceTestsBase.cs` (create) | PG + SQL Server | R19–R21: tier by range, partial-row summing, breakdown top 10 + rest, filter wildcard, overlays, percentile |
| `src/tests/Warp.Tests/Slo/SloMathTests.cs` (modify) | NoDb | Count threshold (N=0 and N>0), floor, gap, baseline drop |
| `src/tests/Warp.Tests/Slo/SloValidationTests.cs` (modify) | NoDb | Per-kind rules, R29, gauge rejected, minimum window |
| `src/tests/Warp.Tests/Slo/MetricFilterTests.cs` (create) | NoDb | Grammar and wildcard |
| `src/tests/Warp.Tests/Slo/MetricSloEvaluatorTestsBase.cs` (create) | PG + SQL Server | R22–R28 with a recording notifier |
| `src/tests/Warp.Tests/Slo/SloCommandQueryTestsBase.cs` (modify) | PG + SQL Server | New fields round-trip through the `GetObjective`/`List` read-back; seeder match |
| `src/tests/Warp.Tests/Core/MediatRCoexistenceTests.cs` (create) | NoDb | R31 |
| `src/tests/Warp.Tests/Core/HostDbContextCoexistenceTestsBase.cs` (create) | PG + SQL Server | R30; migration differ includes the `warp`-schema tables |
| `src/tests/Warp.Tests/Metrics/CustomMetricsEndToEndTestsBase.cs` (create) | Integration, `HeavyIntegration` | AC1 end to end. An AddWarp-only TestHost has a controller using `IMeterFactory` plus the built-in `http.server.request.duration`. Its data flushes to `metric_point`; a DisableWorker server compacts and rolls it up and fires a metric SLO edge; a seeded `Enqueued` job stays `Enqueued` (R14). Uses the default registration. |
| `src/ui/src/pages/metrics/metricModel.test.ts` (create) | vitest | Ratio, overlay alignment, rest bucket, percentile |
| `src/ui/src/layouts/navModel.test.ts` (modify) | vitest | Metrics nav entry gated on the `metrics` flag |
| `src/ui/src/demo/demoFixtures.test.ts` (modify) | vitest | Demo serves `/api/metrics*` |

Every new NoDb class carries `[Trait("Category", "NoDb")]`, and every gate must report a non-zero executed-test count.

---

## Success criteria

| ID | Criterion | Verification | Observable |
|---|---|---|---|
| SC1 (AC1) | In an `AddWarp`-only process, a controller and a MediatR-style class emit counters and histograms with the standard `Meter` API, with no DB I/O on the measuring thread. The values appear in tiered `metric_point` rows after a DisableWorker server's rollup runs. | `dotnet test --project src/tests/Warp.Tests/Warp.Tests.csproj -- --filter-class "*CustomMetricsEndToEndTests*" --filter-class "*WarpMeterCollectorTests*"` | All pass on PG and SQL Server; the asserted compacted fine and hourly rows have the expected count and sum |
| SC2 (AC2) | Unbounded tag values collapse to `{other}`, are reported once, and series stay bounded under fuzz | `… --filter-class "*MetricCardinalityTests*"` | Distinct series ≤ cap + 1; exactly one Warning per (instrument, tag, reason) |
| SC3 (AC3, without deploy markers) | The Metrics page shows breakdown by tag, 1d and 7d overlays and the ratio view | `cd src/ui && npm test -- metricModel`; `… --filter-class "*MetricQueryServiceTests*"`; browser check via `npm run dev` | Tests pass; the screenshot shows breakdown series, two dashed overlays and the ratio view |
| SC4 (AC4) | Ratio, threshold, volume-floor and gap SLOs evaluate from stored aggregates, alert once per edge, and never on NoData | `… --filter-class "*MetricSloEvaluatorTests*" --filter-class "*SloMathTests*" --filter-class "*SloValidationTests*" --filter-class "*MetricFilterTests*"` | All pass on both providers; the recording notifier sees 1 event per edge and 0 for NoData |
| SC5 (AC5/6, revised) | Controller metrics come from the built-in `http.server.request.duration` through `CollectAspNetCoreHttp()`, keyed by route, status and application, with no request body or header ever touched | The `CustomMetricsEndToEndTests` route assertions | Pass. The stored `Tags` contain only `http.request.method`, `http.response.status_code` and `http.route`. |
| SC6 (AC7) | A host interceptor and MediatR 12.5 behave the same with and without `AddWarp`; a DisableWorker server runs the tasks and claims no jobs | `… --filter-class "*MediatRCoexistenceTests*" --filter-class "*HostDbContextCoexistenceTests*" --filter-class "*ServiceRegistrationTopologyTests*" --filter-class "*DeploymentShapeTests*"` | All pass |
| SC7 | No regression; analyzer-clean | `dotnet build src/Warp.slnx` and `dotnet test --project src/tests/Warp.Tests/Warp.Tests.csproj` | Build has 0 warnings and 0 errors; the full suite is green apart from failures recorded in the Phase 2.9 baseline |
| SC8 (AC8) | Docs exist for each feature, including PII and PCI guidance | `grep -l PCI website/docs/features/custom-metrics.md website/docs/features/adapters.md` | Both listed |
| SC9 | The series read is cheap at the first consumer's scale | Manual measurement recorded in the PR: seed about 500 series × (72 fine + 168 hourly + 90 daily) rows for one histogram instrument on PG, then `EXPLAIN (ANALYZE)` the 24h and 7d series reads | An index scan on `(Instrument, Tier, BucketStart)` and each read < 250 ms; otherwise the conditional escalation fires |
| SC10 | The measurement callback is cheap on the caller's thread | `dotnet run -c Release --project src/benchmarks/Warp.Benchmarks -- --filter "*MetricCollector*"` | A warm-series measurement costs ≤ 1 µs and ≤ 1 allocation (mean); results recorded in the PR |

`--filter-class` is the xUnit v3 MTP class filter. It is not quoted in CLAUDE.md, so it gets probed once at B1. **Concrete fallback if it is rejected:** run each listed class's category. NoDb classes use `--filter-trait "Category=NoDb"`; DB classes use both the `PostgreSql` and `SqlServer` runs. Then confirm from the run output that every listed class executed a non-zero number of tests.

---

## Implementation batches

Batches run **serially** in this worktree. Waves only show which batches are independent.

1. **B1, topology:** the full suite (lesson 2026-08-05: shared semantics break far-away tests).
2. **B2, collector:** NoDb.
3. **B3, storage and flush:** PG and SQL Server.
4. **B4, rollup:** PG and SQL Server.
5. **B5, query and API:** PG and SQL Server, plus SC9.
6. **B6, Metrics UI:** `npm run build && npm test && npm run lint`.
7. **B7, SLO model:** NoDb.
8. **B8, SLO evaluation:** PG and SQL Server.
9. **B9, SLO UI:** `npm run build && npm test && npm run lint`.
10. **B10, coexistence and E2E:** the full suite.
11. **B11, benchmark and docs:** SC10 and SC8.

---

## Constitution Check

| Rule | How the design satisfies it |
|---|---|
| §0.1 / §7.5 (never push) | No push. |
| §0.2 / §2.2 / §6.1 (worker hot path) | No worker fetch, execute or finalize file is in the manifest. `IJobContext` is untouched. |
| §0.3 / §4.4 | No budget increases. The E2E class joins `HeavyIntegration` (§4.7.1). |
| §0.4 / §4.7 | Concurrency is tested with `BarrierSignal` and N=2. The fuzz test is pure cardinality fuzz. |
| §0.5 / §2.4 / §2.11 | The flusher resolves the sink through `IServiceScopeFactory`. Seams are internal across Core→Worker (non-addon `InternalsVisibleTo`). No `IServiceProvider` injection. |
| §2.3 / §2.16 | Rollup is an `IServerTask` under the transaction-scoped lock. Only registration blocks move. |
| §2.14 | Server-side flush, rollup and evaluation run on `IWarpServerContext`. The `AddWarp`-only flush is user-side `TContext`. |
| §5.1 / §5.2 / §5.3 / §5.7 | EF LINQ only (`GroupBy`, `ExecuteDeleteAsync`), `AsNoTracking` projections, no subquery-in-projection, `TimeProvider`. |
| §6.2.1 | Rollup is bounded per tick, defers rather than drops, and returns null when idle. |
| §8.11 | `MetricKind` and `MetricTier` start at 1; `SloKind` is appended at 6–9. |
| §8.13 / §2.11 | Both entities are in `Warp.Core.Data.Entities` and always in the schema. |
| §8.25 | Metric SLO alerts use the existing post-commit `OnCommittedAsync` buffer. |
| §1.2 | Tag values are never logged; collection is opt-in; allowlists. |
| §6.2 (Counter rows, never Statistic, from hot paths) | Not applicable to the new path. The **deliberate departure** is a separate typed store, per the engineer's 2026-11-13 decision; see Rejected alternatives. `Counter`/`Statistic` are untouched. |
| User rule: pin exact NuGet versions | MediatR is pinned at 12.5.0 in `Warp.Tests` only. |

---

## INVEST+C score

INVEST is a soft check:
- **Independent:** yes.
- **Negotiable:** yes.
- **Valuable:** yes.
- **Estimable:** yes.
- **Small:** **no**. It is 11 batches in one PR, the engineer's explicit choice, and they run serially, each green on its own.
- **Testable:** yes.

+C checks: **15/16**

| # | Result | Reason |
|---|---|---|
| 1 | ✓ | Concrete paths |
| 2 | ✓ | Patterns named in the plan |
| 3 | ✓ | Each SC has a runnable command |
| 4 | ✓ | Out of scope lists 8 items |
| 5 | ✓ | `pii-exposure` with justification |
| 6 | ✓ | MediatR 12.5.0 is the only new dependency, test-only, behind a checkpoint |
| 7 | ✓ | Tables and columns named |
| 8 | ✓ | |
| 9 | ✗ | Crosses Core, Worker, Dashboard and UI. Justified: one feature spans collect, store, roll up, read and alert, and each surface stays inside its package boundary. |
| 10 | ✓ | |
| 11 | ✓ | |
| 12 | ✓ | Lessons applied: unique meter per test, FindEntityType guard, full-suite checkpoint, default-registration E2E |
| 13 | ✓ | Prior work checked |
| 14 | ✓ | |
| 15 | ✓ | Rollback below |
| 16 | ✓ | |

---

## Risks and assumptions

**Risks**

- **R-A, read cost** (SC9).
  - Readers group by (`BucketStart`, `Tags`) in SQL, which is bounded by series × buckets in range: about 500 × 72 for a 6h fine read.
  - *Conditional escalation:* if SC9 fails, stop and re-open the gate (options: per-tag columns, or a pre-aggregated per-instrument total row). Never redesign silently.
  - **Fired 2026-11-15.** First measurement:
    - 24h: 154 ms;
    - 7d: **372 ms**;
    - SQL only 8.5 ms / 31.7 ms; the rest was in-memory processing of about 120k rows.
    - "No index used" was an artifact of a single-instrument seed with overlapping tiers.
    
    **Engineer decision:** optimise within the approved design and fix the measurement.
    - Parse histogram bucket CSVs only when a percentile is requested.
    - With no breakdown and no filter, total in SQL by bucket.
    - Seed several instruments with non-overlapping tiers.
    - The 250 ms budget stays for the common reads: 24h and 7d without a percentile or breakdown.
    - The percentile-plus-breakdown 7d read at the series cap is measured and documented as the worst case, not gated.
- **R-B, behaviour change for `DisableWorker()` servers.** They start running three moved tasks plus `MetricRollup`, all cluster-locked. Release notes; no data change.
- **R-C, write volume.** Each process inserts one row per active series per flush, so at a 30s interval that is up to 10 partial rows per series per 5 minutes per process. Compaction one minute after bucket close keeps steady state at one row per series per bucket.
  - Estimate for a mid-size host: about 500 active series × 10 processes. That is 5,000 short insert batches per 30s cluster-wide at worst, and far less in practice because most series are idle in any 30s window.
  - A docs table shows the arithmetic and recommends raising `MetricFlushInterval` on emit-only processes.
- **R-D, baseline resolution.** Baselines older than 6 hours are hour-resolution and prorated. Documented and labelled.
- **R-E, PII in tags** is not technically detectable. Mitigated by opt-in, allowlists, truncation and caps. Stated plainly.
- **R-F, built-in instrument names and tags** follow OTel semantic conventions and could change in a future ASP.NET Core release. Presets are config strings, overridable by `CollectMeter(...).Instrument(..., KeepTags(...))`. The E2E test pins today's names.
- **R-G, a second storage path** next to `Counter → Statistic`. Accepted by engineer decision. The rollup mirrors `StatisticRollup`'s contract and test style.
- **dirty-worktree:** the only untracked files are this run's own spec, plan and todo artifacts.

**Assumptions**

- [VERIFIED:C:/Program Files/dotnet/packs/Microsoft.NETCore.App.Ref/10.0.12/ref/net10.0/System.Diagnostics.DiagnosticSource.xml] `MeterListener`, `InstrumentAdvice<T>.HistogramBucketBoundaries`, `Instrument<T>.Advice`, `Gauge<T>` and the observable instruments exist on net10.0.
- [VERIFIED:src/core/Warp.Core/Warp.Core.csproj] `Warp.Core` grants `InternalsVisibleTo` to `Warp.Worker`.
- [VERIFIED:src/tests/Warp.Tests/Admin/DeploymentShapeTests.cs] `:275` asserts the old service-only task set.
- [ASSUMED] ASP.NET Core 10's `http.server.request.duration` carries `http.route`, `http.request.method` and `http.response.status_code`, and `System.Net.Http` emits `http.client.request.duration` with `server.address`. B10's E2E test asserts this on the real stack before docs are written.
- [ASSUMED] MediatR 12.5.0 is Apache-2.0 (the commercial licence starts at v13). It is test-only and behind a checkpoint.

**Rollback:** revert the PR. The migration is additive (new tables and nullable columns), so old binaries ignore it. A host that already generated the migration can leave it in place.

---

## Rejected alternatives

- **Warp-specific `IWarpMetrics` emit API** (the 2026-11-12 draft). It locks host code to Warp and misses the built-in ASP.NET Core and HttpClient metrics. Replaced by the standard API.
- **Riding `Counter → Statistic`.** Values are integer-only (durations in seconds are fractional), each histogram series costs about 14 key-value rows per bucket, and time-range reads mean prefix scans. Replaced by the typed `metric_point` store.
- **Writing compacted rows directly with upserts from every process.** Cross-process contention on the same series-bucket row. Replaced by partial inserts plus server-side compaction.
- **Warp middleware for controllers.** Superseded by the built-in meter (engineer decision); Issues for host 5xx deferred.
- **An OTel SDK dependency** (`OpenTelemetry.Exporter.*`). Unnecessary: `MeterListener` is in the BCL, and a host's OTel SDK keeps working in parallel.

## Prior Work Check

- No `MeterListener`, metric-point store or instrument registry exists in `src/core` (grep, 2026-11-13). `WarpTelemetry` only **emits** on its own `Warp` meter, and the collector excludes it.
- Reused patterns, not code: `ClientEventCardinality` (guard shape), `WarpCounterBuffer` (lossless `TryRemove` drain), `StatisticRollup` (bounded deferring rollup), `DroppedRecordReporter` (per-process idle-free reporter).
- **Verdict: CLEAR.**

## Out of scope

- **Deploy markers and the post-deploy verdict endpoint** (brief item 4). Dropped by engineer decision, along with CI authentication.
- **Issues for host-endpoint 5xx; any Warp controller middleware.** Built-in meter only, by engineer decision.
- Changes to the existing `Warp.Http` endpoint observability, or to the `Counter`/`Statistic` pipeline.
- A latency-percentile SLO kind on custom histograms. Follow-up; percentiles are shown on the Metrics page.
- Grafana-style dashboards, a query language, or learned or anomaly baselines.
- Changing the `IModelCustomizer` replacement or the `AddDbContext` ordering behaviour (documented only).
- Re-pinning the benchmark project's `MediatR 12.*`.
- Replacing the host's existing job runner (Hangfire or similar).

## Open questions

None. Every design fork was decided at the ambiguity gate on 2026-11-12 and 2026-11-13.
