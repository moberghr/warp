using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Warp.Core;
using Warp.Core.ClientObservability;
using Warp.Core.Concurrency;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Helper;
using Warp.Core.RateLimit;
using Warp.Core.Retry;
using Warp.Core.Timeout;
using Warp.Core.Webhooks;
using Warp.Demo.ServiceDefaults;
using Warp.Http;
using Warp.Http.ClientObservability;
using Warp.Http.Observability;
using Warp.Provider.PostgreSql;
using Warp.Test.Shared;
using Warp.Test.Shared.Entities;
using Warp.Test.Shared.Handlers.Sagas;
using Warp.Test.Shared.Shop;
using Warp.TestApp;
using Warp.TestApp.Authentication;
using Warp.UI;
using Warp.UI.DashboardPush;
using Warp.UI.Extensions;
using Warp.UI.Extensions.Retry;
using Warp.Worker;

var builder = WebApplication.CreateBuilder(args);

// This is a NON-server publisher: it references Warp.Test.Shared (for the request types it publishes
// and the inbound HTTP endpoint handlers it serves), so the source generator registers ALL of that
// assembly's handlers here — including the shop JOB handlers (e.g. PlaceOrderHandler) that this process
// never executes (no worker) and whose deps (the shipping-carrier adapters IUpsShipping/…) live only on
// the worker. The dev-default ValidateOnBuild eagerly tries to construct every registered service and
// would fail on those never-run handlers, so turn it off here (a publisher legitimately registers
// handlers it won't run). ValidateScopes stays on — captive-dependency detection is still wanted.
builder.Host.UseDefaultServiceProvider((_, o) =>
{
    o.ValidateScopes = true;
    o.ValidateOnBuild = false;
});

// Aspire service defaults — OTLP export so adapter/webhook spans + meters also appear in the Aspire
// dashboard's trace/metric views (in addition to the Warp dashboard's Adapters/Webhooks pages).
builder.AddServiceDefaults();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddServices(builder.Configuration);
builder.Services.AddWarpHttp();

// Built-in dashboard login: a real cookie authentication scheme plus the WarpDashboardLogin policy that
// RequireWarpDashboardLogin() applies below. The validator is registered as scoped for us, so it could
// resolve a DbContext and check credentials against the database.
builder.Services.AddWarpDashboard().AddBuiltInLogin<DemoCredentialValidator>();

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
builder.Services.AddWarp<TestContext>(options =>
{
    options.UsePostgreSql();

    // Timeout and rate limit must be registered on the PUBLISHER, not just the worker: each addon's
    // *PublishBehavior stamps the job's metadata from the request-type attribute at publish time (§8.8).
    // Without them here, [Timeout] and [RateLimit] parse but nothing is stamped, so the worker-side pipeline
    // sees no configuration and both addons are silently inert. Ordering per §2.12 (concurrency before
    // ratelimit; retry before timeout — neither of those two runs here, so only the pair order matters).
    options.AddRateLimit();
    options.AddTimeout();

    // Multi-application observability (§8.23): this is a NON-server process — publisher + dashboard host +
    // inbound HTTP endpoints, with NO worker. It shares the one TestContext database with the TestWorker,
    // which is the demo's sole server and does all job execution. Distinct ApplicationNames make the
    // dashboard Applications page show two apps and demonstrate the provenance-vs-execution split — jobs
    // published here carry Application="warp-demo-web" but are executed on "warp-demo-worker". As a
    // non-server AddWarp process it registers an ApplicationInstance (heartbeat) row, not a Server row.
    options.ApplicationName = "warp-demo-web";
    options.ApplicationVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString();
    options.ApplicationEnvironment = builder.Environment.EnvironmentName;

    // Cross-process push backbone. The DB-push notification listener lives in Warp.Core, so this
    // non-server process still receives the TestWorker's JobFinalized / MessageEnqueued events over
    // Postgres LISTEN/NOTIFY and drives realtime dashboard push — no worker required here.
    options.UseDatabasePush();

    // Realtime dashboard push — replaces polling on the dashboard with SignalR push.
    options.AddDashboardPush();

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

    // === Client (frontend) observability — errors / logs / web-vitals / custom events from the demo SPA ===
    // The demo page at /client-demo loads the shipped client.js with this DSN key; the key maps to its own
    // application ("warp-demo-spa"), so the browser shows as a third app on the Applications + Client pages.
    // The page is served same-origin, so no AllowedOrigins entry is needed.
    options.AddClientObservability(o =>
    {
        o.AddIngestKey("warp-demo-spa", "pk_demo_spa");
        o.CaptureRemoteIp = true;   // demo: surface the caller IP in the event drawer (PII — opt-in, §1.2)
    });
});

var app = builder.Build();

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
app.MapWarpUI().RequireWarpDashboardLogin();
app.MapControllers();
app.MapWarpHttp();

// Client (browser) observability: the public ingest endpoint + the shipped script, and a tiny demo SPA that
// exercises all four event types so the dashboard Client page shows real browser data end-to-end.
app.MapWarpClientObservability();
app.MapGet("/client-demo", () => Results.Content(ClientDemoPage.Html, "text/html; charset=utf-8"));

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
    var id = await publisher.Enqueue(new ThrowExceptionRequest(), new JobParameters().WithRetry(2));
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

// Semaphore in Wait mode — the counterpart to /seed/mutex. Where a Skip-mode mutex DELETES the surplus,
// a Wait-mode semaphore REQUEUES it, so this is the seed that produces stats:requeued-concurrency.
// Three jobs against a limit of one: two of them bounce and come back.
app.MapPost("/seed/semaphore", async (IPublisher publisher) =>
{
    var ids = new List<Guid>();
    for (var i = 0; i < 3; i++)
    {
        ids.Add(await publisher.Enqueue(
            new SlowRequest(),
            new JobParameters { Queue = "a-critical" }.WithSemaphore("demo-semaphore", 1)));
    }

    await publisher.SaveChangesAsync();

    return Results.Ok(new { jobs = ids.ConvertAll(x => $"/warp/detail/{x}") });
});

// Timeout in Delete mode — the handler sleeps 10s against a 1s budget, so the pipeline marks it Deleted
// with reason Timeout and AddRetry deliberately does not retry it (§8.7). Produces stats:deleted-timeout.
app.MapPost("/seed/timeout", async (IPublisher publisher) =>
{
    var id = await publisher.Enqueue(new TimeoutDemoRequest(), new JobParameters { Queue = "a-critical" });
    await publisher.SaveChangesAsync();

    return Results.Ok(new { detail = $"/warp/detail/{id}" });
});

// Rate limit in Wait mode — one start per minute, so the surplus is rescheduled rather than dropped.
// Produces stats:requeued-ratelimit.
app.MapPost("/seed/ratelimit", async (IPublisher publisher) =>
{
    var ids = new List<Guid>();
    for (var i = 0; i < 3; i++)
    {
        ids.Add(await publisher.Enqueue(new RateLimitDemoRequest(), new JobParameters { Queue = "a-critical" }));
    }

    await publisher.SaveChangesAsync();

    return Results.Ok(new { jobs = ids.ConvertAll(x => $"/warp/detail/{x}") });
});

// === Shop demo — checkout drives the full order flow through the vendor adapters + both webhooks ===
// Each order: charge (the order's payment-provider adapter, group = storefront channel) → order.paid
// webhook → ship (the order's carrier adapter, group = channel) → order.shipped webhook. Watch
// /warp/adapters and /warp/webhooks. The catalog SKUs are seeded at startup.
var shopSkus = new[] { "SKU-TEE", "SKU-MUG", "SKU-CAP", "SKU-BAG", "SKU-PEN" };
var shopPrices = new Dictionary<string, decimal>(StringComparer.Ordinal)
{
    ["SKU-TEE"] = 24.99m,
    ["SKU-MUG"] = 12.50m,
    ["SKU-CAP"] = 19.00m,
    ["SKU-BAG"] = 39.90m,
    ["SKU-PEN"] = 3.25m,
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

// Showcase seed for the live demo. It backfills 24 hours of hourly performance history for the adapters
// and endpoints, plus back-dated webhook deliveries, so the time-series graphs have real depth. Real
// traffic from the trigger routes still supplies the adapter definitions, lifetime totals, and
// percentiles; this only adds the historical bars and spread-out deliveries that now-only triggers cannot.
app.MapPost("/seed/showcase", async (TestContext ctx) =>
{
    var now = DateTime.UtcNow;

    string Hour(int hoursAgo) => now.AddHours(-hoursAgo).ToString("yyyy-MM-dd-HH", System.Globalization.CultureInfo.InvariantCulture);

    void AddCounter(string key, int value)
    {
        if (value > 0)
        {
            ctx.Set<Counter>().Add(new Counter { Key = key, Value = value });
        }
    }

    // Hourly history counters use the same layout the Adapter and Endpoint counter keys emit at runtime.
    void Series(string prefix, string id, int baseCalls, double errorFraction, int baseLatencyMs)
    {
        for (var h = 23; h >= 0; h--)
        {
            var swell = 1 + ((12 - Math.Abs(12 - (23 - h))) / 2);
            var calls = baseCalls + swell + ((h * 7) % 13);
            var failed = (int)Math.Round(calls * errorFraction * (0.3 + ((h % 4) * 0.4)));
            var success = calls - failed;
            var durationSum = calls * (baseLatencyMs + ((h * 13) % 55));
            var hour = Hour(h);
            AddCounter($"{prefix}:{id}:hist:success:{hour}", success);
            AddCounter($"{prefix}:{id}:hist:failed:{hour}", failed);
            AddCounter($"{prefix}:{id}:hist:dur:{hour}", durationSum);
        }
    }

    Series("adapter", "stripe", 30, 0.02, 70);
    Series("adapter", "paypal", 30, 0.04, 110);
    Series("adapter", "adyen", 30, 0.03, 90);
    Series("adapter", "ups", 30, 0.06, 180);
    Series("adapter", "fedex", 30, 0.05, 150);
    Series("adapter", "dhl", 30, 0.12, 240);

    Series("endpoint", "POST /http/queue-email", 25, 0.03, 12);
    Series("endpoint", "POST /http/orders", 25, 0.02, 8);
    Series("endpoint", "GET /http/feed", 25, 0.10, 20);

    string EventAt(int i) => (i % 6) switch
    {
        0 => "order.completed",
        1 => "order.shipped",
        2 => "invoice.finalized",
        3 => "invoice.payment_failed",
        4 => "customer.created",
        _ => "shipment.delivered",
    };
    string SubscriberAt(int i) => (i % 4) switch
    {
        0 => "https://hooks.acme.example/orders",
        1 => "https://hooks.globex.example/inbound",
        2 => "https://hooks.initech.example/ship",
        _ => "https://hooks.umbrella.example/billing",
    };

    var deliveries = 0;
    for (var h = 24; h >= 1; h--)
    {
        for (var k = 0; k < 3; k++)
        {
            var idx = (h * 3) + k;
            var status = (idx % 11, idx % 4) switch
            {
                (0, _) => WebhookDeliveryStatus.Exhausted,
                (_, 0) => WebhookDeliveryStatus.Pending,
                _ => WebhookDeliveryStatus.Delivered,
            };
            var attemptCount = status switch
            {
                WebhookDeliveryStatus.Exhausted => 4,
                _ => 1,
            };
            var created = now.AddHours(-h).AddMinutes(k * 17);
            var endpoint = SubscriberAt(idx);
            ctx.Set<WebhookDelivery>().Add(new WebhookDelivery
            {
                Id = Guid.NewGuid(),
                EventType = EventAt(idx),
                EventId = $"evt_{idx:x6}",
                Url = endpoint,
                GroupName = endpoint,
                Reference = $"sub_{2000 + idx}",
                PayloadJson = "{}",
                SigningMode = WebhookSigning.StandardWebhooks,
                RetrySchedule = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10), TimeSpan.FromHours(1)],
                Status = status,
                AttemptCount = attemptCount,
                CreatedAt = created,
                ExpireAt = created.AddDays(30),
            });
            deliveries++;
        }
    }

    await ctx.SaveChangesAsync();

    return Results.Ok(new { historyHours = 24, adapters = 6, endpoints = 3, webhookDeliveries = deliveries });
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
