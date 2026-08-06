---
sidebar_position: 16
---

# Pluggable metric source (read metrics back from Prometheus)

Warp's dashboard and SLO evaluator read their metrics — job execution stats, queue-wait, backlog, deadline attainment, adapters, endpoints, client vitals, error-group trends — through a single seam, `IMetricSource`. By default that seam reads Warp's own durable `Statistic`/`Counter` fold (the local backend). You can instead point it at **Prometheus**, so Warp renders every page from the same metrics it *exported* via OpenTelemetry — keeping the operational database lean while every view stays in the Warp UI.

The data is always Warp's own, known-shape metrics. Warp **generates the queries itself** from a fixed catalog — there is no graph editor and no user-facing query language. This is deliberately **not** Grafana-style federation of arbitrary series.

## How it works

Every metric read Warp performs reduces to a small, backend-neutral set of operations on `IMetricSource`: a total, a bucketed series, a grouped breakdown, a percentile, a grouped percentile, a gauge, and a tag enumeration. A read is expressed against a **logical** `MetricRef` — a name from `WarpMetricCatalog` plus tags (`adapter`, `route`, `queue`, `type`, `outcome`, …), never a storage key.

Each backend owns its own translation *from* that logical reference — there is no shared mapping table:

- **`LocalMetricSource`** (default) maps a `MetricRef` to the colon-delimited `Statistic`/`Counter` keys and reproduces the merged-read + tier down-bin semantics exactly. Routing a reader through the seam moves no numbers.
- **`PrometheusMetricSource`** maps the same `MetricRef` to an OTel metric name + label matchers and generates the PromQL: `sum by (…)` for breakdowns, `histogram_quantile(…)` for percentiles, `count by (…)` for tag enumeration, and `increase(…)` range queries for series. It reads through a Refit `IPrometheusQueryApi`.

Because the logical catalog mirrors the OTel meter model, the same abstract read serves both stores.

## Enabling the Prometheus backend

The Prometheus backend ships in `Moberg.Warp.Metrics.Prometheus`. Call `AddPrometheusMetricSource` **after** `AddWarpServer` — it replaces the default local registration:

```csharp
using Warp.Metrics.Prometheus;

builder.Services.AddWarpServer<AppDbContext>(opt => opt.UsePostgreSql());

builder.Services.AddPrometheusMetricSource(o =>
{
    o.BaseAddress = "http://prometheus:9090";  // the Prometheus HTTP API
    o.DefaultLookback = TimeSpan.FromDays(7);  // bounds open-ended "all history" reads
});
```

That is all the dashboard needs. The reads that previously hit the database now issue PromQL; nothing else changes.

## Prerequisites on the export side

Reading a family back from Prometheus requires that Warp actually *exported* it, with the right dimensions and bucket boundaries. Two things matter:

### 1. Histogram bucket Views (required for latency)

Warp's latency histograms (`warp.job.execution.duration`, `warp.job.queue.wait`, `warp.adapter.duration`, `warp.endpoint.duration`, `warp.client.vitals`) must be exported with bucket boundaries that match Warp's internal ladders — otherwise `histogram_quantile` saturates at the OpenTelemetry default (~10 s) and can't observe a 30 s/60 s latency SLO. Configure the Views on your `MeterProvider`:

```csharp
metrics
    .AddMeter("Warp")
    .AddWarpHistogramViews();   // job-scale to 5 min, HTTP-scale to 10 s
```

The demo wires this in its service defaults; a library consumer adds it where they configure OpenTelemetry metrics. The boundaries are the DB `:pct(h):` ladders with the implicit `+Inf` overflow rung omitted: job-scale (execution + queue-wait) runs to `300000` ms, HTTP-scale (adapters / endpoints / client vitals) to `10000` ms.

### 2. Export coverage of each family

Most families already emit their key dimensions as OTel attributes, so they read back with no extra work: **jobstat** (succeeded/failed + latency by type/handler/outcome), **queue-wait**, **backlog** (depth + oldest-age gauges), **adapters** (count/duration/outcome), **endpoints**, and per-type **client events + vitals**.

Four families needed additional export, now emitted:

- **`warp.job.deadline.total`** — the attainment denominator, so `1 − miss ÷ total` is computable from the meters alone.
- **`warp.client.events.named`** — the top-N error-type / event-name / log-level breakdown. Because the name is browser-controlled on a public endpoint, it is tallied only *after* the cardinality guard on the recording path bounds it (emitting the raw name at ingest would be an unbounded-cardinality vector). An OTel-only deployment therefore gets the name breakdown only when recording is enabled.
- **`warp.errorgroup.occurrences`** — the per-fingerprint occurrence trend (fingerprint already collapsed by the distinct-group cap).
- **SLO objective gauges** — `warp.slo.attainment` / `warp.slo.budget` are not yet emitted; the SLO page still reads its status from the durable `SloEvaluation` rows.

The last two families (`client.events.named` by name, `errorgroup.occurrences` by fingerprint) are **higher-cardinality** by design — each is a separate instrument a deployment can drop if the label cost isn't wanted.

### 3. Families the Prometheus backend serves

The Prometheus backend reads back every family that has a clean single-instrument OTel export: **adapters**, **endpoints**, **job execution** (count + duration), **queue-wait** (histogram + count), **queue backlog** (depth + oldest-age gauges), **deadline** attainment, **client events + vitals**, and the **error-group** trend. The job-execution and deadline instruments export `job.type` / `job.handler` labels (the OTel attribute names), so the backend translates the seam's logical `type` / `handler` tags to those labels transparently.

Three things stay **local-only** and the Prometheus backend refuses them with a clear error rather than reading a wrong or empty series:

- the **dashboard summary tiles** (`lifecycle.deleted` has no meter — deletions aren't a job-execution outcome),
- **dropped-record counts** (the pipeline selects the instrument *name*, `warp.{adapter|endpoint|client}.records_dropped`, not a label), and
- the **SLO objective gauges** (not exported — see above).

A deployment that points the seam at Prometheus keeps the local backend available for these; the per-surface pages (adapters, endpoints, jobs, queues, clients, issues) and SLO evaluation all read from Prometheus.

## What stays on the database

Raw per-call detail — the recent-calls lists, last-failure timestamps, session and trace joins, and the drill-down request/response bodies — are **not** metrics and always read from the retained log rows (or the OTel spans). Only the aggregate metric reads flow through `IMetricSource`. All metric reads are off the worker fetch/execute hot path.
