using Microsoft.Extensions.Hosting;
using Warp.Adapters.Http;
using Warp.Adapters.Refit;
using Warp.Core;
using Warp.Core.BackgroundServices;
using Warp.Core.Concurrency;
using Warp.Core.Enums;
using Warp.Core.RateLimit;
using Warp.Core.Retry;
using Warp.Core.Sagas;
using Warp.Core.Slo;
using Warp.Core.Timeout;
using Warp.Core.Webhooks;
using Warp.Demo.ServiceDefaults;
using Warp.Provider.PostgreSql;
using Warp.Test.Shared;
using Warp.Test.Shared.Handlers.BackgroundServices;
using Warp.Test.Shared.Handlers.Sagas;
using Warp.Test.Shared.Shop;
using Warp.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Aspire service defaults — OTLP export so the adapter/webhook Client spans + warp.adapter.* /
// warp.webhooks.* meters (now emitted HERE, where the adapters run) show up in the Aspire dashboard's
// trace/metric views, in addition to the Warp dashboard's own Adapters/Webhooks pages.
builder.AddServiceDefaults();

builder.Services.AddServices(builder.Configuration);

// The external shop-providers service base URL, injected by the Aspire AppHost (PartnerApi:BaseUrl).
// The outbound adapters (payment gateways + shipping carriers) all point at it; the shop webhooks
// this worker delivers target its subscriber. The adapters run HERE (not in the web app) — this is
// the demo's only server, so all job execution and every outbound service call happens on it.
var providersBaseUrl = new Uri(ShopWebhooks.SubscriberBaseUrl(builder.Configuration));

builder.Services.AddWarpServer<TestContext>(options =>
{
    options.UsePostgreSql();
    options.WorkerCount = 10;
    options.PollingInterval = TimeSpan.FromSeconds(5);

    // Multi-application observability (§8.23): a distinct application from the TestApp web host,
    // sharing the one TestContext DB. This worker is the demo's sole SERVER — TestApp is a
    // non-server publisher/dashboard — so it executes every job TestApp publishes and the
    // dashboard shows per-job-type execution metrics attributed to "warp-demo-worker".
    options.ApplicationName = "warp-demo-worker";
    options.ApplicationVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString();
    options.ApplicationEnvironment = builder.Environment.EnvironmentName;

    // Queues (correctness): this worker is the ONLY executor in the demo, so it must poll EVERY
    // queue anything in the cluster publishes to — otherwise those jobs never run. Coverage:
    //   a-critical / b-default / c-low  — the /seed workload and the mutex jobs
    //   default                         — DefaultQueue: unqueued jobs, messages, sagas, the
    //                                      ProcessOrder flow, continuations, and recurring jobs
    //   high                            — ScheduleCustomerSignup's handler enqueues here
    //   fulfillment                     — shop order jobs that drive the adapters + webhooks
    // The implicit default group also ALWAYS polls warp:webhooks (a Core feature, §8.20), so the
    // durable webhook deliveries TestApp stages via SendAsync are drained here without listing it.
    // The reports/analytics group below rounds out the set.
    options.Queues = ["a-critical", "b-default", "c-low", "default", "high", ShopQueues.Fulfillment];

    // Dispatcher batch-fetches and distributes jobs to workers, and (combined with UseDatabasePush
    // below) wakes instantly on JobEnqueued notifications instead of waiting for the next poll.
    options.UseDispatcher = true;

    // Execution-side addons — these apply where handlers actually run, which is here.
    options.AddRetry(o => o.MaxRetries = 3);
    options.AddConcurrency();

    // Ordering is load-bearing (§2.12): AddRetry BEFORE AddTimeout so retry's catch can see the
    // TimeoutException that Fail mode throws, and AddConcurrency BEFORE AddRateLimit so a mutex reject does
    // not burn a rate-limit token. Both were missing entirely until now, which meant [Timeout] and
    // [RateLimit] were silently inert in the demo — the attributes parsed, the addons never ran.
    options.AddRateLimit();
    options.AddTimeout();
    options.AddSagas();

    // SLO objectives (§8.31) — seeded from config, editable in the dashboard SLOs page. Queue-scoped so they
    // populate from the demo's job traffic without needing an assembly-qualified job type.
    options.AddSlo(o =>
    {
        o.AddObjective(SloKind.QueueWaitLatency, "default", target: 30_000, percentile: 95, name: "Default queue-wait p95 < 30s");
        o.AddObjective(SloKind.BacklogDepth, "default", target: 100, name: "Default backlog < 100");
    });

    // Push finalize/enqueue notifications so the dashboard (running in the non-server TestApp)
    // sees this worker's job lifecycle events. The DB-push listener lives in Warp.Core, so
    // TestApp's AddWarp process receives these cross-process events and drives realtime push.
    options.UseDatabasePush();

    // Second worker group — different queues and polling cadence.
    options.AddWorkerGroup(group =>
    {
        group.WorkerCount = 3;
        group.Queues = ["reports", "analytics"];
        group.PollingInterval = TimeSpan.FromSeconds(5);
    });

    // Demo background services — visible under /warp/services on the dashboard. TickCounterService
    // runs once on every host (per-server scope); JobStatsLoggerService and LowStockMonitor use
    // singleton scope (one host across the cluster holds the lease).
    options.AddBackgroundService<TickCounterService>();
    options.AddBackgroundService<JobStatsLoggerService>();

    // Shop cluster-singleton service — logs SKUs below the reorder threshold as orders deplete stock.
    options.AddBackgroundService<LowStockMonitor>();

    // Demo-only: stages a mix of failing jobs so the Issues page (§8.29 error grouping) has live errors to diagnose.
    options.AddBackgroundService<FaultInjectorService>();

    // === Outbound adapters — one adapter per external VENDOR (its own health + rate-limit boundary) ===
    // Each payment provider and each shipping carrier is a genuinely different dependency, so each is
    // its own adapter (stripe/paypal/adyen; ups/fedex/dhl). The GROUP axis is the storefront CHANNEL
    // the order came through (web/mobile/marketplace) — same vendor, sliced by who the call is on
    // behalf of — so the per-Channel table shows e.g. marketplace's higher fraud-decline rate across
    // every payment vendor. Diagnosis then reads off the axes: an operation red across channels = a
    // caller bug; a channel red across operations = that storefront's problem; a whole adapter red =
    // that vendor is down. (Real vendors would each have a distinct base URL; the demo points them all
    //  at one mock partner and lets the adapter identity do the modelling.)

    // Payment providers — named HTTP-client adapters. Bodies captured on failure only (payments carry
    // data — §1.2). Resilience retries transient errors; each vendor gets its OWN cluster-shared rate
    // limit (keyed by adapter name), a differentiator per-process Polly cannot provide.
    foreach (var provider in ShopProviders.Payment)
    {
        options.AddAdapter(provider, a =>
        {
            a.BaseUrl = providersBaseUrl;
            a.Recording.GroupLabel = "Channel";
            a.Recording.CaptureRequestBodies = CaptureMode.OnFailure;
            a.Recording.CaptureResponseBodies = CaptureMode.OnFailure;
            a.Recording.IncludeGroupInMetrics = true;
            a.UseResilience();
            a.UseSharedRateLimit(limit: 50, perSeconds: 10, AdapterRateLimitOverflow.Wait, maxWait: TimeSpan.FromSeconds(5));
        });
    }

    // Shipping carriers — Refit adapters (one marker interface each). Operation names come from the
    // interface methods (CreateShipment / GetRate); the storefront channel rides as the group.
    options.AddAdapter<IUpsShipping>("ups", ConfigureCarrier);
    options.AddAdapter<IFedExShipping>("fedex", ConfigureCarrier);
    options.AddAdapter<IDhlShipping>("dhl", ConfigureCarrier);

    void ConfigureCarrier(WarpAdapterHttpOptions a)
    {
        a.BaseUrl = providersBaseUrl;
        a.Recording.GroupLabel = "Channel";
        a.Recording.IncludeGroupInMetrics = true;
        a.UseResilience();
    }

    // === Durable outbound webhooks — order.paid / order.shipped delivered to subscribers, tracked to done ===
    // Webhook delivery/exhaustion executes on the worker (it drains warp:webhooks), so the host's
    // exhausted-delivery callback is registered here.
    options.AddWebhooks(w => w.OnDeliveryExhausted<OrderWebhookExhaustedHandler>());
});
builder.Services.AddSagaHandler<OrderSagaWorkflow>();

var host = builder.Build();

await host.RunAsync();
