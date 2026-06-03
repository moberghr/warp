using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Warp.Core;
using Warp.Core.BackgroundServices;
using Warp.Core.Concurrency;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Helper;
using Warp.Core.Retry;
using Warp.Core.Sagas;
using Warp.Http;
using Warp.Provider.PostgreSql;
using Warp.Test.Shared;
using Warp.Test.Shared.Handlers.BackgroundServices;
using Warp.Test.Shared.Handlers.Sagas;
using Warp.TestApp.Authentication;
using Warp.UI;
using Warp.UI.DashboardPush;
using Warp.UI.Extensions;
using Warp.UI.Extensions.Retry;
using Warp.UI.UIMiddleware;
using Warp.Worker;

// Pin the content root to the dll's location so the app boots from any cwd
// (Rider debugger, raw `dotnet path/to.dll`, Docker). Without this, ASP.NET
// defaults to `Directory.GetCurrentDirectory()` and appsettings.json silently
// resolves to empty, which makes the first Npgsql call throw "ConnectionString
// has not been initialized" before any logs are emitted.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

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
builder.Services.AddWarpWorker<TestContext>(options =>
{
    options.UsePostgreSql();

    options.WorkerCount = 10;
    options.ServerName = "warp-demo-server";
    options.DefaultQueue = "default";
    options.Queues = ["a-critical", "b-default", "c-low", "default"];
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
});
builder.Services.AddSagaHandler<OrderSagaWorkflow>();

var app = builder.Build();

// Refuse to run Migrate() if another instance is already bound to our HTTP port.
// Migrate() does DROP DATABASE warp WITH (FORCE) when WARP_DEMO_PRESERVE_DB is
// unset; a second start that crashes at port-binding would otherwise destroy
// the live instance's DB state before Kestrel ever fails. Bail out before the
// destructive step instead.
EnsurePortFree(5104);

await Migrate();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseWarpUI(options => options.UseBuiltInLogin<DemoCredentialValidator>());
app.MapControllers();
app.MapWarpHttp();

// Seed endpoint — creates a realistic demo workload
var seedQueues = new[] { "a-critical", "b-default", "c-low" };

app.MapGet("/seed", async (IPublisher publisher, IBatchPublisher batchPublisher, IRecurringJobPublisher recurringPublisher, TestContext context) =>
{
    var random = new Random();
    var queues = seedQueues;

    // === Jobs across queues (fast, will complete quickly) ===
    for (var i = 0; i < 300; i++)
    {
        var queue = queues[random.Next(queues.Length)];
        await publisher.Enqueue(new SendEmailRequest { EmailLogId = 1 }, queue);
    }

    // === Register jobs (each spawns child jobs inside handler — creates traces) ===
    for (var i = 0; i < 50; i++)
    {
        var queue = queues[random.Next(queues.Length)];
        await publisher.Enqueue(new RegisterRequest { Email = $"user{i}@test.com" }, queue);
    }

    // === Scheduled jobs (some past, some future) ===
    for (var i = 0; i < 50; i++)
    {
        var offset = random.Next(-60, 120);
        await publisher.Schedule(
            new RegisterRequest { Email = $"scheduled{i}@test.com" },
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
        var parentId = await publisher.Enqueue(new RegisterRequest { Email = $"parent{i}@test.com" });
        await publisher.Enqueue(new RegisterRequest { Email = $"child{i}@test.com" }, parentId);
    }

    // === Slow job with awaiting children (visible for 30s) ===
    var slowJobId = await publisher.Enqueue(new SlowRequest());
    for (var i = 0; i < 5; i++)
    {
        await publisher.Enqueue(new SendEmailRequest { EmailLogId = 1 }, slowJobId);
    }

    // === Messages (pub/sub — each routes to multiple handlers) ===
    for (var i = 0; i < 10; i++)
    {
        await publisher.Publish(new OrderNotification());
    }

    // === Batch: 15 jobs → continuation batch of 8 (OnlyOnSucceeded, default) ===
    var batchJobs = Enumerable.Range(0, 15)
        .Select(_ => new SendEmailRequest { EmailLogId = 1 }).ToList();
    var batchId = await batchPublisher.StartNew(batchJobs);
    var batch2Jobs = Enumerable.Range(0, 8)
        .Select(_ => new SendEmailRequest { EmailLogId = 1 }).ToList();
    await batchPublisher.ContinueBatchWith(batch2Jobs, batchId);

    // === Batch with OnAnyFinishedState (continuation fires even if some fail) ===
    // Can't mix types in BatchPublisher, so use SendEmailRequest for success batch
    var failBatchJobs = Enumerable.Range(0, 5)
        .Select(_ => new ThrowExceptionRequest()).ToList();
    var failBatchId = await batchPublisher.StartNew(failBatchJobs, options: ContinuationOptions.OnAnyFinishedState);
    var afterFailBatchJobs = Enumerable.Range(0, 3)
        .Select(_ => new SendEmailRequest { EmailLogId = 1 }).ToList();
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
            new SendEmailRequest { EmailLogId = 1 },
            new JobParameters { Queue = "a-critical", }.WithMutex("payment:customer-42"));
    }

    // === Multiple continuation fan-out (parent → 3 continuations) ===
    var fanOutParentId = await publisher.Enqueue(new RegisterRequest { Email = "fanout-parent@test.com" });
    await publisher.Enqueue(new SendEmailRequest { EmailLogId = 1 }, fanOutParentId);
    await publisher.Enqueue(new SendEmailRequest { EmailLogId = 1 }, fanOutParentId);
    await publisher.Enqueue(new RegisterRequest { Email = "fanout-child@test.com" }, fanOutParentId);

    // === Job → Batch (7 jobs) → Batch (3 jobs) chain ===
    var chainJobId = await publisher.Enqueue(new RegisterRequest { Email = "chain-start@test.com" });
    var chainBatch1Jobs = Enumerable.Range(0, 7)
        .Select(_ => new SendEmailRequest { EmailLogId = 1 }).ToList();
    var chainBatch1Id = await batchPublisher.ContinueBatchWith(chainBatch1Jobs, chainJobId, "chain-batch-7");
    var chainBatch2Jobs = Enumerable.Range(0, 3)
        .Select(_ => new SendEmailRequest { EmailLogId = 1 }).ToList();
    await batchPublisher.ContinueBatchWith(chainBatch2Jobs, chainBatch1Id, "chain-batch-3");

    // === Batch with mixed success/failure (shows green/red progress bar) ===
    var mixedBatchJobs = new List<SendEmailRequest>();
    for (var i = 0; i < 10; i++)
    {
        mixedBatchJobs.Add(new SendEmailRequest { EmailLogId = 1 });
    }

    await batchPublisher.StartNew(mixedBatchJobs, "mixed-result-batch");

    // === Named batch (type column won't be null) ===
    var namedBatchJobs = Enumerable.Range(0, 5)
        .Select(_ => new SendEmailRequest { EmailLogId = 1 }).ToList();
    await batchPublisher.StartNew(namedBatchJobs, "email-campaign-batch");

    // === Cancellable job (long-running 30s, cancel from UI to see "Cancelling..." badge) ===
    await publisher.Enqueue(new SlowRequest(), queue: "c-low");

    // === Recurring jobs ===
    await recurringPublisher.AddOrUpdateRecurringJob(
        new SendEmailRequest { EmailLogId = 1 }, "send-daily-report", "0 9 * * *");
    await recurringPublisher.AddOrUpdateRecurringJob(
        new SendEmailRequest { EmailLogId = 1 }, "cleanup-hourly", "0 * * * *");
    await recurringPublisher.AddOrUpdateRecurringJob(
        new SendEmailRequest { EmailLogId = 1 }, "every-minute", "* * * * *");

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
    var simpleJobId = await publisher.Enqueue(new SendEmailRequest { EmailLogId = 1 });

    // 2. Simple failing job (shows retries + failed state)
    var failingJobId = await publisher.Enqueue(new ThrowExceptionRequest(), new JobParameters().Configure<IRetryMetadata>(m => m.MaxRetries = 2));

    // 3. Job → 3 continuation jobs (fan-out, creates trace via handler spawning)
    var fanOutId = await publisher.Enqueue(new RegisterRequest { Email = "flow-parent@test.com" });
    await publisher.Enqueue(new SendEmailRequest { EmailLogId = 1 }, fanOutId);
    await publisher.Enqueue(new SendEmailRequest { EmailLogId = 2 }, fanOutId);
    await publisher.Enqueue(new RegisterRequest { Email = "flow-child@test.com" }, fanOutId);

    // 4. Job → Batch(5) → Batch(3) chain
    var chainJobId = await publisher.Enqueue(new RegisterRequest { Email = "chain-start@test.com" });
    var batch1Jobs = Enumerable.Range(0, 5).Select(_ => new SendEmailRequest { EmailLogId = 1 }).ToList();
    var batch1Id = await batchPublisher.ContinueBatchWith(batch1Jobs, chainJobId, "chain-phase-1");
    var batch2Jobs = Enumerable.Range(0, 3).Select(_ => new SendEmailRequest { EmailLogId = 1 }).ToList();
    var batch2Id = await batchPublisher.ContinueBatchWith(batch2Jobs, batch1Id, "chain-phase-2");

    // 5. Batch(8) → continuation Batch(4)
    var batchJobs = Enumerable.Range(0, 8).Select(_ => new SendEmailRequest { EmailLogId = 1 }).ToList();
    var batchId = await batchPublisher.StartNew(batchJobs, "flow-batch");
    var contJobs = Enumerable.Range(0, 4).Select(_ => new SendEmailRequest { EmailLogId = 1 }).ToList();
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
        new SendEmailRequest { EmailLogId = 1 },
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
    var id = await publisher.Enqueue(new SendEmailRequest { EmailLogId = 1 });
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
    var parentId = await publisher.Enqueue(new RegisterRequest { Email = "flow-parent@test.com" });
    await publisher.Enqueue(new SendEmailRequest { EmailLogId = 1 }, parentId);
    await publisher.Enqueue(new SendEmailRequest { EmailLogId = 2 }, parentId);
    await publisher.Enqueue(new RegisterRequest { Email = "flow-child@test.com" }, parentId);
    await publisher.SaveChangesAsync();
    var traceId = await context.Set<Job>().Where(x => x.Id == parentId).Select(x => x.TraceId).FirstAsync();
    return Results.Ok(new { detail = $"/warp/detail/{parentId}", trace = $"/warp/trace/{traceId:N}" });
});

app.MapPost("/seed/chain", async (IPublisher publisher, IBatchPublisher batchPublisher, TestContext context) =>
{
    var jobId = await publisher.Enqueue(new RegisterRequest { Email = "chain-start@test.com" });
    var batch1Jobs = Enumerable.Range(0, 5).Select(_ => new SendEmailRequest { EmailLogId = 1 }).ToList();
    var batch1Id = await batchPublisher.ContinueBatchWith(batch1Jobs, jobId, "chain-phase-1");
    var batch2Jobs = Enumerable.Range(0, 3).Select(_ => new SendEmailRequest { EmailLogId = 1 }).ToList();
    var batch2Id = await batchPublisher.ContinueBatchWith(batch2Jobs, batch1Id, "chain-phase-2");
    await publisher.SaveChangesAsync();
    var traceId = await context.Set<Job>().Where(x => x.Id == jobId).Select(x => x.TraceId).FirstAsync();
    return Results.Ok(new { detail = $"/warp/detail/{jobId}", batch1 = $"/warp/detail/{batch1Id}", batch2 = $"/warp/detail/{batch2Id}", trace = $"/warp/trace/{traceId:N}" });
});

app.MapPost("/seed/batch", async (IBatchPublisher batchPublisher, TestContext context) =>
{
    var batchJobs = Enumerable.Range(0, 8).Select(_ => new SendEmailRequest { EmailLogId = 1 }).ToList();
    var batchId = await batchPublisher.StartNew(batchJobs, "flow-batch");
    var contJobs = Enumerable.Range(0, 4).Select(_ => new SendEmailRequest { EmailLogId = 1 }).ToList();
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
    var id2 = await publisher.Enqueue(new SendEmailRequest { EmailLogId = 1 }, new JobParameters { Queue = "a-critical", }.WithMutex("test-mutex"));
    await publisher.SaveChangesAsync();
    return Results.Ok(new { holder = $"/warp/detail/{id1}", cancelled = $"/warp/detail/{id2}" });
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

static void EnsurePortFree(int port)
{
    if (string.Equals(Environment.GetEnvironmentVariable("WARP_DEMO_PRESERVE_DB"), "1", StringComparison.Ordinal))
    {
        return;
    }

    var listener = new TcpListener(IPAddress.Loopback, port);
    try
    {
        listener.Start();
    }
    catch (SocketException)
    {
        Console.Error.WriteLine(
            $"Warp.TestApp: port {port} is in use — another instance appears to be running. "
            + "Refusing to start because Migrate() would DROP the live database. "
            + "Stop the running instance or set WARP_DEMO_PRESERVE_DB=1 to keep the existing schema.");
        Environment.Exit(2);
    }
    finally
    {
        listener.Stop();
    }
}

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
}

internal class DemoCredentialValidator : IWarpCredentialValidator
{
    public Task<bool> ValidateAsync(string username, string password)
    {
        return Task.FromResult(string.Equals(username, "admin", StringComparison.Ordinal) && string.Equals(password, "admin", StringComparison.Ordinal));
    }
}
