---
sidebar_position: 7
---

# Metrics and SLOs

This page covers the numbers Northwind Goods tracks itself: checkout outcomes, PayFast latency, Stockroom sync
lag. Warp collects them from the shop's own `System.Diagnostics.Metrics` code, keeps their history in tiers,
holds them to objectives, puts the important ones on one board for the post-deploy check, and lets the team
write down known noise next to them.

| Feature | Reference |
|---|---|
| [Custom metrics](#custom-metrics) | [Custom application metrics](../features/custom-metrics.md) |
| [Metrics retention tiers](#metrics-retention-tiers) | [Metrics retention tiers](../features/metrics-retention.md) |
| [SLOs and error budgets](#slos-and-error-budgets) | [SLO / error budget](../features/slo-error-budget.md) |
| [Health board](#health-board) | [Health board](../features/custom-metrics.md#health-board) |
| [Annotations](#annotations) | [Annotations](../features/custom-metrics.md#annotations) |

SQL examples assume the default `warp` schema and `UseSnakeCaseNamingConvention()`.

## Custom metrics

**In the shop.** The team wants to know how many checkouts succeed, decline or fail per payment method, how
long PayFast takes to authorise and to call back, and how far the Stockroom stock sync lags behind. These live
in the shop's own code, not in Warp, so Warp only collects them.

**Wiring.** The emitting code is plain .NET and takes no Warp dependency:

```csharp
public sealed class ShopMetrics
{
    private readonly Counter<long> _checkouts;
    private readonly Histogram<double> _authorise;
    private readonly Histogram<double> _callback;

    public ShopMetrics(IMeterFactory meterFactory, StockroomSyncState sync)
    {
        var meter = meterFactory.Create("Northwind.Shop");
        _checkouts = meter.CreateCounter<long>("checkout.completed");
        _authorise = meter.CreateHistogram<double>("payfast.authorise.duration", unit: "s");
        _callback = meter.CreateHistogram<double>("payfast.callback.latency", unit: "s");
        meter.CreateObservableGauge("stockroom.sync.lag", () => sync.LagSeconds, unit: "s");
    }

    // paymentMethod: card | wallet | invoice; outcome: success | declined | error
    public void CheckoutCompleted(string paymentMethod, string outcome)
        => _checkouts.Add(1, new("payment_method", paymentMethod), new("outcome", outcome));

    public void Authorised(double seconds) => _authorise.Record(seconds);

    public void CallbackReceived(double seconds) => _callback.Record(seconds);
}
```

Each service names the meter it wants collected. `ByVersion()` adds `ApplicationVersion` as a series dimension:

```csharp
// shop-api and shop-worker
opt.ApplicationName = "shop-api";                                   // "shop-worker" in the worker
opt.ApplicationVersion = builder.Configuration["Build:Version"];
opt.AddMetrics(m => m
    .CollectMeter("Northwind.Shop")
    .CollectAspNetCoreHttp()                                         // http.server.request.duration
    .CollectHttpClient()                                             // http.client.request.duration
    .Instrument("payfast.authorise.duration", i => i.Buckets(0.1, 0.25, 0.5, 1, 2, 5, 10))
    .ByVersion());
```

The detail page of each instrument then offers:

- **Version dimension.** Breakdown or filter by **application version** (`{version}` in the API) to compare
  1.4.1 and 1.4.2 during a rolling deploy.
- **Custom range.** A from/to picker. The range lives in the URL (`?from=…&to=…`, no `to` for "until now").
- **Deploy markers.** Each [recorded deploy](./observability.md#applications-and-deploys) is a dashed line
  labelled with its version. **Since last deploy** sets the range from the newest deploy to now.
- **Rate toggle.** **Per bucket / Per minute / Per second** for counters and histogram counts (`?rate=minute`),
  so a 6h view and a 24h view have the same scale.

**Test scenario.**

- *Given* `shop-api` 1.4.1 running with the wiring above.
- *When* you place 10 orders (8 card successes, 1 wallet decline, 1 invoice error) and wait 30 seconds (one
  `MetricFlushInterval`).
- *Then* the **Metrics** page lists `checkout.completed`, `payfast.authorise.duration`,
  `payfast.callback.latency` and `stockroom.sync.lag`. Breakdown by `outcome` on `checkout.completed` shows
  8 / 1 / 1. `SELECT sum(sum) FROM warp.metric_point WHERE instrument = 'checkout.completed'` returns 10.
- *When* you roll out 1.4.2 while one 1.4.1 instance still runs, and place orders against both.
- *Then* breakdown by **application version** draws two lines, `1.4.1` and `1.4.2`. A dashed marker labelled
  `1.4.2` sits at the deploy time. **Since last deploy** sets the URL to `?from=<deploy time>` with no `to`.
- *When* you switch **Per minute** on a 24h range.
- *Then* the axis reads "Sum per minute", the URL has `?rate=minute`, and a refresh keeps it.
- `Covered by:` `WarpMeterCollectorTests`, `MetricSeriesTests`, `MetricBucketsTests`, `MetricCardinalityTests`,
  `MetricAggregatorTests`, `MetricFlusherTestsBase`, `MetricQueryServiceTestsBase`, `WarpMetricsOptionsTests`,
  `CustomMetricsEndToEndTestsBase`; custom range, deploy markers and rate toggle in the UI: `metricModel.test.ts`.

Pitfall: never put customer ids, emails or PAN in metric tags (§1.2). Tag with bounded categories such as
`payment_method=card`. Warp stores tag values in plain text and cannot detect PII.

## Metrics retention tiers

**In the shop.** The team wants 5-minute detail for today's checkout chart, hourly history for the last week to
compare against, and a 90-day view of the Black Friday weeks, without the metric tables growing forever.

**Wiring.** The tiers are always on and shared by Warp's own statistics and the custom metrics. The shop
raises the hourly tier to 8 days because its "checkouts vs last week" objective uses a 7-day baseline:

```csharp
// shop-api and shop-worker
opt.FineResolutionRetention = TimeSpan.FromHours(6);     // default
opt.HourlyStatisticsRetention = TimeSpan.FromDays(8);    // default 7; raised for BaselineDays = 7
opt.DailyStatisticsRetention = TimeSpan.FromDays(90);    // default
```

**Test scenario.**

- *Given* `shop-worker` running (it runs `MetricRollup` and `StatisticRollup`) and checkout traffic for a day.
- *When* you run `SELECT tier, min(bucket_start), count(*) FROM warp.metric_point WHERE instrument =
  'checkout.completed' GROUP BY tier`.
- *Then* tier 1 (fine) only covers about the last 6 hours, tier 2 (hourly) covers the rest, and no fine bucket
  older than 6 hours remains. The 24h chart on the Metrics page is continuous across the boundary and its
  total equals the sum of all rows.
- `Covered by:` `StatisticRollupTestsBase`, `MetricRollupTestsBase`, `MetricRollupHeavyTestsBase`,
  `MetricTiersTests`.

Pitfall: `BaselineDays = 7` needs `HourlyStatisticsRetention` of at least 7 days plus the window plus one hour.
At the 7-day default the objective is not seeded and an error naming the setting is logged.

## SLOs and error budgets

**In the shop.** The team commits to "at least 99.5% of checkouts succeed" (declines are a business outcome,
not a failure), "p95 PayFast authorise under 2 s", "Stockroom sync lag under 15 minutes 95% of the time",
"no 5xx on the checkout route", and "checkout volume not below half of last week". The payments queue-wait
objective is on the [Observability](./observability.md#queue-metrics) page.

**Wiring.** On `shop-worker`, which runs the `SloEvaluator`:

```csharp
opt.AddSlo(o => o
    .AddMetricRatio(
        "Checkout success", "checkout.completed",
        ratioTag: "outcome", goodValues: "success", badValues: "error",   // "declined" is ignored
        target: 0.995, application: "shop-api")
    .AddMetricLatency(
        "PayFast authorise p95 < 2 s", "payfast.authorise.duration",
        threshold: 2, percentile: 95, application: "shop-worker")
    .AddMetricGaugeThreshold(
        "Stockroom sync lag", "stockroom.sync.lag", threshold: 900,
        aggregate: SloGaugeAggregate.Max, percentile: 95, application: "shop-worker")
    .AddMetricThreshold(
        "Checkout: no 5xx", "http.server.request.duration", maxEvents: 0, windowSeconds: 900,
        application: "shop-api",
        tagFilter: "http.route=api/checkout*,http.response.status_code=5*")
    .AddMetricVolumeFloor(
        "Checkouts vs last week", "checkout.completed", minRatio: 0.5, baselineDays: 7,
        application: "shop-api"));
```

| Objective | Kind | What it reads |
|---|---|---|
| Checkout success | `MetricRatio` | `success` ÷ (`success` + `error`) |
| PayFast authorise p95 < 2 s | `MetricLatency` | share of authorise calls in buckets ≤ 2 s |
| Stockroom sync lag | `MetricGaugeThreshold` | per-bucket max lag ≤ 900 s, 95% of the time |
| Checkout: no 5xx | `MetricThreshold` | 5xx count on the checkout route |
| Checkouts vs last week | `MetricVolumeFloor` | checkouts now ÷ same hour last week |
| Payments queue wait | `QueueWaitLatency` | Warp's own queue-wait p95 on `payments` |

A breach fires `SloBreached` through the ops [notifier](./observability.md#operational-notifications).
Take `http.route` values from the Metrics page's filter picker: ASP.NET Core records the route template as
written in the route attribute, so check the exact form before you filter on it.

**Test scenario.**

- *Given* the objectives above, a PayFast sandbox you can slow down, and a test Stockroom you can stop.
- *When* you make the sandbox answer authorise in 3 s and place 20 orders.
- *Then* within a minute or two the **SLOs** page shows "PayFast authorise p95 < 2 s" as **Breaching**, with an
  observed value below `95.00% ≤ 2`, a negative error budget and a high fast burn. The ops channel gets one
  `SloBreached` message. **Acknowledge** it and no further message is sent for that breach.
- *When* you stop the Stockroom sync so `stockroom.sync.lag` climbs past 900 for more than 3 minutes of the hour.
- *Then* "Stockroom sync lag" goes Warning, then Breaching.
- *When* you rename the ratio tag in the code to `result` without changing the objective.
- *Then* "Checkout success" reads **No data**, not Healthy, and never alerts.
- `Covered by:` `SloEvaluatorTestsBase`, `MetricSloEvaluatorTestsBase`, `SloMathTests`, `SloValidationTests`,
  `SloCommandQueryTestsBase`, `MetricFilterTests`, `CustomMetricsEndToEndTestsBase`.

Pitfall: a `MetricLatency` threshold is compared with bucket upper bounds. On the default seconds ladder 2 is
not a bound, so a 2 s threshold behaves like 1 s. That is why the wiring sets `Buckets(…, 1, 2, 5, …)`.

## Health board

**In the shop.** Ten minutes after a deploy, someone needs one screen: checkout success %, partner webhook
delivered %, 5xx count, and PayFast callback volume, each against yesterday or last week. The same tiles gate
the CI [post-deploy verdict](./observability.md#post-deploy-verdict).

**Wiring.** Tiles can be edited on the **Board** tab or seeded from code (insert-if-absent). Scope each to the
application it should gate:

```csharp
// shop-api (any AddWarp process can own the board)
opt.AddMetricBoard(b => b
    .Ratio("Checkout success", "checkout.completed",
        ratioTag: "outcome", goodValues: "success", badValues: "error", application: "shop-api",
        check: MetricTileCheck.Deviation(0.02, baselineDays: 1, direction: MetricDeviationDirection.Drop))
    .Ratio("Partner webhooks delivered", "http.client.request.duration",
        ratioTag: "http.response.status_code", goodValues: "2*", badValues: "4*,5*",
        tagFilter: "server.address=hooks.partner.example", application: "shop-worker",
        check: MetricTileCheck.AtLeast(0.98))
    .Value("Checkout 5xx", "http.server.request.duration",
        tagFilter: "http.route=api/checkout*,http.response.status_code=5*", application: "shop-api",
        check: MetricTileCheck.AtMost(5))
    .Value("PayFast callbacks", "payfast.callback.latency", application: "shop-api",
        check: MetricTileCheck.Deviation(0.5, baselineDays: 7, direction: MetricDeviationDirection.Drop)));
```

A value tile on a histogram counts measurements, so "PayFast callbacks" catches a broken callback route, not
slow callbacks. PayFast latency is gated by the `MetricLatency` SLO, which the verdict also counts.

"At least 0.98" on a ratio tile uses the success-rate budget, `(value − 0.98) / (1 − 0.98)`: 99.5 % delivered
is **Healthy**, 98.0–98.4 % is **Warning** and anything below 98 % is **Breaching**.

**Test scenario.**

- *Given* the tiles above and a day of normal traffic.
- *When* you deploy a `shop-api` build that throws on SKU `TEST-500`, send 10 such checkouts, and open
  **Metrics > Board** with **Since last deploy**.
- *Then* "Checkout 5xx" is red (**Breaching**) with value 10, its sparkline shows a dashed deploy line, and
  "Checkout success" shows the drop against 1 day earlier. The other tiles stay green. `GET
  /warp/api/metrics/board?from=<deploy>&to=<now>` returns the same states.
- `Covered by:` `MetricBoardTestsBase`, `MetricBoardMathTests`, `MetricBoardValidationTests`; UI:
  `boardModel.test.ts`.

Pitfall: an unscoped tile (no `application`) counts in every application's verdict. On the shared database a
checkout tile without `application: "shop-api"` can fail a `shop-worker` release.

## Annotations

**In the shop.** About once a day PayFast sends the same callback twice at nearly the same moment, and one
checkout records `outcome=error` although the order is fine. It predates the current release and is known. The
team wants that written down next to the chart, and not to fail a deploy for it.

**Wiring.** Add a note on the **Metrics** detail page or on a tile, or through the API. Mark it acknowledged
noise so a Warning or Breaching tile on the same instrument reads **Acknowledged**:

```bash
curl -fsS -H "Authorization: Bearer $WARP_OPS_TOKEN" -H "Content-Type: application/json" \
  -X POST "https://ops.northwind.example/warp/api/annotations" \
  -d '{
        "id": 0,
        "target": 1,
        "instrument": "checkout.completed",
        "tagFilter": "outcome=error",
        "note": "PayFast duplicate-callback race, about once a day, known (PAY-412)",
        "startsAt": "2026-11-01T00:00:00Z",
        "endsAt": null,
        "acknowledgedNoise": true
      }'
```

The author is the signed-in dashboard user, never a body field.

**Test scenario.**

- *Given* the "Checkout success" tile from the [health board](#health-board) in **Warning** because of one
  duplicate-callback error in a low-traffic window.
- *When* you add the note above.
- *Then* the tile reads **Acknowledged**, its value and sparkline do not change, the tooltip lists the note and
  names the rule's own verdict (`unacknowledgedStatus` in the API), and the sparkline shows a shaded band. The
  verdict check for that tile reads `"acknowledged": true` and does not fail the deploy. The
  `metric_annotation` row has your user name as `author`.
- *When* you set `endsAt` to an hour ago.
- *Then* the tile shows its real status again. Annotations never change SLO evaluation: use **Acknowledge** on
  the SLO for that.
- `Covered by:` `MetricAnnotationTestsBase`, `MetricAnnotationValidationTests`; UI: `annotationModel.test.ts`.

Pitfall: a note stores only what you type. Do not paste order ids, customer names or card data into it.
