# SLO / error budget

Warp already folds every health signal it needs into durable aggregates — per-type/handler execution, queue-wait, backlog depth, deadline attainment — and, with [custom application metrics](./custom-metrics.md), the metrics your own code emits. **SLOs** turn those into promises: declare an objective (a target over a rolling window), and Warp continuously computes **attainment**, the remaining **error budget**, and a multi-window **burn rate**, surfaces them on the dashboard, and fires an operational event on a burn — all off the worker hot path, reading the metrics that are already there.

It's an opt-in feature: call `AddSlo(...)` to turn evaluation on. The objective/evaluation tables and the read/command services are always in the schema, so a dashboard-only process can still view and edit objectives.

## Objective kinds

| Kind | Measures | Target | Windowed |
|---|---|---|---|
| `SuccessRate` | succeeded ÷ (succeeded + failed), per job type | ratio (e.g. `0.995`) | ✓ |
| `ExecutionLatency` | handler execution percentile, per job type | ms | ✓ (via `pcth`) |
| `QueueWaitLatency` | queue-wait percentile, per queue | ms | ✓ (via `pcth`) |
| `BacklogDepth` | current backlog depth, per queue | job count | current gauge |
| `DeadlineAttainment` | fraction of `Total`-scope jobs that met their deadline (§8.7) | ratio | ✓ |
| `MetricRatio` | good ÷ (good + bad) events of a [custom metric](./custom-metrics.md), split by one tag | ratio, or a maximum drop against a baseline | ✓ |
| `MetricThreshold` | matching events of a custom metric stay at or below N | event count (0 allowed) | ✓ |
| `MetricVolumeFloor` | matching events stay above a fraction of the same window 1 or 7 days earlier | ratio | ✓ |
| `MetricGap` | events of one custom metric minus events of another stay at or below N | event count | ✓ |
| `MetricLatency` | percentile of a custom **histogram** stays at or below a threshold | threshold in the instrument's unit, plus a percentile (1–99) | ✓ |
| `MetricGaugeThreshold` | a custom **gauge**'s per-bucket average, min or max stays at most (or at least) a value | gauge value, plus the compliant share of the window (1–99%) | ✓ |

The first five kinds read Warp's own aggregates. The `Metric*` kinds read your [custom application metrics](./custom-metrics.md) and are described [below](#metric-objectives).

Rate/attainment kinds use the standard error-budget model (`budget = 1 − burn`, where `burn = observed error rate ÷ allowed error rate`); latency/depth kinds compare the observed value to the target (lower is better). A retried-then-succeeded job counts as a success. For `DeadlineAttainment`, a job that reaches a terminal state *after* its Total-scope deadline counts as a miss — including a late `Completed`, since the deadline is a time bound, not just a failure signal.

Latency objectives read a bucketed histogram, and the job-domain ladder (execution + queue-wait) extends to **5 minutes** — job timescales run well past the 10 s top of the HTTP-scale surfaces — so a "p95 < 30s" objective observes and breaches at real job latencies rather than saturating at 10 s.

## Metric objectives

The `Metric*` kinds hold a [custom application metric](./custom-metrics.md) to an objective, with the same burn, No data, edge-alert and acknowledge machinery as the others. For these kinds `Dimension` holds the **instrument name** and `Application` scopes the series (null means all applications).

**What an "event" is.** The quantity an objective counts per bucket is the **sum** for counters and up-down counters, and the **count** of measurements for histograms. Gauges have no event count, so they are rejected by every kind except `MetricGaugeThreshold`, which accepts **only** gauges. `MetricLatency` reads a histogram's **bucket counts** and accepts histograms only.

**Filters.** `TagFilter`, `SecondaryTagFilter` and the ratio value lists use one grammar: `k=v[,k=v...]`, where a value may end in `*` for a prefix match (`http.response.status_code=5*`). An absent tag reads as `{none}` and only matches `k={none}`. The Metrics page uses the same matcher, so a filter means the same thing in an objective and on the page.

### Semantics

| Kind | Observed over the window | Breaching when | Budget | No data when |
|---|---|---|---|---|
| `MetricRatio` | good = events whose `RatioTag` value matches `GoodValues`; bad = events matching `BadValues`; every other value is **ignored** (for example `refused`) | good ÷ (good + bad) < target | the rate budget (`1 − burn`) | good + bad = 0 |
| `MetricRatio` with `BaselineDays` | the same ratio now and over the same window 1 or 7 days earlier | baseline ratio − current ratio > target, where target is the **maximum drop in ratio points** (0 to 1) | `(target − drop) ÷ target` | the current or the baseline good + bad = 0 |
| `MetricThreshold` | events matching `TagFilter` | observed > N (N ≥ 0, **0 is allowed**) | N = 0: 1 when there are none, otherwise −observed; N > 0: `(N − observed) ÷ N` | the instrument has **no rows at all** in the window |
| `MetricVolumeFloor` | matching events now vs the same window `BaselineDays` earlier | now ÷ baseline < target (0 to 1) | `(ratio − target) ÷ (1 − target)` | the baseline is 0 |
| `MetricGap` | events(primary, `TagFilter`) − events(secondary, `SecondaryTagFilter`) | gap > N (N ≥ 0) | as `MetricThreshold` | neither instrument has rows in the window |
| `MetricLatency` | matching histogram events split at the threshold: **within** = events in buckets whose upper bound is ≤ the threshold, **over** = the rest (overflow bucket included) | within ÷ (within + over) < `Percentile` ÷ 100, i.e. the bucket-resolution p`Percentile` is above the threshold | the rate budget, with an allowed slow fraction of `1 − Percentile ÷ 100` | no matching event with bucket counts in the window |
| `MetricGaugeThreshold` | per bucket, the `GaugeAggregate` (average, min or max) of the matching gauge samples; a bucket **complies** or **violates** (`Comparison`: above the target for `AtMost`, below it for `AtLeast`; equal complies) | violating time ÷ observed time > `1 − Percentile ÷ 100` | the rate budget, with that allowed violating share | no bucket with samples in the window |

- **`MetricLatency` is a good-event ratio, not a percentile value.** "p95 ≤ 0.5 s" is the same statement as "at most 5% of requests are slower than 0.5 s", and the second form is what the budget model needs: the observed value (`Attainment`) is the share of events within the threshold, and burn is the slow share over the allowed 5%, so a p95 objective breaching twice as fast as allowed reads burn 2× on the same scale as `SuccessRate`. The dashboard shows the target as `p95 ≤ 0.5` and the observed value as `97.20% ≤ 0.5`.
- **The threshold is compared with bucket upper bounds, so percentile resolution is the bucket ladder.** A histogram stores counts per bucket, not individual measurements. An event in a bucket whose upper bound is at most the threshold is within it; an event in the bucket that straddles the threshold cannot be placed and counts as **over**. A threshold of `0.3` on the ladder `…, 0.25, 0.5, …` therefore behaves exactly like `0.25`. Pick a threshold that is one of the instrument's bounds (the Metrics page shows them), or configure the bounds with `AddMetrics(m => m.Buckets(...))`. The threshold is in the **instrument's unit**: seconds for `http.server.request.duration`, milliseconds for an instrument whose unit is `ms`.
- **`MetricGaugeThreshold` compares one stored value per bucket.** A gauge row stores the count, sum, min and max of the samples a process observed in a bucket (an observable gauge is read once per flush, every 30 s by default). The evaluator combines every matching row of a bucket — counts and sums add, the extremes take the min / max — so **`Average` is the sample-weighted mean across processes and series, never their total**, `Min` is the lowest sample any process saw and `Max` the highest. A per-process quantity (each instance's own queue length) is therefore averaged, not totalled: use `Max` for "no instance above X", and a tag filter to pin one series when the gauge has several (one per queue, say). Rollup keeps the same four values, so an hourly bucket's average is the average of its samples, not of its 5-minute averages.
- **The bad fraction is time-weighted.** Each bucket with samples counts for the time it covers in the window (an hourly row for up to an hour, a fine row for five minutes, the still-filling bucket up to now), so a window that spans fine and hourly rows is not skewed toward the fine ones. With only fine rows this is exactly *buckets in violation ÷ buckets observed*. Time with no samples (a process that was down) counts neither way. `Percentile` is the share of observed time that must comply, so "p95 of the bucket values ≤ X" and "X holds 95% of the time" are the same objective; with a 1-hour window of 5-minute buckets a single violating bucket is 8.3% and already breaches a 95% objective.
- **Each stored row is split on its own ladder**, so rows with different bounds (a bounds change across a deploy) are all counted, and a row whose bucket detail was dropped (the rollup keeps the counts but drops a bucket sum too large for its column) is skipped.

- **`MetricThreshold` is Healthy at zero when there is traffic.** If the instrument has rows in the window but none match the filter, the observed value is 0 and the objective is Healthy. That is what makes "5xx = 0" work: a quiet hour with only 2xx responses is not "No data". A typo'd instrument name still has no rows at all, so it reads No data.
- **Non-finite values read No data.** A `NaN` or infinite observed value, an overflowing result, or a negative event total (an up-down counter driven below zero) reports No data, never a false Healthy.
- **A stored objective that cannot be evaluated reads No data and logs an error**: an instrument that turned into a gauge after the objective was saved (or, for `MetricLatency`, into anything but a histogram, and for `MetricGaugeThreshold` into anything but a gauge), an invalid filter, or a missing required field. Nothing alerts on No data.
- **Windows.** The short (fast-burn) window is `max(5 min, window ÷ 12)`, as for the other kinds, and the **minimum `WindowSeconds` for a metric kind is 600**, so a window always spans at least one closed bucket. The window's trailing edge includes the partial fine rows the flushers have written, so a fast burn reacts within about one flush interval.
- **Every window and baseline reads all tiers that overlap it** (fine, hourly and daily), so the newest data that has not rolled up yet is included. A bucket that only partly overlaps the window contributes its events scaled by the overlap. A baseline older than 6 hours is therefore **hourly resolution, prorated**.
- **Read cost.** The evaluator reads the whole window (and baseline) on every tick, so long windows on high-cardinality instruments cost proportionally more; an objective with no tag filter (other than a ratio, which always needs its ratio tag) reads totals grouped per bucket instead of per tag set.

### Validation

Objectives are validated on save (a 400 from the endpoint) and when seeded (an exception):

- `Dimension` must be a valid instrument name of at most 128 characters, and `WindowSeconds` must be at least **600**.
- The instrument must not be a gauge (checked against the instrument registry on save and when a seed is inserted; an instrument that has not reported yet is allowed). For `MetricGap` the same applies to `SecondaryMetric`. **`MetricLatency` needs a histogram**: a counter, up-down counter or gauge is rejected. **`MetricGaugeThreshold` needs a gauge** and is the only kind that accepts one.
- `MetricRatio` needs a `RatioTag` and non-empty `GoodValues` and `BadValues` (comma-separated, a trailing `*` allowed). **The two lists must not overlap**: no value may match a pattern in both (`500` against `5*` overlaps). The target is a ratio between 0 and 1 (exclusive), or, with `BaselineDays`, the maximum drop and also between 0 and 1.
- `MetricThreshold` and `MetricGap` need a target of 0 or more; `MetricGap` also needs `SecondaryMetric`. `MetricVolumeFloor` needs `BaselineDays` of 1 or 7 and a target between 0 and 1. `MetricLatency` needs a threshold of 0 or more and a `Percentile` from 1 to 99 (95 when left out); `MetricGaugeThreshold` takes any finite target (a gauge can be negative), `Comparison` `AtMost` (default) or `AtLeast`, `GaugeAggregate` `Average` (default), `Min` or `Max`, and a `Percentile` from 1 to 99 (95 when left out) as the compliant share. Every other metric kind has its `Percentile`, `Comparison` and `GaugeAggregate` cleared.
- **A 7-day baseline requires `HourlyStatisticsRetention` of at least 7 days + the window + 1 hour.** Otherwise the hourly baseline would already have rolled to daily. **The default is exactly 7 days, so a `BaselineDays = 7` objective is rejected until you raise `HourlyStatisticsRetention`** (to 8 days, say). The validation message names the setting. A 1-day baseline has no such requirement.
- Fields that do not belong to a kind are cleared, and filters must parse.

### Builder API

Next to `AddObjective`, on the same `AddSlo` options:

```csharp
opt.AddSlo(o =>
{
    o.AddMetricRatio(name, instrument, ratioTag, goodValues, badValues, target,
        windowSeconds: 3600, application: null, tagFilter: null, baselineDays: null);

    o.AddMetricThreshold(name, instrument, maxEvents,
        windowSeconds: 3600, application: null, tagFilter: null);

    o.AddMetricVolumeFloor(name, instrument, minRatio, baselineDays,
        windowSeconds: 3600, application: null, tagFilter: null);

    o.AddMetricGap(name, instrument, secondaryMetric, maxGap,
        windowSeconds: 3600, application: null, tagFilter: null, secondaryTagFilter: null);

    o.AddMetricLatency(name, instrument, threshold, percentile: 95,
        windowSeconds: 3600, application: null, tagFilter: null);

    o.AddMetricGaugeThreshold(name, instrument, threshold,
        comparison: SloComparison.AtMost, aggregate: SloGaugeAggregate.Average, percentile: 95,
        windowSeconds: 3600, application: null, tagFilter: null);
});
```

Seeding is insert-if-absent, and for metric kinds the match key adds `TagFilter`, `SecondaryMetric`, `RatioTag`, `Percentile`, `Comparison` and `GaugeAggregate` (and, for `MetricLatency` and `MetricGaugeThreshold`, the threshold), so two objectives on one instrument with different filters, percentiles, thresholds or aggregates do not collide. A metric objective is also matched by its name (unique per kind and application; a repeated name throws at registration), so editing its filter, secondary instrument or ratio tag in the dashboard does not reseed a duplicate on restart. Renaming it alone does not either; only editing both the name and those fields does. A seeded objective is re-validated against `HourlyStatisticsRetention` when the host starts. The SLOs page has a form with an instrument picker and the fields of each kind.

When a metric objective breaches, `SloBreachedEvent.Metric` (nullable, metric kinds only) carries a label of what it reads, built from the objective's own definition (for example `http.server.request.duration [http.route=/api/adyen/*,http.response.status_code=5*]`). It never contains a tag value observed in the data.

### Recipes

The examples use `http.server.request.duration` from `CollectAspNetCoreHttp()` and the `payment.results` counter from the [quick start](./custom-metrics.md#quick-start).

**HTTP 5xx = 0 on a route prefix.** Matching events must stay at 0 over the window, filtered to one route prefix and the 5xx statuses:

```csharp
o.AddMetricThreshold(
    "Adyen callbacks: no 5xx", "http.server.request.duration", maxEvents: 0,
    windowSeconds: 900,
    tagFilter: "http.route=/api/adyen/*,http.response.status_code=5*");
```

**A success ratio.** 2xx and 3xx are good, 5xx are bad, 4xx are ignored (a client error is not the service failing):

```csharp
o.AddMetricRatio(
    "API success ratio", "http.server.request.duration",
    ratioTag: "http.response.status_code", goodValues: "2*,3*", badValues: "5*",
    target: 0.995);
```

**Payment success ratio of at least 0.9, ignoring `refused`.** A refusal is a legitimate business outcome, not a fault. Leave it out of both lists and it counts as neither:

```csharp
o.AddMetricRatio(
    "Payment success", "payment.results",
    ratioTag: "result", goodValues: "approved", badValues: "error",
    target: 0.9, tagFilter: "step=authorize");
```

**Volume of at least 50% of last week.** Alert on a silent drop in traffic, with a 7-day baseline (raise `HourlyStatisticsRetention` first, see [Validation](#validation)):

```csharp
o.AddMetricVolumeFloor(
    "Webhook intake vs last week", "notifications.received",
    minRatio: 0.5, baselineDays: 7, windowSeconds: 3600);
```

**Received minus processed at most 20.** Two counters, one gap. Both are summed over the window, so the number is the backlog that built up in it:

```csharp
o.AddMetricGap(
    "Webhook backlog", "notifications.received", secondaryMetric: "notifications.processed",
    maxGap: 20, windowSeconds: 900);
```

**Checkout p95 at most 500 ms.** `http.server.request.duration` is in seconds and uses the OTel seconds ladder (`…, 0.25, 0.5, 0.75, …`), so `0.5` is a bucket bound. A second tier on the same route, differing only by threshold, is a separate objective:

```csharp
o.AddMetricLatency(
    "Checkout p95", "http.server.request.duration", threshold: 0.5, percentile: 95,
    tagFilter: "http.route=/api/checkout");
o.AddMetricLatency(
    "Checkout p95 (page)", "http.server.request.duration", threshold: 1, percentile: 95,
    tagFilter: "http.route=/api/checkout");
```

**No instance's queue above 500, 95% of the time.** An `ObservableGauge<int>` that each process reports for its own in-memory queue: `Max` takes the worst instance in each bucket (an average would hide one stuck instance among healthy ones):

```csharp
o.AddMetricGaugeThreshold(
    "Webhook queue depth", "webhooks.queue.depth", threshold: 500,
    aggregate: SloGaugeAggregate.Max, percentile: 95);
```

**Connection pool never drained.** `AtLeast` with `Min`: a bucket whose lowest sample of free connections fell under 2 violates:

```csharp
o.AddMetricGaugeThreshold(
    "Free DB connections", "db.pool.free", threshold: 2,
    comparison: SloComparison.AtLeast, aggregate: SloGaugeAggregate.Min, percentile: 99,
    windowSeconds: 21600);
```

## Fast-burn

The **short (fast-burn) window** is the objective's window ÷ 12, floored to 5 minutes — and it's real, because it reads the **5-minute fine tier** from the metrics retention tiers (§8.30). Every objective reports two burn rates: **fast** (recent) and **slow** (full window), so a sudden burn is caught quickly while the slow window governs the sustained budget. `ExecutionLatency`/`QueueWaitLatency` read the tiered `pcth` latency histogram, so their percentiles are windowed rather than all-time.

Everything is **off the hot path** — a periodic `SloEvaluator` server task reads the already-folded aggregates and upserts one status row per objective. The only new write on the hot path is the deadline-miss counter (§8.7), recorded once at finalization alongside the execution counters.

## Configuration

Seed objectives in code, edit them in the dashboard — the DB row wins, so a dashboard edit is never clobbered on restart:

```csharp
services.AddWarpServer<AppDb>(opt =>
{
    opt.UsePostgreSql();

    opt.AddSlo(o =>
    {
        o.AddObjective(SloKind.SuccessRate, "MyApp.Jobs.SendEmail", target: 0.995);
        o.AddObjective(SloKind.QueueWaitLatency, "default", target: 30_000, percentile: 95);
        o.AddObjective(SloKind.DeadlineAttainment, "MyApp.Jobs.Charge", target: 0.99);
    });

    opt.SloEvaluationInterval = TimeSpan.FromMinutes(1); // default; null disables evaluation
});
```

## Alerting

On a healthy→breaching edge the evaluator fires a `WarpEventType.SloBreached` operational event through the notifier seam (`AddNotifier<T>`, §8.25) — `BacklogDepth` objectives fire the previously-reserved `BacklogBreached`. Severity is `Error` on a fast burn, `Warning` on a slow one. It fires **once per edge**, not every tick, and is suppressed while an operator has acknowledged the objective. Delivery is the host's notifier (Slack, email, PagerDuty, …) — Warp raises the event, the host routes it.

## Dashboard

The **SLOs** page (`/slo`, gated on the `slo` addon flag) lists every objective with its state (Healthy / Warning / Breaching / Acknowledged / No data), error-budget bar, and fast/slow burn, and lets you create/edit/delete objectives and acknowledge a breach. **No data** is distinct from Healthy: an objective whose dimension never matched any metric (a typo'd job type or queue) reads as No data instead of a false green, and never alerts. Objectives are validated on save — a rate target must be a 0–1 ratio, a window must be positive — so a malformed objective can't silently disable its own evaluation. The detail page (`/slo/:id`) shows the budget gauge, burn stats, and the window/fast-burn breakdown. `GET/POST/DELETE {prefix}/api/slo*` serve the same data in any `AddWarp` process.

## Migration

Additive — two new tables (`slo_definition`, `slo_evaluation`), picked up by a standard `dotnet ef migrations add` / `database update`. The metric kinds (7.5.0) add seven nullable columns to `slo_definition` (`TagFilter`, `SecondaryMetric`, `SecondaryTagFilter`, `RatioTag`, `GoodValues`, `BadValues`, `BaselineDays`), and `MetricGaugeThreshold` two more (`Comparison`, `GaugeAggregate`, nullable ints), all additive. Disable with `SloEvaluationInterval = null`, or simply never call `AddSlo()`.
