---
sidebar_position: 15
---

# Observability sinks & OpenTelemetry

Warp collects two kinds of observability data for adapters, endpoints, and jobs:

- **Per-call records** — one row per adapter call / endpoint request, with the captured request/response headers + bodies (redacted, truncated).
- **Aggregate metrics** — counts, durations, error rates, sliced by adapter/operation/route, application, and job type/handler.

By default both land in **your database** (call-log rows + `Counter`→`Statistic` aggregates) so the built-in Warp dashboard can render them. At high throughput that database write volume is the expensive part. Warp lets you route the whole firehose to **OpenTelemetry** instead — spans, structured logs, and meters to an OTLP collector — and keep your database out of the hot path.

## The two tracks

Warp **always emits OpenTelemetry** — spans and meter instruments fire unconditionally through `WarpTelemetry`'s `ActivitySource` and `Meter` (both named `WarpTelemetry.ServiceName` = `"Warp"`), using the null-listener pattern: **zero cost when nothing is listening**. The database recording is a *separate, opt-in* track. So "send observability to OTel" is mostly a matter of (1) wiring an OTLP exporter and (2) choosing where the recorded detail goes.

## Choosing a sink

Each surface takes a `RecordingSink` (`Database` = 1, `Otel` = 2, `Both` = 3; default `Database` → today's behavior byte-for-byte):

```csharp
builder.Services.AddWarpServer<AppDbContext>(opt =>
{
    opt.UsePostgreSql();

    // Per-call records → OTel structured logs instead of DB rows (no channel/flusher/call-log table):
    opt.AddAdapters(o => o.Sink = RecordingSink.Otel);
    opt.AddEndpointObservability(o => o.Sink = RecordingSink.Both);   // DB *and* OTel

    // Job type/handler execution metrics → skip the DB Counter writes, rely on OTel meters:
    opt.JobMetricsSink = RecordingSink.Otel;
});
```

| Sink | Per-call records | DB aggregate `Counter` writes | Dashboard |
|---|---|---|---|
| `Database` (default) | `AdapterCallLog` / `EndpointCallLog` rows | written | Warp dashboard works |
| `Otel` | one structured OTLP **log** per call | **skipped** | use Grafana/Jaeger (or a backend-backed query service) |
| `Both` | rows **and** OTel logs | written | Warp dashboard works + OTel |

`JobMetricsSink` (on `WarpConfiguration`) is the equivalent switch for the per-job-type/handler execution metrics: `Otel` skips their `jobstat` `Counter` writes on the finalization path (the perf win), because the OTel meters carry the same data.

> The OTel **meters always emit** regardless of sink (they're free without a listener). The sink only controls the DB writes. So `RecordingSink.Otel` = "stop writing to my database; I'll read this from my collector."

## Wiring the exporter

Register Warp's source + meter in your OpenTelemetry pipeline and add an OTLP exporter (the demo's `AddServiceDefaults` already does this):

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(WarpTelemetry.ServiceName).AddOtlpExporter())
    .WithMetrics(m => m.AddMeter(WarpTelemetry.ServiceName).AddOtlpExporter())
    // structured call records are emitted as ILogger log records:
    .WithLogging(l => l.AddOtlpExporter());
```

## What OTel receives

**Structured logs** (one record per completed call, under the Otel/Both sink) — categories `Warp.Adapters.CallLog` and `Warp.Endpoints.CallLog`. Every field of the record is a log property (adapter/operation/group/outcome/status/duration/request+response headers+bodies/correlation/tags/application; endpoints add method/route/remoteIp/userAgent/user). Fields are already redacted + truncated by the capture pipeline — the same PII stance as the DB path (§1.2). Level is `Information` for success, `Warning` for failures.

**Meters** (always on):

| Instrument | Kind | Key tags |
|---|---|---|
| `warp.adapter.calls` / `warp.adapter.duration` | Counter / Histogram | `adapter`, `operation`, `outcome`, `application` |
| `warp.endpoint.calls` / `warp.endpoint.duration` | Counter / Histogram | `route`, `outcome`, `application` |
| `warp.job.execution.total` / `warp.job.execution.duration` | Counter / Histogram | `job.type`, `job.handler`, `outcome`, `application` (executor) |

All tags are bounded, low-cardinality identifiers. The unbounded caller **group** stays off meter tags (it's a DB/log dimension only), so metric cardinality is safe. `application` is present only when `WarpConfiguration.ApplicationName` is set. Percentiles (p95/p99) come from the Histogram in your backend.

**Spans** — adapter calls emit a `Client`-kind Activity; jobs/producers/receivers emit their activities; all carry `warp.application` when set. Correlate across services in Tempo/Jaeger via the shared trace id.

## Dashboards

- **`Database` / `Both`** — the built-in Warp dashboard renders Adapters / Endpoints / Jobs-by-Type metrics from the DB as usual.
- **`Otel`-only** — those pages have no DB rows to read. Use your telemetry backend's UI (Grafana over Prometheus for the meters, Loki/OTLP-logs for the call records, Tempo/Jaeger for traces). The Warp dashboard's **control** surfaces (jobs list, retry/requeue, pause, sagas, webhooks, the Applications roster) are unaffected — they're operational state, not telemetry, and stay in the database.
- Advanced: because the dashboard reads through public query-service interfaces (`IAdapterQueryService`, `IEndpointQueryService`, …, registered via `TryAdd`), you can register backend-backed implementations (e.g. Prometheus-querying) *before* `AddWarp` and keep the built-in pages while sourcing them from OTel.

## Why it's faster

The database path per call is a bounded-channel write → a flusher DB round-trip (a row per call) → `Counter` inserts → the `CounterAggregator` fold → cleanup sweeps → storage growth; the channel is lossy precisely because the DB can't always keep up. The OTel path is a cheap in-process log/meter record + **background, batched OTLP export off the hot path**, with the collector doing aggregation and storage **out of process**. For hot adapters/endpoints or high job throughput, `RecordingSink.Otel` takes the diagnostics firehose off your database entirely.

## Guidance

- Start `Database` (default) — nothing to configure, dashboard works.
- Move a hot surface to `Both` first (dashboard + collector in parallel), confirm your backend has what you need, then to `Otel` to drop the DB load.
- Keep the low-volume **control-plane** in the database (it's not telemetry). Only the high-volume per-call records + derived metrics belong on the OTel track.
