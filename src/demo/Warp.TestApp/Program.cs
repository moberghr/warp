using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Warp.Adapters.Http;
using Warp.Adapters.Refit;
using Warp.Adapters.Webhooks;
using Warp.Core;
using Warp.Core.Adapters;
using Warp.Core.BackgroundServices;
using Warp.Core.Concurrency;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Helper;
using Warp.Core.Retry;
using Warp.Core.Sagas;
using Warp.Demo.ServiceDefaults;
using Warp.Http;
using Warp.Http.Observability;
using Warp.Provider.PostgreSql;
using Warp.Test.Shared;
using Warp.Test.Shared.Entities;
using Warp.Test.Shared.Handlers.BackgroundServices;
using Warp.Test.Shared.Handlers.Sagas;
using Warp.Test.Shared.Shop;
using Warp.TestApp.Authentication;
using Warp.UI;
using Warp.UI.DashboardPush;
using Warp.UI.Extensions;
using Warp.UI.Extensions.Retry;
using Warp.UI.UIMiddleware;
using Warp.Worker;

var builder = WebApplication.CreateBuilder(args);

// Aspire service defaults — OTLP export so adapter/webhook spans + meters also appear in the Aspire
// dashboard's trace/metric views (in addition to the Warp dashboard's Adapters/Webhooks pages).
builder.AddServiceDefaults();

// The external shop-providers service base URL, injected by the Aspire AppHost (PartnerApi:BaseUrl).
// Both outbound adapters (payment gateway + shipping carrier) point at it; webhooks target its subscriber.
var providersBaseUrl = new Uri(ShopWebhooks.SubscriberBaseUrl(builder.Configuration));

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddServices(builder.Configuration);
builder.Services.AddWarpHttp();

builder.Services.AddDataProtection();
builder.Services.AddScoped<IWarpCredentialValidator, DemoCredentialValidator>();

// Webhook-password authorization demo. Proves a custom IAuthorizationRequirement +
// AuthorizationHandler composes with [Authorize(Policy = "WebhookPassword")] on a
// [WarpHttpPost] handler — see WebhookEcho in HttpEndpoints.cs and the README/curl
// snippet in WebhookAuthorization.cs. The permissive scheme exists so authentication
// always succeeds with an empty identity; the policy is the only gatekeeper.
builder.Services
    .AddAuthentication(PermissiveAuthHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, PermissiveAuthHandler>(PermissiveAuthHandler.SchemeName, _ => { });
builder.Services.AddSingleton<IAuthorizationHandler, WebhookPasswordAuthorizationHandler>();
builder.Services.AddAuthorization(opts => opts.AddPolicy(
    "WebhookPassword",
    policy => policy.AddRequirements(new WebhookPasswordRequirement("secret"))));

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

builder.Services.AddSingleton<IWarpUIExtension, RetryUIExtension>();
builder.Services.AddWarpServer<TestContext>(options =>
{
    options.UsePostgreSql();

    options.WorkerCount = 10;
    options.ServerName = "warp-demo-server";
    options.DefaultQueue = "default";

    // "fulfillment" is polled only by this app's workers (TestWorker polls "default"), so the order jobs
    // that call the adapters run here where the adapters are registered. AddWebhooks appends "warp:webhooks".
    options.Queues = ["a-critical", "b-default", "c-low", "default", ShopQueues.Fulfillment];
    options.PollingInterval = TimeSpan.FromMilliseconds(500);
    options.HealthCheckInterval = TimeSpan.FromSeconds(10);
    options.HealthCheckTimeout = TimeSpan.FromSeconds(30);
    options.JobExpirationTimeout = TimeSpan.FromMinutes(30);

    // Dispatcher batch-fetches and distributes jobs to workers, and (combined with
    // UseDatabasePush below) wakes instantly on JobEnqueued notifications instead of
    // waiting for the next poll. Without this, idle workers exponentially back off to
    // MaxPollingInterval (default 30s), so newly seeded jobs wait up to 30s for pickup.
    options.UseDispatcher = true;

    options.AddRetry(o => o.MaxRetries = 3);
    options.AddConcurrency();
    options.AddSagas();

    // Cross-server push backbone — also fans dashboard events from TestWorker to TestApp.
    options.UseDatabasePush();

    // Realtime dashboard push — replaces polling on the dashboard with SignalR push.
    options.AddDashboardPush();

    // Second worker group — different queues and polling
    options.AddWorkerGroup(group =>
    {
        group.WorkerCount = 3;
        group.Queues = ["reports", "analytics"];
        group.PollingInterval = TimeSpan.FromSeconds(5);
    });

    // Demo background services — visible under /warp/services on the dashboard. The first
    // service runs once on every host (per-server scope). The second uses singleton scope —
    // one host across the cluster holds the lease and reports job stats every 10 seconds.
    // Watch the lease panel on the detail page to see which host currently holds it.
    options.AddBackgroundService<TickCounterService>();
    options.AddBackgroundService<JobStatsLoggerService>();

    // Shop cluster-singleton service — logs SKUs below the reorder threshold as orders deplete stock.
    options.AddBackgroundService<LowStockMonitor>();

    // === Outbound adapters — one adapter per external VENDOR (its own health + rate-limit boundary) ===
    // Each payment provider and each shipping carrier is a genuinely different dependency, so each is its
    // own adapter (stripe/paypal/adyen; ups/fedex/dhl). The GROUP axis is the storefront CHANNEL the order
    // came through (web/mobile/marketplace) — same vendor, sliced by who the call is on behalf of — so the
    // per-Channel table shows e.g. marketplace's higher fraud-decline rate across every payment vendor.
    // Diagnosis then reads off the axes: an operation red across channels = a caller bug; a channel red
    // across operations = that storefront's problem; a whole adapter red = that vendor is down.
    // (Real vendors would each have a distinct base URL; the demo points them all at one mock partner and
    //  lets the adapter identity do the modelling.)

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
    options.AddWebhooks(w => w.OnDeliveryExhausted<OrderWebhookExhaustedHandler>());

    // === Inbound endpoint observability — who calls OUR Warp HTTP endpoints (the inbound mirror of adapters) ===
    // Records IP / user-agent / user + duration + status per request to MapWarpHttp endpoints. Request bodies
    // always captured (demo), response bodies on failure. Group by user-agent family so the per-caller table
    // shows a real browser/curl/other split without a high-cardinality dimension.
    options.AddEndpointObservability(o =>
    {
        // Demo: capture everything so the dashboard call drawer shows full request/response headers + bodies.
        // A real app keeps these at OnFailure (§1.2). SampleRate stays 1.0 here so every call shows up.
        o.CaptureRequestBodies = CaptureMode.Always;
        o.CaptureResponseBodies = CaptureMode.Always;
        o.CaptureHeaders = CaptureMode.Always;

        // Group by user-agent family so the per-caller table shows a real browser/curl/other split without
        // a high-cardinality dimension.
        o.GroupSelector = ctx =>
        {
            var ua = ctx.Request.Headers.UserAgent.ToString();
            if (ua.Contains("curl", StringComparison.OrdinalIgnoreCase))
            {
                return "curl";
            }

            if (ua.Contains("Mozilla", StringComparison.OrdinalIgnoreCase))
            {
                return "browser";
            }

            return string.IsNullOrEmpty(ua) ? null : "other";
        };

        // Custom enrichment — attach free-form tags (shown in the call drawer). A real app would put a user
        // id / tenant here; the demo reads an optional X-Client-Id header and always records the scheme.
        o.Enrich = (ctx, tags) =>
        {
            var clientId = ctx.Request.Headers["X-Client-Id"].ToString();
            if (!string.IsNullOrEmpty(clientId))
            {
                tags["clientId"] = clientId;
            }

            tags["scheme"] = ctx.Request.Scheme;
        };
    });
});
builder.Services.AddSagaHandler<OrderSagaWorkflow>();

var app = builder.Build();

await Migrate();
await RegisterShopRecurringJobs();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Explicit routing so the inbound observability middleware (below) sees the matched endpoint + its
// WarpEndpointIdentity — it no-ops for anything that isn't a MapWarpHttp endpoint (dashboard, controllers).
app.UseRouting();
app.UseWarpHttpObservability();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseWarpUI(options => options.UseBuiltInLogin<DemoCredentialValidator>());
app.MapControllers();
app.MapWarpHttp();

// Seed endpoint — creates a realistic demo workload
var seedQueues = new[] { "a-critical", "b-default", "c-low" };

app.MapPost("/seed", async (IPublisher publisher, IBatchPublisher batchPublisher, IRecurringJobPublisher recurringPublisher, TestContext context) =>
{
    var random = new Random();
    var queues = seedQueues;

    // === Jobs across queues (fast, will complete quickly) ===
    for (var i = 0; i < 300; i++)
    {
        var queue = queues[random.Next(queues.Length)];
        await publisher.Enqueue(new OrderConfirmationRequest { EmailLogId = 1 }, queue);
    }

    // === Register jobs (each spawns child jobs inside handler — creates traces) ===
    for (var i = 0; i < 50; i++)
    {
        var queue = queues[random.Next(queues.Length)];
        await publisher.Enqueue(new CustomerSignupRequest { Email = $"user{i}@test.com" }, queue);
    }

    // === Scheduled jobs (some past, some future) ===
    for (var i = 0; i < 50; i++)
    {
        var offset = random.Next(-60, 120);
        await publisher.Schedule(
            new CustomerSignupRequest { Email = $"scheduled{i}@test.com" },
            DateTime.UtcNow.AddSeconds(offset));
    }

    // === Failing jobs (no retries — go straight to Failed) ===
    for (var i = 0; i < 30; i++)
    {
        await publisher.Enqueue(new ThrowExceptionRequest(), queues[random.Next(queues.Length)]);
    }

    // === Failing jobs with retries (shows retry lifecycle) ===
    for (var i = 0; i < 10; i++)
    {
        await publisher.Enqueue(new ThrowExceptionRequest(), new JobParameters { Queue = queues[random.Next(queues.Length)] }.Configure<IRetryMetadata>(m => m.MaxRetries = 3));
    }

    // === Continuations (parent → child chains) ===
    for (var i = 0; i < 10; i++)
    {
        var parentId = await publisher.Enqueue(new CustomerSignupRequest { Email = $"parent{i}@test.com" });
        await publisher.Enqueue(new CustomerSignupRequest { Email = $"child{i}@test.com" }, parentId);
    }

    // === Slow job with awaiting children (visible for 30s) ===
    var slowJobId = await publisher.Enqueue(new SlowRequest());
    for (var i = 0; i < 5; i++)
    {
        await publisher.Enqueue(new OrderConfirmationRequest { EmailLogId = 1 }, slowJobId);
    }

    // === Messages (pub/sub — each routes to multiple handlers) ===
    for (var i = 0; i < 10; i++)
    {
        await publisher.Publish(new OrderNotification());
    }

    // === Batch: 15 jobs → continuation batch of 8 (OnlyOnSucceeded, default) ===
    var batchJobs = Enumerable.Range(0, 15)
        .Select(_ => new OrderConfirmationRequest { EmailLogId = 1 }).ToList();
    var batchId = await batchPublisher.StartNew(batchJobs);
    var batch2Jobs = Enumerable.Range(0, 8)
        .Select(_ => new OrderConfirmationRequest { EmailLogId = 1 }).ToList();
    await batchPublisher.ContinueBatchWith(batch2Jobs, batchId);

    // === Batch with OnAnyFinishedState (continuation fires even if some fail) ===
    // Can't mix types in BatchPublisher, so use OrderConfirmationRequest for success batch
    var failBatchJobs = Enumerable.Range(0, 5)
        .Select(_ => new ThrowExceptionRequest()).ToList();
    var failBatchId = await batchPublisher.StartNew(failBatchJobs, options: ContinuationOptions.OnAnyFinishedState);
    var afterFailBatchJobs = Enumerable.Range(0, 3)
        .Select(_ => new OrderConfirmationRequest { EmailLogId = 1 }).ToList();
    await batchPublisher.ContinueBatchWith(afterFailBatchJobs, failBatchId);

    // === Complex flow: ProcessOrder → batch of ShipItem → PublishInvoice → InvoiceNotification message ===
    for (var i = 0; i < 5; i++)
    {
        await publisher.Enqueue(new ProcessOrderRequest { OrderId = $"ORD-{1000 + i}" });
    }

    // === Mutex jobs (same key — first holds mutex with slow handler, rest get cancelled) ===
    // Uses a-critical queue so these are picked up before the 300+ other jobs
    await publisher.Enqueue(
        new SlowRequest(),
        new JobParameters { Queue = "a-critical", }.WithMutex("payment:customer-42"));
    for (var i = 0; i < 4; i++)
    {
        await publisher.Enqueue(
            new OrderConfirmationRequest { EmailLogId = 1 },
            new JobParameters { Queue = "a-critical", }.WithMutex("payment:customer-42"));
    }

    // === Multiple continuation fan-out (parent → 3 continuations) ===
    var fanOutParentId = await publisher.Enqueue(new CustomerSignupRequest { Email = "fanout-parent@test.com" });
    await publisher.Enqueue(new OrderConfirmationRequest { EmailLogId = 1 }, fanOutParentId);
    await publisher.Enqueue(new OrderConfirmationRequest { EmailLogId = 1 }, fanOutParentId);
    await publisher.Enqueue(new CustomerSignupRequest { Email = "fanout-child@test.com" }, fanOutParentId);

    // === Job → Batch (7 jobs) → Batch (3 jobs) chain ===
    var chainJobId = await publisher.Enqueue(new CustomerSignupRequest { Email = "chain-start@test.com" });
    var chainBatch1Jobs = Enumerable.Range(0, 7)
        .Select(_ => new OrderConfirmationRequest { EmailLogId = 1 }).ToList();
    var chainBatch1Id = await batchPublisher.ContinueBatchWith(chainBatch1Jobs, chainJobId, "chain-batch-7");
    var chainBatch2Jobs = Enumerable.Range(0, 3)
        .Select(_ => new OrderConfirmationRequest { EmailLogId = 1 }).ToList();
    await batchPublisher.ContinueBatchWith(chainBatch2Jobs, chainBatch1Id, "chain-batch-3");

    // === Batch with mixed success/failure (shows green/red progress bar) ===
    var mixedBatchJobs = new List<OrderConfirmationRequest>();
    for (var i = 0; i < 10; i++)
    {
        mixedBatchJobs.Add(new OrderConfirmationRequest { EmailLogId = 1 });
    }

    await batchPublisher.StartNew(mixedBatchJobs, "mixed-result-batch");

    // === Named batch (type column won't be null) ===
    var namedBatchJobs = Enumerable.Range(0, 5)
        .Select(_ => new OrderConfirmationRequest { EmailLogId = 1 }).ToList();
    await batchPublisher.StartNew(namedBatchJobs, "email-campaign-batch");

    // === Cancellable job (long-running 30s, cancel from UI to see "Cancelling..." badge) ===
    await publisher.Enqueue(new SlowRequest(), queue: "c-low");

    // === Recurring jobs ===
    await recurringPublisher.AddOrUpdateRecurringJob(
        new OrderConfirmationRequest { EmailLogId = 1 }, "send-daily-report", "0 9 * * *");
    await recurringPublisher.AddOrUpdateRecurringJob(
        new OrderConfirmationRequest { EmailLogId = 1 }, "cleanup-hourly", "0 * * * *");
    await recurringPublisher.AddOrUpdateRecurringJob(
        new OrderConfirmationRequest { EmailLogId = 1 }, "every-minute", "* * * * *");

    await publisher.SaveChangesAsync();

    return Results.Ok(new
    {
        jobs = 300,
        registerJobs = 50,
        scheduled = 50,
        failing = 30,
        failingWithRetries = 10,
        continuations = 10,
        fanOutContinuations = 4,
        slowWithAwaiting = 6,
        messages = 10,
        orderFlows = 5,
        batches = 4,
        mutexJobs = 5,
        cancellableJobs = 1,
        recurringJobs = 3,
    });
});

// Seed endpoint — creates flow scenarios to test FlowCard UI and trace page
app.MapPost("/seed-flow", async (IPublisher publisher, IBatchPublisher batchPublisher, TestContext context) =>
{
    // 1. Simple standalone job (no relationships)
    var simpleJobId = await publisher.Enqueue(new OrderConfirmationRequest { EmailLogId = 1 });

    // 2. Simple failing job (shows retries + failed state)
    var failingJobId = await publisher.Enqueue(new ThrowExceptionRequest(), new JobParameters().Configure<IRetryMetadata>(m => m.MaxRetries = 2));

    // 3. Job → 3 continuation jobs (fan-out, creates trace via handler spawning)
    var fanOutId = await publisher.Enqueue(new CustomerSignupRequest { Email = "flow-parent@test.com" });
    await publisher.Enqueue(new OrderConfirmationRequest { EmailLogId = 1 }, fanOutId);
    await publisher.Enqueue(new OrderConfirmationRequest { EmailLogId = 2 }, fanOutId);
    await publisher.Enqueue(new CustomerSignupRequest { Email = "flow-child@test.com" }, fanOutId);

    // 4. Job → Batch(5) → Batch(3) chain
    var chainJobId = await publisher.Enqueue(new CustomerSignupRequest { Email = "chain-start@test.com" });
    var batch1Jobs = Enumerable.Range(0, 5).Select(_ => new OrderConfirmationRequest { EmailLogId = 1 }).ToList();
    var batch1Id = await batchPublisher.ContinueBatchWith(batch1Jobs, chainJobId, "chain-phase-1");
    var batch2Jobs = Enumerable.Range(0, 3).Select(_ => new OrderConfirmationRequest { EmailLogId = 1 }).ToList();
    var batch2Id = await batchPublisher.ContinueBatchWith(batch2Jobs, batch1Id, "chain-phase-2");

    // 5. Batch(8) → continuation Batch(4)
    var batchJobs = Enumerable.Range(0, 8).Select(_ => new OrderConfirmationRequest { EmailLogId = 1 }).ToList();
    var batchId = await batchPublisher.StartNew(batchJobs, "flow-batch");
    var contJobs = Enumerable.Range(0, 4).Select(_ => new OrderConfirmationRequest { EmailLogId = 1 }).ToList();
    var batchContId = await batchPublisher.ContinueBatchWith(contJobs, batchId, "flow-batch-cont");

    // 6. Message (pub/sub — spawns multiple child jobs with trace)
    var messageId = await publisher.Publish(new OrderNotification());

    // 7. ProcessOrder flow (complex: job → batch of ShipItem → PublishInvoice → InvoiceNotification message)
    var orderJobId = await publisher.Enqueue(new ProcessOrderRequest { OrderId = "FLOW-001" });

    // 8. Light flow (job that spawns 3 emails + batch of 4 + parent with 2 continuations — good for trace testing)
    var lightFlowId = await publisher.Enqueue(new LightFlowRequest());

    // 9. Mutex jobs (same key — first holds mutex, rest cancelled)
    var mutexId1 = await publisher.Enqueue(
        new SlowRequest(),
        new JobParameters { Queue = "a-critical", }.WithMutex("test-mutex"));
    var mutexId2 = await publisher.Enqueue(
        new OrderConfirmationRequest { EmailLogId = 1 },
        new JobParameters { Queue = "a-critical", }.WithMutex("test-mutex"));

    await publisher.SaveChangesAsync();

    // Return all IDs for easy testing
    return Results.Ok(new
    {
        links = new
        {
            simpleJob = $"/warp/detail/{simpleJobId}",
            failingJob = $"/warp/detail/{failingJobId}",
            fanOutJob = $"/warp/detail/{fanOutId}",
            fanOutTrace = $"/warp/trace/{fanOutId}",
            chainJob = $"/warp/detail/{chainJobId}",
            chainTrace = $"/warp/trace/{chainJobId}",
            batch1 = $"/warp/detail/{batch1Id}",
            batch2 = $"/warp/detail/{batch2Id}",
            batchStandalone = $"/warp/detail/{batchId}",
            batchCont = $"/warp/detail/{batchContId}",
            batchTrace = $"/warp/trace/{batchId}",
            message = $"/warp/detail/{messageId}",
            orderJob = $"/warp/detail/{orderJobId}",
            orderTrace = $"/warp/trace/{orderJobId}",
            lightFlow = $"/warp/detail/{lightFlowId}",
            lightFlowTrace = $"/warp/trace/{lightFlowId}",
            mutexHolder = $"/warp/detail/{mutexId1}",
            mutexCancelled = $"/warp/detail/{mutexId2}",
        },
    });
});

// Individual seed endpoints — each HTTP request gets its own Activity trace
app.MapPost("/seed/simple-job", async (IPublisher publisher) =>
{
    var id = await publisher.Enqueue(new OrderConfirmationRequest { EmailLogId = 1 });
    await publisher.SaveChangesAsync();
    return Results.Ok(new { detail = $"/warp/detail/{id}" });
});

app.MapPost("/seed/failing-job", async (IPublisher publisher) =>
{
    var id = await publisher.Enqueue(new ThrowExceptionRequest(), new JobParameters().Configure<IRetryMetadata>(m => m.MaxRetries = 2));
    await publisher.SaveChangesAsync();
    return Results.Ok(new { detail = $"/warp/detail/{id}" });
});

app.MapPost("/seed/fan-out", async (IPublisher publisher, TestContext context) =>
{
    var parentId = await publisher.Enqueue(new CustomerSignupRequest { Email = "flow-parent@test.com" });
    await publisher.Enqueue(new OrderConfirmationRequest { EmailLogId = 1 }, parentId);
    await publisher.Enqueue(new OrderConfirmationRequest { EmailLogId = 2 }, parentId);
    await publisher.Enqueue(new CustomerSignupRequest { Email = "flow-child@test.com" }, parentId);
    await publisher.SaveChangesAsync();
    var traceId = await context.Set<Job>().Where(x => x.Id == parentId).Select(x => x.TraceId).FirstAsync();
    return Results.Ok(new { detail = $"/warp/detail/{parentId}", trace = $"/warp/trace/{traceId:N}" });
});

app.MapPost("/seed/chain", async (IPublisher publisher, IBatchPublisher batchPublisher, TestContext context) =>
{
    var jobId = await publisher.Enqueue(new CustomerSignupRequest { Email = "chain-start@test.com" });
    var batch1Jobs = Enumerable.Range(0, 5).Select(_ => new OrderConfirmationRequest { EmailLogId = 1 }).ToList();
    var batch1Id = await batchPublisher.ContinueBatchWith(batch1Jobs, jobId, "chain-phase-1");
    var batch2Jobs = Enumerable.Range(0, 3).Select(_ => new OrderConfirmationRequest { EmailLogId = 1 }).ToList();
    var batch2Id = await batchPublisher.ContinueBatchWith(batch2Jobs, batch1Id, "chain-phase-2");
    await publisher.SaveChangesAsync();
    var traceId = await context.Set<Job>().Where(x => x.Id == jobId).Select(x => x.TraceId).FirstAsync();
    return Results.Ok(new { detail = $"/warp/detail/{jobId}", batch1 = $"/warp/detail/{batch1Id}", batch2 = $"/warp/detail/{batch2Id}", trace = $"/warp/trace/{traceId:N}" });
});

app.MapPost("/seed/batch", async (IBatchPublisher batchPublisher, TestContext context) =>
{
    var batchJobs = Enumerable.Range(0, 8).Select(_ => new OrderConfirmationRequest { EmailLogId = 1 }).ToList();
    var batchId = await batchPublisher.StartNew(batchJobs, "flow-batch");
    var contJobs = Enumerable.Range(0, 4).Select(_ => new OrderConfirmationRequest { EmailLogId = 1 }).ToList();
    var contId = await batchPublisher.ContinueBatchWith(contJobs, batchId, "flow-batch-cont");
    await batchPublisher.SaveChangesAsync();
    var traceId = await context.Set<Job>().Where(x => x.Id == batchId).Select(x => x.TraceId).FirstAsync();
    return Results.Ok(new { detail = $"/warp/detail/{batchId}", cont = $"/warp/detail/{contId}", trace = $"/warp/trace/{traceId:N}" });
});

app.MapPost("/seed/message", async (IPublisher publisher, TestContext context) =>
{
    var id = await publisher.Publish(new OrderNotification());
    await publisher.SaveChangesAsync();
    var traceId = await context.Set<Job>().Where(x => x.Id == id).Select(x => x.TraceId).FirstAsync();
    return Results.Ok(new { detail = $"/warp/detail/{id}", trace = $"/warp/trace/{traceId:N}" });
});

app.MapPost("/seed/order-flow", async (IPublisher publisher, TestContext context) =>
{
    var id = await publisher.Enqueue(new ProcessOrderRequest { OrderId = "FLOW-001" });
    await publisher.SaveChangesAsync();
    var traceId = await context.Set<Job>().Where(x => x.Id == id).Select(x => x.TraceId).FirstAsync();
    return Results.Ok(new { detail = $"/warp/detail/{id}", trace = $"/warp/trace/{traceId:N}" });
});

app.MapPost("/seed/light-flow", async (IPublisher publisher, TestContext context) =>
{
    var id = await publisher.Enqueue(new LightFlowRequest());
    await publisher.SaveChangesAsync();
    var traceId = await context.Set<Job>().Where(x => x.Id == id).Select(x => x.TraceId).FirstAsync();
    return Results.Ok(new { detail = $"/warp/detail/{id}", trace = $"/warp/trace/{traceId:N}" });
});

app.MapPost("/seed/sagas", async (IPublisher publisher) =>
{
    // Start three sagas and feed them in different orders to demo the dashboard:
    //   ORD-S-001: ordered (OrderPlaced → PaymentCaptured → InventoryReserved) — completes
    //   ORD-S-002: payment first, inventory later — completes via the same handler
    //   ORD-S-003: OrderPlaced only — stays open, waiting for the timeout to compensate
    await publisher.Publish(new OrderPlaced { OrderId = "ORD-S-001" });
    await publisher.Publish(new PaymentCaptured { OrderId = "ORD-S-001" });
    await publisher.Publish(new InventoryReserved { OrderId = "ORD-S-001" });

    await publisher.Publish(new OrderPlaced { OrderId = "ORD-S-002" });
    await publisher.Publish(new PaymentCaptured { OrderId = "ORD-S-002" });
    await publisher.Publish(new InventoryReserved { OrderId = "ORD-S-002" });

    await publisher.Publish(new OrderPlaced { OrderId = "ORD-S-003" });

    await publisher.SaveChangesAsync();

    return Results.Ok(new
    {
        sagas = "/warp/sagas",
        completed = "ORD-S-001 + ORD-S-002",
        pending = "ORD-S-003 sticks around for 1h (OrderTimeout delay); use the dashboard's Force complete button to exercise compensation early",
    });
});

app.MapPost("/seed/mutex", async (IPublisher publisher) =>
{
    var id1 = await publisher.Enqueue(new SlowRequest(), new JobParameters { Queue = "a-critical", }.WithMutex("test-mutex"));
    var id2 = await publisher.Enqueue(new OrderConfirmationRequest { EmailLogId = 1 }, new JobParameters { Queue = "a-critical", }.WithMutex("test-mutex"));
    await publisher.SaveChangesAsync();
    return Results.Ok(new { holder = $"/warp/detail/{id1}", cancelled = $"/warp/detail/{id2}" });
});

// === Shop demo — checkout drives the full order flow through the vendor adapters + both webhooks ===
// Each order: charge (the order's payment-provider adapter, group = storefront channel) → order.paid
// webhook → ship (the order's carrier adapter, group = channel) → order.shipped webhook. Watch
// /warp/adapters and /warp/webhooks. The catalog SKUs are seeded at startup.
var shopSkus = new[] { "SKU-TEE", "SKU-MUG", "SKU-CAP", "SKU-BAG", "SKU-PEN" };
var shopPrices = new Dictionary<string, decimal>(StringComparer.Ordinal)
{
    ["SKU-TEE"] = 24.99m, ["SKU-MUG"] = 12.50m, ["SKU-CAP"] = 19.00m, ["SKU-BAG"] = 39.90m, ["SKU-PEN"] = 3.25m,
};
var subscriberMix = new[]
{
    ShopProviders.ReliableSubscriber,
    ShopProviders.ReliableSubscriber,
    ShopProviders.FlakySubscriber,
    ShopProviders.FlakySubscriber,
    ShopProviders.DownSubscriber,
};

// Place N orders. Each becomes a Pending ShopOrder + a fulfillment job that runs the whole flow.
app.MapPost("/shop/checkout", async (IPublisher publisher, TestContext context, int? count) =>
{
    var random = new Random();
    var placed = count ?? 25;

    for (var i = 0; i < placed; i++)
    {
        var sku = shopSkus[random.Next(shopSkus.Length)];
        var order = new ShopOrder
        {
            Sku = sku,
            Provider = ShopProviders.Payment[random.Next(ShopProviders.Payment.Length)],
            Carrier = ShopProviders.Carriers[random.Next(ShopProviders.Carriers.Length)],
            Channel = ShopProviders.Channels[random.Next(ShopProviders.Channels.Length)],
            Amount = shopPrices[sku],
            Status = "Pending",
            CreatedAt = DateTime.UtcNow,
        };
        context.Orders.Add(order);
        await publisher.Enqueue(new PlaceOrderRequest { OrderId = order.Id }, ShopQueues.Fulfillment);
    }

    await publisher.SaveChangesAsync();

    return Results.Ok(new
    {
        placed,
        flow = "charge → order.paid → ship → order.shipped",
        adaptersDashboard = "/warp/adapters",
        webhooksDashboard = "/warp/webhooks",
    });
});

// === Single-shot GET triggers (browser-clickable) — fire one thing and watch the UI ===

// Place one order through the full flow. ?provider= (stripe/paypal/adyen) picks the payment adapter,
// ?carrier= (ups/fedex/dhl) picks the shipping adapter, ?channel= (web/mobile/marketplace) the group.
app.MapGet("/trigger/order", async (IPublisher publisher, TestContext context, string? provider, string? carrier, string? channel) =>
{
    var sku = shopSkus[Random.Shared.Next(shopSkus.Length)];
    var order = new ShopOrder
    {
        Sku = sku,
        Provider = provider ?? "stripe",
        Carrier = carrier ?? "ups",
        Channel = channel ?? "web",
        Amount = shopPrices[sku],
        Status = "Pending",
        CreatedAt = DateTime.UtcNow,
    };
    context.Orders.Add(order);
    await publisher.Enqueue(new PlaceOrderRequest { OrderId = order.Id }, ShopQueues.Fulfillment);
    await publisher.SaveChangesAsync();

    return Results.Ok(new { orderId = order.Id, order.Sku, order.Provider, order.Carrier, order.Channel, watch = $"/warp/adapters/{order.Provider}" });
});

// One shipping rate quote through a carrier's Refit adapter (GetRate operation, channel group).
app.MapGet("/trigger/rate", async (IUpsShipping ups, IFedExShipping fedex, IDhlShipping dhl, string? carrierName, string? sku, string? channel) =>
{
    var name = carrierName ?? "dhl";
    var group = channel ?? "web";
    IShippingApi carrier = name switch
    {
        "fedex" => fedex,
        "ups" => ups,
        _ => dhl,
    };

    RateQuote quote;
    using (WarpAdapterCall.Group(group))
    {
        quote = await carrier.GetRate(sku ?? "SKU-TEE", name, group, CancellationToken.None);
    }

    return Results.Ok(new { quote.Sku, quote.Carrier, quote.Price, channel = group, watch = $"/warp/adapters/{name}" });
});

// One durable webhook to a chosen subscriber. ?subscriber=reliable|flaky|down → Delivered /
// retry-then-settle / Exhausted. Returns the delivery id to watch on /warp/webhooks.
app.MapGet("/trigger/webhook", async (IWebhookDispatcher dispatcher, IConfiguration configuration, string? subscriber) =>
{
    var target = subscriber switch
    {
        "flaky" => ShopProviders.FlakySubscriber,
        "down" => ShopProviders.DownSubscriber,
        _ => ShopProviders.ReliableSubscriber,
    };
    var send = ShopWebhooks.Build(configuration, "order.shipped", target, $"ORD-{Guid.NewGuid().ToString("N")[..6]}");
    var deliveryId = await dispatcher.SendAsync(send);

    return Results.Ok(new
    {
        deliveryId,
        subscriber = target,
        watch = $"/warp/webhooks/{deliveryId}",
        subscriberInbox = $"{ShopWebhooks.SubscriberBaseUrl(configuration)}/subscriber",
    });
});

// Bulk webhook variety — a spread across reliable/flaky/down subscribers so the Webhooks dashboard shows
// Delivered, retry-then-settle, and Exhausted (+ the host exhausted-callback) together.
app.MapPost("/seed/webhooks", async (IWebhookDispatcher dispatcher, IConfiguration configuration) =>
{
    var ids = new List<Guid>();

    for (var i = 0; i < subscriberMix.Length; i++)
    {
        var send = ShopWebhooks.Build(configuration, "order.shipped", subscriberMix[i], $"ORD-{3000 + i}");
        ids.Add(await dispatcher.SendAsync(send));
    }

    return Results.Ok(new
    {
        delivered = ids.Count,
        subscribers = new { reliable = 2, flaky = 2, down = 1 },
        webhooksDashboard = "/warp/webhooks",
        subscriberInbox = $"{ShopWebhooks.SubscriberBaseUrl(configuration)}/subscriber",
    });
});

app.MapPost("/seed-perf", async (IPublisher publisher, int? count) =>
{
    var total = count ?? 10000;
    const int batchSize = 500;
    var created = 0;

    while (created < total)
    {
        var remaining = Math.Min(batchSize, total - created);
        for (var i = 0; i < remaining; i++)
        {
            await publisher.Enqueue(new EmptyRequest());
        }

        await publisher.SaveChangesAsync();
        created += remaining;
    }

    return Results.Ok(new { created });
});

app.MapGet("/seed-perf-batch", async (IBatchPublisher batchPublisher, int? jobsPerBatch, int? batchCount) =>
{
    var jobs = jobsPerBatch ?? 100;
    var batches = batchCount ?? 10;
    var totalJobs = 0;

    for (var b = 0; b < batches; b++)
    {
        var batchJobs = Enumerable.Range(0, jobs).Select(_ => new EmptyRequest()).ToList();
        await batchPublisher.StartNew(batchJobs);
        totalJobs += jobs;
    }

    await batchPublisher.SaveChangesAsync();
    return Results.Ok(new { batches, jobsPerBatch = jobs, totalJobs });
});

app.MapGet("/seed-perf-batch-continuation", async (IBatchPublisher batchPublisher, int? batchCount, int? jobsPerBatch1, int? jobsPerBatch2) =>
{
    var batches = batchCount ?? 100;
    var jobs1 = jobsPerBatch1 ?? 10;
    var jobs2 = jobsPerBatch2 ?? 100;

    for (var b = 0; b < batches; b++)
    {
        var firstBatchJobs = Enumerable.Range(0, jobs1).Select(_ => new EmptyRequest()).ToList();
        var batchId = await batchPublisher.StartNew(firstBatchJobs);

        var continuationJobs = Enumerable.Range(0, jobs2).Select(_ => new EmptyRequest()).ToList();
        await batchPublisher.ContinueBatchWith(continuationJobs, batchId);
    }

    await batchPublisher.SaveChangesAsync();
    return Results.Ok(new
    {
        batches,
        phase1Jobs = batches * jobs1,
        phase2Jobs = batches * jobs2,
        totalJobs = batches * (jobs1 + jobs2),
    });
});

app.MapGet("/seed-perf-messages", async (IPublisher publisher, int? count) =>
{
    var total = count ?? 100;
    for (var i = 0; i < total; i++)
    {
        await publisher.Publish(new EmptyMessage());
    }

    await publisher.SaveChangesAsync();
    return Results.Ok(new { messages = total, jobsPerMessage = 3, totalJobs = total * 3 });
});

app.MapGet("/perf-continuation-latency", async (TestContext context) =>
{
    // For each batch chain: measure time between last phase-1 child completing
    // and first phase-2 child starting to process
    var firstBatchIds = await context.Set<Job>()
        .Where(b => b.Kind == JobKind.Batch && b.ParentJobId == null)
        .Select(b => b.Id)
        .ToListAsync();

    var latencies = new List<double>();

    foreach (var batchId in firstBatchIds)
    {
        // Last completion time of first-phase children
        var lastChildCompleted = await context.Set<JobLog>()
            .Where(l => context.Set<Job>().Any(j => j.Id == l.JobId && j.ParentJobId == batchId && j.Kind == JobKind.Job))
            .Where(l => l.EventType == "Completed")
            .MaxAsync(l => (DateTime?)l.Timestamp);

        if (lastChildCompleted == null)
        {
            continue;
        }

        // Find continuation batch
        var contBatchId = await context.Set<Job>()
            .Where(j => j.ParentJobId == batchId && j.Kind == JobKind.Batch)
            .Select(j => j.Id)
            .FirstOrDefaultAsync();

        if (contBatchId == Guid.Empty)
        {
            continue;
        }

        // First processing time of continuation children
        var firstContProcessing = await context.Set<JobLog>()
            .Where(l => context.Set<Job>().Any(j => j.Id == l.JobId && j.ParentJobId == contBatchId && j.Kind == JobKind.Job))
            .Where(l => l.EventType == "Processing")
            .MinAsync(l => (DateTime?)l.Timestamp);

        if (firstContProcessing == null)
        {
            continue;
        }

        var latencyMs = (firstContProcessing.Value - lastChildCompleted.Value).TotalMilliseconds;
        latencies.Add(latencyMs);
    }

    if (latencies.Count == 0)
    {
        return Results.Ok(new { error = "No continuation chains found" });
    }

    latencies.Sort();
    return Results.Ok(new
    {
        chains = latencies.Count,
        avgMs = Math.Round(latencies.Average(), 1),
        minMs = Math.Round(latencies.Min(), 1),
        maxMs = Math.Round(latencies.Max(), 1),
        p50Ms = Math.Round(latencies[latencies.Count / 2], 1),
        p95Ms = Math.Round(latencies[(int)(latencies.Count * 0.95)], 1),
        p99Ms = Math.Round(latencies[(int)(latencies.Count * 0.99)], 1),
    });
});

app.MapPost("/perf-trace/enable", () =>
{
    Warp.Worker.PerfTrace.Enable();
    return Results.Ok("Perf tracing enabled");
});

app.MapPost("/perf-trace/disable", () =>
{
    Warp.Worker.PerfTrace.Disable();
    return Results.Ok("Perf tracing disabled");
});

app.MapGet("/perf-trace/dump", () =>
{
    var result = Warp.Worker.PerfTrace.Dump();
    return Results.Text(result);
});

await app.RunAsync();

async Task Migrate()
{
    await using var scope = app!.Services.CreateAsyncScope();
    var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();

    // Set WARP_DEMO_PRESERVE_DB=1 to skip the wipe (useful for multi-host demos where
    // another worker already has registered state in the DB).
    if (!string.Equals(Environment.GetEnvironmentVariable("WARP_DEMO_PRESERVE_DB"), "1", StringComparison.Ordinal))
    {
        await ctx.Database.EnsureDeletedAsync();
    }

    await ctx.Database.EnsureCreatedAsync();

    // Seed the shop catalog (some SKUs start below the reorder threshold so the low-stock monitor has
    // something to report immediately).
    if (!await ctx.Products.AnyAsync())
    {
        ctx.Products.AddRange(
            new Product { Sku = "SKU-TEE", Name = "T-Shirt", Stock = 40, Price = 24.99m },
            new Product { Sku = "SKU-MUG", Name = "Mug", Stock = 8, Price = 12.50m },
            new Product { Sku = "SKU-CAP", Name = "Cap", Stock = 3, Price = 19.00m },
            new Product { Sku = "SKU-BAG", Name = "Tote Bag", Stock = 25, Price = 39.90m },
            new Product { Sku = "SKU-PEN", Name = "Pen", Stock = 2, Price = 3.25m });
        await ctx.SaveChangesAsync();
    }
}

async Task RegisterShopRecurringJobs()
{
    await using var scope = app!.Services.CreateAsyncScope();
    var recurring = scope.ServiceProvider.GetRequiredService<IRecurringJobPublisher>();

    // Sales summary every 2 minutes; sweep pending/failed orders back into fulfillment every minute.
    await recurring.AddOrUpdateRecurringJob(new GenerateSalesReportRequest(), "shop-sales-report", "*/2 * * * *");
    await recurring.AddOrUpdateRecurringJob(new RetryPendingPaymentsRequest(), "shop-retry-payments", "* * * * *");
}

internal class DemoCredentialValidator : IWarpCredentialValidator
{
    public Task<bool> ValidateAsync(string username, string password)
    {
        return Task.FromResult(string.Equals(username, "admin", StringComparison.Ordinal) && string.Equals(password, "admin", StringComparison.Ordinal));
    }
}
