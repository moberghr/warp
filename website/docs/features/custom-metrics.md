---
sidebar_position: 20
---

# Custom application metrics

Warp can collect the metrics **your own code already emits** with the standard .NET `System.Diagnostics.Metrics` API, store them in your operational database, show them on a **Metrics** page, and hold them to an [SLO](./slo-error-budget.md). There is no Warp metrics API to learn and no Warp dependency in the code that emits: you create a `Meter`, a `Counter<T>` or a `Histogram<T>` the way the .NET docs show, and Warp is only the **collector** that listens to the meters you name.

It is an **opt-in** feature: nothing is collected until `opt.AddMetrics(...)` names a meter. The `metric_point` and `metric_instrument` tables are always in the schema, so a dashboard-only process can read metrics another process wrote.

It is **not a time-series database and not Grafana**: no query language, no custom dashboards, no alert rules beyond the [metric SLO kinds](./slo-error-budget.md#metric-objectives). A host's OpenTelemetry exporter keeps working in parallel (see [Coexistence](#coexistence)). Use that for power-user dashboards.

## Quick start

Register the collector inside the `AddWarp` (or `AddWarpServer`) lambda:

```csharp
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connStr));

builder.Services.AddWarp<AppDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.ApplicationName = "Shop.Api";       // optional: becomes a dimension of every series

    opt.AddMetrics(m => m
        .CollectMeter("Shop.Payments")      // every instrument of this meter, every tag (capped)
        .CollectAspNetCoreHttp()            // http.server.request.duration, per route and status
        .CollectHttpClient());              // http.client.request.duration, per host and status
});
```

The emitting code is ordinary .NET and takes no Warp dependency:

```csharp
public sealed class PaymentMetrics
{
    private readonly Counter<long> _results;
    private readonly Histogram<double> _latency;

    public PaymentMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create("Shop.Payments");
        _results = meter.CreateCounter<long>("payment.results");
        _latency = meter.CreateHistogram<double>("payment.latency", unit: "ms");
    }

    public void Recorded(string step, string gateway, string result, double elapsedMs)
    {
        _results.Add(1,
            new("step", step),          // authorize | capture | refund
            new("gateway", gateway),    // a small, fixed set
            new("result", result));     // approved | refused | error

        _latency.Record(elapsedMs, new KeyValuePair<string, object?>("step", step));
    }
}
```

Open the dashboard's **Metrics** page: `payment.results` and `payment.latency` appear within about one flush interval (30 seconds by default).

## What gets collected

- **Only meters you name.** `CollectMeter("name")` matches the meter name exactly (ordinal). Warp enables measurement events only for the instruments of collected meters, so a process that names none pays nothing and starts no listener.
- **Warp's own `Warp` meter is never collected**, even if you name it. Its data is already persisted through its own pipelines, and collecting it would double-count.
- **No I/O on the measuring thread.** The collector is a `MeterListener`. Its callback projects the tags, looks up the series and adds to an in-memory aggregate, then returns. It never opens a database connection, and it never throws into your code: a callback failure is counted on `warp.metrics.collapsed{reason=error}`, logged once per instrument without values, and the measurement is dropped.
- **Cost.** Measured with BenchmarkDotNet (`ShortRun`, .NET 10.0.12) for an already-seen series with three tags through the real collector path: **`Counter<long>.Add` about 253 ns and `Histogram<double>.Record` about 249 ns per measurement, with no managed allocation.** The first measurement of a new series allocates it once. Reproduce with `dotnet run -c Release --project src/benchmarks/Warp.Benchmarks -- --filter "*MetricCollector*"`.

### Choosing instruments and tags

```csharp
opt.AddMetrics(m =>
{
    // All instruments of a meter, all tags (capped at 6 per instrument).
    m.CollectMeter("Shop.Payments");

    // Only some instruments, and only some tags of each.
    m.CollectMeter("Shop.Webhooks", meter => meter
        .Instrument("notifications.received", i => i.KeepTags("type", "event_code", "outcome")));

    // Presets: configuration only, Warp takes no ASP.NET Core dependency.
    m.CollectAspNetCoreHttp();   // keeps http.route, http.request.method, http.response.status_code
    m.CollectHttpClient();       // keeps http.request.method, server.address, http.response.status_code, error.type

    // Per-instrument settings that apply whichever collected meter publishes it.
    m.Instrument("payment.latency", i => i.Buckets(5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000));
});
```

- A `CollectMeter(name, meter => ...)` selector that lists instruments collects **only those instruments**. A selector that lists none collects the whole meter.
- `Instrument(name, ...)` configures an instrument by name without enabling a meter. Settings made inside a meter's own selector win over the global ones, field by field.
- `KeepTags(...)` is an **allowlist**. Every other tag is dropped before the series is formed. Without it, every tag is kept, up to `MaxTagsPerInstrument`.
- Calling `AddMetrics` more than once merges the configurations.

### Histogram buckets

Bucket bounds come from, in order of precedence: **`Buckets(...)` in configuration, then the instrument's own advice** (`InstrumentAdvice<T>.HistogramBucketBoundaries`, which ASP.NET Core and `HttpClient` set), **then a default chosen by the instrument's unit**: `s` gets the OTel seconds ladder (0.005 to 10), `ms` the millisecond ladder (5 to 10000), anything else the OTel default explicit ladder.

Bounds must be 1 to 20 finite values in strictly increasing order; the overflow bucket is implicit. Each bound is stored rounded to 7 significant digits, and the rounded ladder must still be strictly increasing and fit **256 characters** as CSV. `Buckets(...)` **throws** if it does not (it never truncates, which would misalign the counts). Advice that cannot be stored this way is not an error: the unit default is used instead and the instrument is reported as collapsed with reason `bounds`.

Rows whose bounds differ are never merged, so changing an instrument's buckets starts a new ladder rather than corrupting the old one.

## Instrument kinds and what is stored

Every measurement is converted to `double` and added to an in-memory aggregate for its **series** and its **5-minute bucket**. A series is the instrument name, the application, the tag set and, only on a host that opted in with `ByVersion()`, the application version.

| Instrument | Stored kind | Per bucket | "Value" on charts and SLOs |
|---|---|---|---|
| `Counter<T>` | Counter | count of measurements, sum | sum |
| `ObservableCounter<T>` | Counter | per-series **delta** from the last cumulative value this process saw (the first observation is a baseline only; a total that went down is a reset and the new total is the delta) | sum |
| `UpDownCounter<T>`, `ObservableUpDownCounter<T>` | UpDownCounter | sum of deltas | **net change** in the bucket (sum of deltas), not the level |
| `Histogram<T>` | Histogram | count, sum, min, max, bucket counts | count (rates); percentiles from the buckets; a [`MetricLatency` SLO](./slo-error-budget.md#metric-objectives) reads the bucket counts |
| `Gauge<T>`, `ObservableGauge<T>` | Gauge | count, sum, min, max | average (sum / count), min, max |

Observable instruments are read once per flush, just before the aggregates are written out. Non-finite values (`NaN`, infinities) are ignored.

:::caution Up-down counters chart the net change, not the level
An `UpDownCounter` or `ObservableUpDownCounter` is stored as deltas, so each bucket on the chart (and the value an SLO reads) is the **net change during that bucket**, not the current level. For an observable up-down counter the first observed total is only a baseline, so the level cannot be rebuilt from what is stored. Instrumenting "in-flight requests" with an up-down counter therefore draws a line hovering around 0, and the Metrics page labels the axis **Net change** for this kind. **To chart a level** (in-flight requests, queue length, pool size), publish it with an `ObservableGauge<T>`, which is charted as the average of the observed values per bucket.
:::

**Series identity.** `application` is the sanitized `ApplicationName` (or none). It is added automatically as a dimension and is never read from a tag. `version` is the sanitized `ApplicationVersion`, added the same way **only** on a host that called `ByVersion()` (see [Comparing versions](#comparing-versions)); everywhere else it is none, and the series is exactly what it was before. The tag set is `k1=v1,k2=v2`, sorted by key (ordinal) after the allowlist. Values are formatted with the invariant culture, trimmed and **truncated to 64 characters**; a null or empty value becomes `{none}`; `,` and `=` become `-` and control characters are removed. A tag set that would exceed 512 characters loses its trailing keys. If two meters publish an instrument with the same name, the first one wins and a later one that differs in meter, kind or bounds is not collected (reason `name_conflict`). Instrument names longer than 128 characters are refused (`name_too_long`).

## Topology

```
any AddWarp process with AddMetrics(...)             any AddWarpServer (worker on OR DisableWorker())
────────────────────────────────────────             ───────────────────────────────────────────────
your code: meter.CreateCounter(...)                  MetricRollup          compact, fine → hourly → daily
ASP.NET Core / HttpClient built-in meters            SloEvaluator          metric SLOs + the existing kinds
        │ measurement callback (caller's thread)     CounterAggregator
        ▼                                            ErrorGroupAggregator
WarpMeterCollector → in-memory aggregates            StatisticRollup
        │ every MetricFlushInterval (30s) + on shutdown
        ▼
MetricFlusher ──► metric_point (partial fine rows) ──► MetricRollup
              └─► metric_instrument (registry)
```

- **Every `AddWarp` process collects and flushes.** The aggregates are per process, so each process writes its own: the flusher is a per-process `BackgroundService`, not a cluster-locked task. A process with `AddWarp` only writes on your `DbContext`; a process that runs `AddWarpServer` writes on Warp's server context.
- **Any server runs the folds, including a `DisableWorker()` server.** `MetricRollup`, `CounterAggregator`, `ErrorGroupAggregator`, `SloEvaluator` and `StatisticRollup` run on every server, with or without a worker. All of them are cluster-locked, so a cluster of several servers still runs each tick once. A `DisableWorker()` server never claims a job. See [Service-only deployment](./background-services.md#service-only-deployment).
- The **job worker hot path is untouched**: nothing in the collector runs inside job fetch or execution.

:::note A new behaviour for service-only servers
`CounterAggregator`, `ErrorGroupAggregator` and `SloEvaluator` used to run only on servers with a worker. In 7.5.0 they run on every server, so a `DisableWorker()` server now runs them too.
:::

### Alongside Hangfire or Jobly

Warp does not have to run your jobs to give you metrics. Keep your job system and add Warp for metrics, SLOs and dashboards:

```csharp
// Every application process (APIs, Hangfire/Jobly workers, ...): collect and flush.
services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connStr));
services.AddWarp<AppDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.ApplicationName = "Shop.Api";
    opt.AddMetrics(m => m.CollectMeter("Shop.Payments").CollectAspNetCoreHttp());
});

// One service-only Warp server (two for redundancy): rolls up, evaluates SLOs, claims no job.
services.AddWarpServer<AppDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.DisableWorker();
    opt.AddSlo(o => o.AddMetricThreshold(
        "Adyen callbacks 5xx", "http.server.request.duration", maxEvents: 0,
        tagFilter: "http.route=/api/adyen/*,http.response.status_code=5*"));
});
```

The application processes need only `AddWarp`; the service-only server is the one place the folds run. An end-to-end test covers this shape: a `DisableWorker()` server compacts and rolls up the metrics an `AddWarp`-only host flushed, fires a metric SLO edge, and leaves a seeded `Enqueued` job `Enqueued`.

## Storage

Two tables in the Warp schema, picked up by your normal `dotnet ef migrations add` (additive: nothing existing changes).

- **`metric_point`**: one row per (instrument, application, version, tag set, tier, bucket start), holding count, sum, min, max, and for histograms the bucket counts and bounds as CSV text. Indexed on `(Instrument, Tier, BucketStart)` for reads.
- **`metric_instrument`**: a registry (name, meter, kind, unit, description, bounds, observed tag keys, first and last seen, and the collapse badge). The list page reads only this table and never scans points. Rows whose `LastSeenAt` is older than `DailyStatisticsRetention` are removed by `ExpirationCleanup` (never when it is `null`).

**Flush.** Every `MetricFlushInterval` each process drains its in-memory aggregates and inserts one **partial** fine row per active series. A flush also runs once on graceful shutdown. An idle process does no database work. The registry is upserted in a **separate** save: a registry failure never fails or re-adds the points. A failed point write merges the aggregates back so the next flush writes them (additive: nothing is lost or counted twice).

### Comparing versions

During a rolling deploy two versions of an application emit at once and their measurements merge into one series. To tell them apart, opt a host into the version dimension:

```csharp
builder.Services.AddWarp<AppDbContext>(opt =>
{
    opt.ApplicationName = "checkout-api";
    opt.ApplicationVersion = typeof(Program).Assembly.GetName().Version?.ToString();
    opt.AddMetrics(m => m.CollectAspNetCoreHttp().ByVersion());
});
```

- **Opt-in per host, default off.** Without `ByVersion()` the `version` column stays null and every row is written exactly as before. It needs `ApplicationName` and `ApplicationVersion` too; without either it does nothing. Hosts can differ: a host that did not opt in writes version-less rows beside a host that did.
- **The version is a series dimension, like the application.** It is sanitized like a tag value (trimmed, `,` and `=` to `-`, at most 64 characters), it is part of the series key that compaction and the rollup tiers match on, so two versions never merge into one stored row, and a read without a version filter or breakdown sums every version, so totals are unchanged.
- **Reading it.** The version is addressed as the reserved pseudo-tag `{version}`: `breakdown={version}` draws one line per version, and `filter={version}:2.4.*` keeps matching versions (exact or `prefix*`, like any filter). Rows without a version read as `{none}`. On the Metrics page, **application version** is listed in the breakdown and filter pickers.
- **Cost.** Each version running at once adds its own set of series, so a rolling deploy briefly doubles them. Old versions stop being written when their instances stop and age out with the retention tiers, so the steady state is one or two versions per application. The cardinality caps are per process, and a process only ever has one version, so they are unaffected.
- **Migration.** One nullable column, `metric_point.version`, picked up by your next `dotnet ef migrations add`.

**Compaction.** Once a fine bucket has been closed for longer than twice the flush interval (`bucket start + 5 min + 2 × MetricFlushInterval`), `MetricRollup` merges its partial rows into **one compacted row per series** (including its version, when there is one): count, sum and bucket counts summed, min of mins, max of maxes. This is what turns the 10 partial rows per process per 5 minutes into one row per series per bucket.

**Tiers.** Then the existing [retention tiers](./metrics-retention.md) apply to metric rows too, on the same settings:

| Tier | Resolution | Kept | Setting |
|---|---|---|---|
| fine | 5 min (`FineResolutionMinutes`) | 6 h | `FineResolutionRetention` |
| hourly | 1 h | 7 d | `HourlyStatisticsRetention` |
| daily | 1 day | 90 d (`null` keeps forever) | `DailyStatisticsRetention` |

Each pass **adds** the source rows into the parent row and then deletes them, inside one transaction, bounded to 20,000 source rows per tick. A tick that reaches the cap leaves the rest for the next one (it defers, it never drops), and an idle tick does nothing. `MetricRollupInterval = null` disables the task, and metric rows then accumulate.

**Readers sum on read.** The newest fine rows are still partial (several rows per series) until compaction, so every read sums by key. Compaction is an optimisation, never a correctness requirement, and a measurement shows up on the Metrics page within about one flush interval, long before it is compacted.

### Write volume

Each process inserts **one row per active series per flush**, so the worst case is *series × processes ÷ flush interval* (every series active in every process in every window; an idle series costs nothing):

| Active series | Processes | `MetricFlushInterval` | Partial rows per second (worst case) | Before compaction, per day |
|---|---|---|---|---|
| 100 | 2 | 30 s | 6.7 | about 0.58 M |
| 500 | 10 | 30 s | 167 | about 14.4 M |
| 500 | 10 | 60 s | 83 | about 7.2 M |
| 500 | 10 | 5 min | 17 | about 1.4 M |

Compaction keeps the steady state far smaller: **about 330 rows per series** are retained at any time (72 fine + 168 hourly + 90 daily), so 500 series of one instrument hold roughly 165,000 rows. **Raise `MetricFlushInterval` on emit-only processes** (a busy API that no operator watches in real time) to cut the insert rate; the price is a larger loss window on an ungraceful exit and slower freshness.

## Reading metrics

`IMetricQueryService` (registered by `AddWarp`, so any dashboard host resolves it) has two calls: `ListInstruments()` (the registry only) and `GetSeries(name, query)`.

- **Display resolution** follows the range: up to 6 hours is fine (5 min), up to 7 days hourly, longer daily. A range is at most 90 days.
- **Every tier that overlaps the range is read** and re-binned into the display resolution, because fine rows only roll to hourly after 6 hours and hourly rows to daily after 7 days: a single-tier read would miss the newest data. A value lives in exactly one tier at any moment (a rollup deletes each source in the transaction that adds it to its parent), so nothing is counted twice. A row coarser than the display bucket keeps its own bucket, and **each series is labelled with the coarsest resolution it contains** ("Current: Fine", "7d: Hourly").
- **Breakdown by one tag** returns the **top 10 values by volume** plus one `(rest)` series that sums the others. Lines are ranked by the headline value over the range (sum for counters, count for histograms, average for gauges).
- **Filters** use the same grammar as the SLOs: an exact value, a trailing `*` prefix (`5*`), a bare `*` for any present value, and `{none}` to match a series that lacks the tag. An absent tag reads as `{none}`, so a prefix pattern never matches it.
- **Compare** `1d` and `7d` returns the same range shifted back, read the same way, drawn as dashed overlays.
- **Every point carries its bucket length** in `bucketSeconds`: the display bucket, or the stored bucket of a coarser row that kept its own (an hourly row at the edge of a 6-hour read is 3600). Divide by it for a rate.
- **The newest bucket is flagged while it is still filling.** A point is `incomplete` when its bucket ends after now, or within one `MetricFlushInterval` of now (each process writes its measurements once per interval, so the last ones may not be in yet). For counters and histograms such a bucket reads low, which right after a deploy looks like a volume drop. Only the current range is flagged; overlay buckets are in the past.
- **Headlines cover complete buckets only.** The response carries one `headlines` entry per range (the current one, then each overlay): the summed sum for counters and up-down counters, the summed count for histograms, the average for gauges, over every line including `(rest)`. The still-filling bucket is left out, **never prorated**, and each overlay leaves out the bucket aligned with it, so "now" and "1d ago" cover the same span. `completeUntil` says where the complete buckets end. The value is `null` when no complete bucket holds a measurement. The breakdown ranking still uses the whole range.
- **Percentiles** (1 to 99) are computed from the stored bucket counts, so they read as "at most": the answer is the upper bound of the bucket that holds the percentile. A percentile over the overflow bucket reports the last finite bound. When rows have different bounds the ladder with the most samples is used.

The dashboard API is `GET {prefix}/api/metrics` and `GET {prefix}/api/metrics/{id}/series`, where `{id}` is the URL-safe base64 of the instrument name. The series query takes `from` and `to` (ISO 8601), `application`, `breakdown` (a tag key, or `{version}`), repeated `filter=key:value` (the key may be `{version}`), `compare=1d,7d` and `percentile`. A bad range, percentile, compare value or filter is a 400.

## Dashboard

The **Metrics** page (`/metrics`) lists every instrument with its kind, unit, tag keys, last seen and a **collapse badge**. The detail page has a range picker (1h to 30d, or a custom range), an application picker, a breakdown selector, filter chips, the 1d/7d overlays, a percentile selector for histograms, and avg, min and max for gauges. Above the chart a **headline** shows the range's total (count for histograms, average for gauges) and, with overlays on, the same figure 1 day and 7 days ago with the change in percent; all of them leave out the bucket that is still filling. That bucket is drawn **faded and dashed**, and its tooltip says "still filling".

**Custom range and deploys.** **Custom** opens a from/to picker in the browser's time zone; leave **To** empty for "until now", which keeps the window moving like a preset. Any range up to the API's 90 days can be picked, and the resolution follows the same rules as a preset (up to 6 hours fine, up to 7 days hourly, longer daily). The 1d and 7d overlays shift the custom range back, exactly as they shift a preset. The range is kept in the page URL (`?range=7d` for a preset, `?from=…&to=…` for a custom range, `to` left out for "until now"), so a shared link reproduces the view. A range that ended in the past is not refetched. When [deploy history](./applications.md#deploy-history) is recorded, each deploy in the range is drawn as a dashed vertical line labelled with its version (and its application when no application is selected), and **Since last deploy** sets the range to run from the newest deploy until now: the newest deploy of the selected application, or of any application when none is selected. A deploy older than 90 days is cut to the last 90 days, and the page says so.

Counters and histogram counts have a **Per bucket / Per minute / Per second** toggle. Per bucket (the default) is the stored total, so its scale follows the bucket size: switching from 6h (5-minute buckets) to 24h (hourly) multiplies the y-axis by 12, and an overlay served at another resolution is not comparable by eye. Per minute and per second divide each bucket by its own length, put the unit on the axis ("Sum per minute", "Measurements per second"), and turn the headline into the average rate over the span it covers. The choice is kept in the page URL (`?rate=minute` / `?rate=second`), so a refresh or a shared link keeps the scale. Up-down counters, gauges, histogram percentiles and the ratio view are never divided. The **ratio view** divides one numerator over the total of the same breakdown: pick the breakdown tag, then a single value or a `prefix*` pattern (for example status `5*`) as the numerator. The numerator is its own request, so a low-volume value that lands in `(rest)` is still counted.

The nav item shows where `AddMetrics` ran **in the dashboard's own process**. For a dashboard-only host, whose collectors live in other processes, force it on:

```csharp
app.MapWarpDashboard(o => o.ShowMetrics());
```

The routes inherit the dashboard's authorization like every other page, and expose instrument names and tag values, so gate them accordingly.

## Cardinality

Collected tags can be unbounded (a request id, a user id), and Warp writes them to your own operational database. Each process therefore bounds what a collected instrument can do, in memory:

| Cap (on `WarpMetricsOptions`) | Default | When exceeded |
|---|---|---|
| `MaxTagsPerInstrument` | 6 | Extra keys are dropped, in ordinal key order |
| `MaxValuesPerTag` | 100 | New values of that tag become `{other}` |
| `MaxSeriesPerInstrument` | 500 | New tag sets are recorded under the `{other}` tag set |
| `MaxInstruments` | 300 | Further instruments are **never enabled** and are reported under `{other}` |

A collapse is reported three ways, and **a report never contains a tag value**:

- a **Warning log**, once per (instrument, tag key, reason);
- the always-on meter **`warp.metrics.collapsed{instrument, reason}`**, with reasons `tag_values`, `series`, `tag_count`, `instrument_cap`, `name_conflict`, `name_too_long`, `tags_truncated`, `bounds` and `error`;
- a **badge** on the instrument in the Metrics list (`CollapsedAt` and the collapsed tag keys), with a tooltip naming the keys.

The caps are per process, so with several processes the stored series count can be larger than one cap, since each process caps independently.

## PCI and PII

:::danger Tag values must never carry PII, PAN or customer identifiers
Warp cannot detect PII or card data in a free string. Whatever a measurement is tagged with is persisted to the database in plain text, shown on the dashboard, and returned by the API.
:::

- **Never tag a measurement with a card number (PAN), CVV, name, email, phone number, address, IP address, account number or any customer or cardholder identifier**, including a hash of one that someone could reverse. Tag with bounded categories instead: `result=refused`, `step=capture`, `gateway=primary`.
- **Use `KeepTags` allowlists on every instrument whose tags you don't control**: a shared library, a vendor SDK, or any meter another team owns. An allowlist drops everything else before the series is formed.
- **Values are truncated to 64 characters**, which is a size control and not a privacy control: 64 characters hold a PAN.
- **The built-in presets keep only a safe set.** `CollectAspNetCoreHttp()` keeps `http.route` (the route template, never the raw path or query string), `http.request.method` and `http.response.status_code`. `CollectHttpClient()` keeps `http.request.method`, `server.address`, `http.response.status_code` and `error.type`. No request or response body or header is ever touched.
- Collection is opt-in per meter, and the [cardinality caps](#cardinality) limit the blast radius of a mistake, but they do not remove it.
- The same rule applies to [adapter capture](./adapters.md#pci-payment-processors): leave it at `None` for payment processors and never use `ForceCapture` on them.

Compliance with PCI DSS and data protection law remains the host's responsibility.

## Durability and delivery

- **An ungraceful exit loses at most one flush interval** of measurements per process (30 seconds by default).
- **A graceful shutdown flushes** during `StopAsync`, inside the host's `ShutdownTimeout`. The flusher stops before the collector, so the final drain still sees every measurement.
- **Measurements are not transactional.** A measurement recorded inside a request that later rolls back still counts. These are diagnostics, not an audit trail.
- A failed write is retried on the next flush without losing or double-counting anything. Nothing is written when nothing was measured.

## Coexistence

Several `MeterListener`s receive every measurement independently, so an OpenTelemetry SDK exporter on the same meters keeps working unchanged next to Warp's collector. You can collect a meter in both. For job, adapter and endpoint telemetry the [observability sinks](./observability-sinks.md) page covers routing to OTel instead of the database.

## Limits

This feature is deliberately small. Know these before relying on it:

- **SQL Server's default case-insensitive collation merges tag sets that differ only by letter case** into one series on read, because the grouped read is a `GROUP BY` on the tag text. Keep tag values consistently cased.
- **Under locking READ COMMITTED (SQL Server without RCSI), a read racing a rollup can transiently double-count.** A bucket may briefly read high until the rollup commits. Enable RCSI to avoid it.
- **Baselines older than 6 hours are hour resolution and prorated.** A `1d` or `7d` comparison, and a [volume-floor or ratio baseline](./slo-error-budget.md#metric-objectives), reads rows that have already rolled up to hourly (or daily), and a bucket that only partly overlaps the window contributes its events scaled by the overlap.
- **Read cost at the 500-series cap, measured on PostgreSQL with 1.58 M rows:** a 24 h and a 7 d read **without a breakdown or percentile take about 31 ms**; a **7 d breakdown takes about 1 s**; a **7 d percentile plus breakdown about 0.8 s**. The breakdown and percentile reads at the cap parse every series in memory, so they are the expensive ones. Keep the instruments you chart with a breakdown well under the cap.
- **Caps are per process.** Several processes collecting the same high-cardinality tag each admit up to the cap.
- **Metric SLOs need at least one closed bucket**: the minimum window is 10 minutes.
- **Not a TSDB.** No query language, no custom dashboards, no downsampling beyond the three tiers, no per-point exemplars. Use the OTel exporter for those.

## Configuration reference

| Setting | Where | Default | Description |
|---|---|---|---|
| `MetricFlushInterval` | `WarpConfiguration` | `30s` | How often each process writes its aggregates. Must be greater than zero. |
| `MetricRollupInterval` | `WarpConfiguration` | `1m` | How often `MetricRollup` runs. `null` disables it. |
| `FineResolutionMinutes`, `FineResolutionRetention`, `HourlyStatisticsRetention`, `DailyStatisticsRetention` | `WarpConfiguration` | 5 min, 6 h, 7 d, 90 d | The [retention tiers](./metrics-retention.md), shared with `Statistic` rows. |
| `MaxTagsPerInstrument`, `MaxValuesPerTag`, `MaxSeriesPerInstrument`, `MaxInstruments` | `WarpMetricsOptions` | 6, 100, 500, 300 | The [cardinality caps](#cardinality). |
| `ByVersion()` | `WarpMetricsOptions` | off | Adds `ApplicationVersion` to every series this host collects. See [Comparing versions](#comparing-versions). |

See also [Configuration](/docs/operations/configuration#custom-application-metrics), [SLO / error budget](./slo-error-budget.md#metric-objectives) and [Metrics retention tiers](./metrics-retention.md).

## Migration

Additive: two new tables (`metric_point`, `metric_instrument`), seven nullable columns on `slo_definition` for the metric SLO kinds, and the nullable `metric_point.version` column for [comparing versions](#comparing-versions), all picked up by a standard `dotnet ef migrations add` / `database update`. Rolling back leaves old binaries unaffected, since they ignore the new tables and columns. Without `AddMetrics`, nothing runs.
