---
sidebar_position: 6
---

# Observability

This page covers how Northwind Goods sees what its services are doing: one trace per customer action, the
calls in and out of `shop-api` and `shop-worker`, browser errors from `shop-web`, queue health, grouped
errors, alerts to the ops channel, and a pass or fail answer after each deploy. Each section links to the
reference page for the full detail.

| Feature | Reference |
|---|---|
| [Tracing (unified trace view)](#tracing-unified-trace-view) | [Job tracing](../features/tracing.md) |
| [Observability sinks](#observability-sinks) | [Observability sinks & OpenTelemetry](../features/observability-sinks.md) |
| [Endpoint observability](#endpoint-observability) | [Inbound endpoint observability](../features/endpoint-observability.md) |
| [Client observability](#client-observability) | [Client (frontend) observability](../features/client-observability.md) |
| [Queue metrics](#queue-metrics) | [Queue metrics](../features/queue-metrics.md) |
| [Error grouping (Issues)](#error-grouping-issues) | [Error grouping / Issues](../features/error-grouping.md) |
| [Operational notifications](#operational-notifications) | [Operational notifications](../features/operational-notifications.md) |
| [Applications and deploys](#applications-and-deploys) | [Multi-application observability](../features/applications.md) |
| [Post-deploy verdict](#post-deploy-verdict) | [Post-deploy verdict](../features/applications.md#post-deploy-verdict) |

The dashboard is mapped at `/warp` on `shop-api` and reached by the ops team at
`https://ops.northwind.example/warp`. SQL examples assume the default `warp` schema and
`UseSnakeCaseNamingConvention()`.

## Tracing (unified trace view)

**In the shop.** A customer says "I paid but never got a confirmation email". Support has the order id and
opens the `AuthorisePayment` job for it. From there they want the whole story on one screen: the browser's
`POST /api/checkout`, the `shop-api` call it hit, the jobs `OrderPlaced` fanned out to, and the PayFast and
Mailer calls those jobs made.

**Wiring.** Job tracing needs no setup: every job spawned from a handler shares its `TraceId`. The other
spans in the waterfall come from the rows the other features already write, so the trace view is as complete
as what you record: [endpoint observability](#endpoint-observability) for `shop-api`, `AddAdapters()` for the
PayFast and Mailer clients, and [client observability](#client-observability) for `shop-web`. To also send
the spans to an external backend:

```csharp
// shop-api and shop-worker
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(WarpTelemetry.ServiceName).AddOtlpExporter())
    .WithMetrics(m => m.AddMeter(WarpTelemetry.ServiceName).AddOtlpExporter());
```

**Test scenario.**

- *Given* `shop-api` has endpoint observability with `CheckoutController` opted in, `shop-worker` calls
  PayFast and Mailer through adapters with `AddAdapters()`, and `shop-web` loads the client script.
- *When* you place an order on `shop-web`, then open the `AuthorisePayment` job on the dashboard and click
  **View full trace**.
- *Then* `/trace/{traceId}` shows the waterfall with one green client bar (`POST /api/checkout`), one slate
  server bar for the same route, blue bars for `ReserveStock`, `AuthorisePayment` and `SendConfirmationEmail`
  (each nested under `OrderPlaced` in the job graph below), and purple bars for the PayFast and Mailer calls
  inside their job bars. Make Mailer return 500 and the Mailer bar turns red.
- `Covered by:` `TraceQueryTestsBase`, `TraceIntegrationTestsBase`, `TracePropagationIntegrationTestsBase`,
  `ActivityTraceTestsBase`, `TraceGraphDefaultTests`.

Pitfall: a job whose logs have aged out under retention shows a `—` placeholder instead of a bar. Look at
traces while they are fresh.

## Observability sinks

**In the shop.** The ops team runs Grafana and Tempo next to the Warp dashboard. They want checkout requests in
both places, but the ShipIt tracking poll calls ShipIt thousands of times an hour and should not fill the
database with call-log rows.

**Wiring.** Each surface picks its `RecordingSink` (`Database` by default, `Otel` or `Both`):

```csharp
// shop-api
builder.Services.AddWarp<ShopDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.AddEndpointObservability(o => o.Sink = RecordingSink.Both);   // dashboard rows + span attributes
});

// shop-worker (the ShipIt tracking poller runs here)
builder.Services.AddWarpServer<ShopDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.AddAdapters(o => o.Sink = RecordingSink.Otel);   // span attributes only, no adapter_call_log rows
});
```

**Test scenario.**

- *Given* the wiring above and an OTLP collector receiving the `Warp` source.
- *When* a checkout runs and the ShipIt tracking job polls a parcel.
- *Then* the checkout request has a row on the dashboard's **Endpoints** page and its ASP.NET request span
  in Tempo carries `warp.endpoint.*` attributes. The ShipIt call has a `Client` span with `warp.adapter.*`
  attributes, and `SELECT count(*) FROM warp.adapter_call_log WHERE adapter_name = 'shipit'` does not grow.
  The `warp.adapter.calls` meter still counts every call.
- `Covered by:` `RecordingSinkWiringTests`, `SpanEnrichmentTests`, `OTelAggregateMetricsTests`,
  `OTelJobMetricsSinkTestsBase`.

Pitfall: an `Otel`-only endpoint sink writes no rows, so it produces no Issues. Keep `shop-api` on `Both` if you
want checkout 500s to become Issues.

## Endpoint observability

**In the shop.** Checkout is an ordinary ASP.NET `CheckoutController`, not a Warp HTTP endpoint. When it
returns 500s, the team wants them on the **Endpoints** page and as Issues, with the request that caused them.
The order-status polling action is called every few seconds by the SPA and should stay out.

**Wiring.** Register the recorder, install the middleware after `UseRouting`, and opt the controllers in.
`WithWarpObservability()` on the route builder, or `[WarpObserved]` (namespace `Warp.Http.Observability`) on a
controller or action. `[ExcludeFromWarpObservability]` (namespace `Warp.Core.Endpoints`) always wins.

```csharp
// shop-api: Program.cs
builder.Services.AddWarp<ShopDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.AddEndpointObservability(o =>
    {
        o.CaptureRequestBodies = CaptureMode.None;    // checkout bodies carry customer data
        o.CaptureResponseBodies = CaptureMode.OnFailure;
    });
});

var app = builder.Build();
app.UseRouting();
app.UseWarpHttpObservability();   // after UseRouting
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();             // only the opted-in controllers below are observed

// CheckoutController.cs
[WarpObserved]
[ApiController]
[Route("api/checkout")]
public class CheckoutController : ControllerBase { /* ... */ }

// OrdersController.cs
[WarpObserved]
[ApiController]
[Route("api/orders")]
public class OrdersController : ControllerBase
{
    [ExcludeFromWarpObservability]
    [HttpGet("{id}/status")]
    public IActionResult PollStatus(Guid id) { /* ... */ }
}
```

To observe every controller instead, use `app.MapControllers().WithWarpObservability();`.

**Test scenario.**

- *Given* the wiring above, and a temporary line in `CheckoutController` that throws
  `InvalidOperationException` when the cart contains SKU `TEST-500`.
- *When* you check out a cart with `TEST-500` three times, and poll `GET /api/orders/{id}/status` a few times.
- *Then* the **Endpoints** page lists `POST /api/checkout` with 3 failed calls and a non-zero error rate. The
  call drawer shows the status 500, the exception type and the related jobs (none for the failed calls).
  `GET /api/orders/{id}/status` does not appear. Within about a minute **Issues** shows one Endpoint issue,
  `InvalidOperationException` on `POST /api/checkout`, with count 3.
- `Covered by:` `HostEndpointObservabilityTestsBase`, `WarpEndpointIdentityFactoryTests`,
  `HostEndpointPipelineOrderTests`, `EndpointMiddlewareTests`, `EndpointFlusherTestsBase`.

Pitfall: the middleware must go after `UseRouting()`. Placed before an explicit `UseRouting()` it silently
observes nothing.

## Client observability

**In the shop.** `shop-web` is a single-page app. A broken cart script or a slow product page never reaches
the server logs. The team wants JavaScript errors and Core Web Vitals in the same dashboard, attributed to
`shop-web`, and linked to the server calls they made.

**Wiring.** `shop-api` hosts the ingest endpoint, and the SPA loads the shipped script:

```csharp
// shop-api
builder.Services.AddWarp<ShopDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.AddClientObservability(o =>
    {
        o.AddIngestKey("shop-web", "pk_live_northwind_web");   // public, write-only
        o.AllowedOrigins.Add("https://shop.northwind.example");
    });
});

var app = builder.Build();
app.UseRouting();
app.MapWarpClientObservability();   // POST /warp/ingest + GET /warp/ingest/client.js
```

```html
<script src="https://shop.northwind.example/warp/ingest/client.js"
        data-key="pk_live_northwind_web"
        data-release="1.4.2"></script>
```

**Test scenario.**

- *Given* the wiring above and the script on every `shop-web` page.
- *When* you open the storefront, browse two product pages, and run
  `setTimeout(() => { throw new Error('cart test 42') })` in the browser console. Then run
  `window.warp.track('cart.opened', { items: 3 })`.
- *Then* the **Client** page for `shop-web` shows the error in the event stream, the custom `cart.opened`
  event, and LCP / CLS / INP p75 tiles after the page hides. The error's detail has a stack and breadcrumbs.
  The session timeline lists the API requests of that visit with links to `/trace/{traceId}`. **Issues** gets
  one Client issue whose title is the normalized message `cart test <num>`.
- `Covered by:` `ClientIngestEndpointTests`, `ClientEventFlusherTestsBase`, `ClientEventQueryTestsBase`,
  `ClientEventKeysTests`, `ClientEventRecorderTests`, `ClientIngestMeterTests`, `ClientEventCleanupTestsBase`.

Pitfall: the ingest key is public by design. It only authorises writes. Never send customer ids or emails in
`track()` properties; consent for behavioural data is the shop's responsibility.

## Queue metrics

**In the shop.** Payment captures (`CapturePayment`, see [Reliability](./reliability.md#no-restart)) run on their own
`payments` queue in `shop-worker`. An order stuck in "capturing payment" is a queue that is not drained. The team wants to see how long payment jobs wait before a worker
picks them up, and how deep the queue is.

**Wiring.** Queue-wait and backlog are always on. Put the work on its own queue and serve it with a worker
group. A queue-wait SLO turns the signal into an objective (see [Metrics and SLOs](./metrics-and-slos.md)):

```csharp
// shop-worker, when an order ships: capture on the payments queue
await publisher.Enqueue(new CapturePayment { OrderId = order.Id }, "payments");

// shop-worker
builder.Services.AddWarpServer<ShopDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.AddWorkerGroup(g =>
    {
        g.Queues = ["payments"];
        g.WorkerCount = 4;
    });

    opt.AddSlo(o => o.AddObjective(
        SloKind.QueueWaitLatency, "payments", target: 30_000, percentile: 95, name: "Payments queue wait p95 < 30 s"));
});
```

**Test scenario.**

- *Given* `shop-worker` running with the `payments` group.
- *When* you pause the `shop-worker` server from the **Applications** page, ship 10 orders, wait two minutes,
  and resume it.
- *Then* while paused, the **Queues** family on the **Counters** page shows the `payments` row with backlog
  depth 10 and an oldest age that grows with each sample (60 s cadence). After resume, depth returns to 0 and
  the queue-wait p95 and p99 for `payments` read around two minutes. The SLO above reads **Breaching** on the
  **SLOs** page. `GET /warp/api/queues/metrics` returns the same numbers.
- `Covered by:` `QueueWaitClaimTestsBase`, `BacklogSamplerTestsBase`, `QueueMetricsQueryTestsBase`,
  `QueueWaitKeysTests`, `SloEvaluatorTestsBase`.

Pitfall: backlog is queue-global, not per application. Queue-wait per application has no histogram, so its
p95 / p99 read 0; use the global row for percentiles.

## Error grouping (Issues)

**In the shop.** When PayFast is slow, `AuthorisePayment` throws `PayFastTimeoutException` hundreds of times,
each with a different order id in the message. The team wants one issue, not hundreds of rows, with the
version that introduced it and a link to one failing trace.

**Wiring.** Issues are always on. Every failed job, observed 5xx/4xx, failed adapter call and browser error feeds
them. `ApplicationVersion` makes first-seen / last-seen versions useful:

```csharp
// shop-worker
builder.Services.AddWarpServer<ShopDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.ApplicationName = "shop-worker";
    opt.ApplicationVersion = builder.Configuration["Build:Version"];   // set by CI
    opt.ApplicationEnvironment = "production";
});
```

**Test scenario.**

- *Given* a test PayFast sandbox that times out for card number `4000 0000 0000 0119`.
- *When* you place three orders with that card.
- *Then* within about a minute **Issues** shows one Job issue `PayFastTimeoutException: Payment <num> timed
  out` with culprit `AuthorisePayment`, count 3 or more (retries count too), and first-seen version
  equal to the deployed build. **Resolve** it, place one more failing order, and the issue re-opens with a
  "regressed" badge; an `IssueRegressed` event reaches any registered notifier.
- `Covered by:` `ErrorFingerprintTests`, `ErrorGroupAggregatorTestsBase`,
  `ErrorGroupSingleWorkerIntegrationTestsBase`, `ErrorGroupDispatcherIntegrationTestsBase`,
  `ErrorGroupCommandTestsBase`, `ErrorGroupQueryTestsBase`, `ErrorGroupCleanupTestsBase`.

Pitfall: the issue title is normalized, but the raw sample is not. Never put a PAN or an email in an
exception message (§1.2).

## Operational notifications

**In the shop.** The ops Slack channel should hear about the things nobody watches a dashboard for: a
`shop-api` instance that disappeared, an SLO that started breaching, a webhook to a marketplace partner that
gave up.

**Wiring.** Implement `IWarpNotifier` and register it on `shop-worker`, the process that runs the server tasks
raising `InstanceDown` and `SloBreached`:

```csharp
public sealed class OpsSlackNotifier : IWarpNotifier
{
    private readonly IHttpClientFactory _httpClientFactory;

    public OpsSlackNotifier(IHttpClientFactory httpClientFactory) => _httpClientFactory = httpClientFactory;

    public async Task NotifyAsync(WarpOperationalEvent evt, CancellationToken ct)
    {
        var text = evt switch
        {
            InstanceDownEvent x => $"{(x.IsServer ? "Server" : "Instance")} of {x.ApplicationName} went down",
            SloBreachedEvent x => $"SLO '{x.Name}' breaching, budget left {x.BudgetRemaining:P0}",
            WebhookDeliveryExhaustedEvent x => $"Webhook {x.EventType} to {x.GroupName ?? x.Url} gave up",
            _ => evt.Message,
        };

        using var client = _httpClientFactory.CreateClient("ops-slack");
        await client.PostAsJsonAsync(string.Empty, new { text }, ct);
    }
}

// shop-worker
opt.AddNotifier<OpsSlackNotifier>();
```

**Test scenario.**

- *Given* the notifier registered on `shop-worker` and pointed at a test Slack channel.
- *When* you kill one `shop-api` instance with `kill -9` (no graceful stop).
- *Then* about two minutes later (`ApplicationInstanceStaleGrace`) the channel gets "Instance of shop-api went
  down". The **Applications** page shows the instance gone and a `StaleSwept` lifecycle entry. Repeat the
  [queue metrics](#queue-metrics) pause test and the channel gets one "SLO 'Payments queue wait p95 < 30 s'
  breaching" message, once per healthy-to-breaching edge.
- `Covered by:` `WarpNotifierDispatcherTests`, `NotifierDispatchTestsBase`,
  `NotifierPostCommitIntegrationTestsBase`, `SloEvaluatorTestsBase`.

Pitfall: notifications are not persisted. A crash between commit and dispatch, or Slack being down, drops the
alert. Notifiers are singletons: inject `IServiceScopeFactory`, never `ShopDbContext`.

## Applications and deploys

**In the shop.** `shop-api` runs three replicas and `shop-worker` two. The team wants one roster of every
process, alive or dead, and a record of each release: when `shop-api` 1.4.2 reached production and what it
replaced.

**Wiring.** Set the name and the CI build version on each service. A deploy is recorded the first time an
instance registers with a version not seen before for that application and environment:

```csharp
// shop-api (AddWarp) and shop-worker (AddWarpServer) alike
opt.ApplicationName = "shop-api";                                   // "shop-worker" in the worker
opt.ApplicationVersion = builder.Configuration["Build:Version"];   // CI sets Build__Version=1.4.2
opt.ApplicationEnvironment = "production";
```

**Test scenario.**

- *Given* `shop-api` 1.4.1 running in production.
- *When* you roll out 1.4.2 with three replicas starting at the same time.
- *Then* **Applications > shop-api** lists three live instances on 1.4.2 and the **Deploys** card shows one
  entry `1.4.1 → 1.4.2`. `SELECT count(*) FROM warp.application_deploy WHERE application = 'shop-api' AND
  version = '1.4.2'` returns 1. Rolling back to 1.4.1 adds no row. On the Metrics page, the deploy appears as a
  dashed marker labelled `1.4.2`.
- `Covered by:` `ApplicationDeployTestsBase`, `ApplicationRegistryTestsBase`, `ApplicationQueryTestsBase`,
  `ApplicationTracingTests`; UI: `deployModel.test.ts`.

Pitfall: without both `ApplicationName` and a non-blank `ApplicationVersion`, no deploy is recorded and the
verdict below returns 404.

## Post-deploy verdict

**In the shop.** After CI rolls out `shop-api`, the pipeline should wait ten minutes and fail the release if
checkout breaks. The answer comes from the [health board](./metrics-and-slos.md#health-board) tiles and SLOs
scoped to `shop-api`.

**Wiring.** No registration: the route is served by any `AddWarp` process with the dashboard, behind the
dashboard's authorization. A CI step after the deploy:

```bash
curl -fsS -H "Authorization: Bearer $WARP_CI_TOKEN" --get \
  "https://ops.northwind.example/warp/api/applications/verdict" \
  --data-urlencode "application=shop-api" \
  --data-urlencode "version=$GIT_TAG" \
  --data-urlencode "environment=production" \
  --data-urlencode "minWindowMinutes=10" | jq '{verdict, summary, settlesAt: .window.settlesAt}'
```

Poll while `verdict` is `pending`, fail the job on `fail`. The full script is in
[Calling it from CI](../features/applications.md#post-deploy-verdict).

**Test scenario.**

- *Given* board tiles scoped to `shop-api` (checkout success %, 5xx count, PayFast latency) and the
  checkout SLOs from [Metrics and SLOs](./metrics-and-slos.md).
- *When* you deploy a build of `shop-api` that throws on every checkout with SKU `TEST-500`, send a few such
  checkouts, and call the route at minute 2 and again at minute 11.
- *Then* the first call returns `"verdict": "pending"` with `window.settled: false`. The second returns
  `"verdict": "fail"`, `deploy.previousVersion` set to the old version, and a `checks` entry for the 5xx tile
  with `"state": "breaching"`. With an active acknowledged-noise annotation on that tile, the check reads
  `"state": "acknowledged"` and no longer fails the verdict. A version that never registered returns 404.
- `Covered by:` `DeployVerdictTestsBase`, `DeployVerdictMathTests`, `DeployVerdictQueryTests`; UI:
  `verdictModel.test.ts`.

Pitfall: unscoped board tiles count in every application's verdict. On the shared database, scope the
checkout tiles to `shop-api`, or a `shop-worker` deploy fails on a checkout problem.
