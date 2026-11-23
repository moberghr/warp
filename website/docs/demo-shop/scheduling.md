---
sidebar_position: 4
---

# Scheduling and background work

Northwind Goods does a lot of work that no customer request starts: it imports stock every night,
reminds customers about abandoned carts, polls ShipIt for tracking updates and builds a monthly sales
report. This page shows which Warp feature each of those uses, and the two push features that make the
work start fast and show up on the ops screen in real time.

| Feature | In the shop | Reference |
|---|---|---|
| [Recurring jobs](#recurring-jobs) | Nightly Stockroom stock import, abandoned-cart sweep every 15 minutes | [Recurring Jobs](../features/recurring-jobs.md) |
| [Background services](#background-services) | One ShipIt tracking poller across all `shop-worker` replicas | [Background Services](../features/background-services.md) |
| [Batches](#batches) | Monthly sales report: one job per region, then a merge | [Jobs: Batches](../patterns/jobs.md#batches) |
| [DB push](#db-push) | The payment job starts as soon as checkout commits | [DB Push](../features/db-push.md) |
| [Dashboard push](#dashboard-push) | The ops screen updates live | [Dashboard Push](../features/dashboard-push.md) |

All jobs on this page run on `shop-worker`. `shop-api` only publishes and hosts the dashboard.

## Recurring jobs

### In the shop

Stockroom exports the warehouse stock levels once a night, and the shop imports them at 02:00. Every 15
minutes a sweep finds carts that were left for more than an hour and asks Mailer to send a reminder.
Both are ordinary jobs that a cron schedule creates; nobody calls them from code. The import must never
overlap itself. The scheduler already holds back a firing while the previous run is still enqueued or
running, but an operator can still press **Trigger**, so the import also carries a mutex.

The sweep is one of two designs for abandoned-cart emails in this guide. The other schedules one job per
cart (see [Jobs](./orders-and-checkout.md#jobs)). Pick one; running both sends two emails.

### Wiring

```csharp
// Contracts and handlers (shared by shop-api and shop-worker).
[Mutex("stockroom-stock-import")]   // guards manual triggers; needs opt.AddConcurrency()
public sealed class ImportStockroomStock : IJob;

public sealed class SendAbandonedCartReminders : IJob;

public sealed class ImportStockroomStockHandler : IJobHandler<ImportStockroomStock>
{
    public async Task HandleAsync(ImportStockroomStock job, CancellationToken ct)
    {
        // Download the Stockroom export and upsert stock levels.
    }
}

// shop-worker, after the host is built.
using (var scope = host.Services.CreateScope())
{
    var recurring = scope.ServiceProvider.GetRequiredService<IRecurringJobPublisher>();

    // Cron is evaluated in UTC.
    await recurring.AddOrUpdateRecurringJob(new ImportStockroomStock(), "stockroom-stock-import", "0 2 * * *");
    await recurring.AddOrUpdateRecurringJob(new SendAbandonedCartReminders(), "abandoned-cart-sweep", "*/15 * * * *");
}
```

`AddOrUpdateRecurringJob` saves the definition itself and is safe to call on every start of every
replica: it takes a lock on the name and updates the existing row.

### Test scenario

- **Given** `shop-worker` is running and both definitions are registered.
- **When** you open `/warp/recurring` and press **Trigger** on `stockroom-stock-import`.
- **Then** a job starts at once. The **Last Result** column shows `Completed` with a link to the job,
  and **Next Execution** still shows the next 02:00 (a manual trigger does not move the schedule).
- **When** you press **Disable** on `abandoned-cart-sweep` and wait past the next quarter hour.
- **Then** the definition's history shows an orange **Skipped** entry, no job is created, and **Next
  Execution** shows `—`. **Enable** it again and the next quarter hour creates a real job.
- **When** you trigger the import twice while the first run is still running.
- **Then** the second job ends `Deleted` (skipped by the mutex), not `Failed`.
- **When** a scheduled 02:00 firing comes due while a triggered import is still running.
- **Then** no second job is created yet. The firing waits and runs once the running import has finished.

Pitfall: `AddOrUpdateRecurringJob` takes a session-scoped lock. Behind a pooler in transaction mode the
lock gives no mutual exclusion, and its release logs a Warning instead of throwing:
`Failed to release Warp distributed lock 'warp:recurring:{name}'…`, with the inner exception
`Attempted to release a lock that was not held` attached. See
[Connection Pooling](/docs/operations/connection-pooling).

Covered by: `RecurringJobTestsBase`, `RecurringJobEdgeCaseTestsBase`, `RecurringFinalStateTestsBase`,
`RecurringJobBugTestsBase`.

## Background services

### In the shop

Shipped orders move to `Delivered` when ShipIt's tracking says so. ShipIt has no push for this, so the
shop polls it every two minutes. A recurring job would hold a worker slot and restart from zero each
run; a `WarpBackgroundService` is a long-running loop that shows up in the dashboard and restarts
itself on a fault. `shop-worker` runs as several replicas, but only one of them may poll ShipIt, so the
service is a cluster singleton.

### Wiring

```csharp
public sealed class ShipItTrackingPoller : WarpBackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ShipItTrackingPoller> _logger;

    // Background services are singletons: inject IServiceScopeFactory, never ShopDbContext.
    public ShipItTrackingPoller(IServiceScopeFactory scopeFactory, ILogger<ShipItTrackingPoller> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public override ServiceScope Scope => ServiceScope.Singleton;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var scope = _scopeFactory.CreateScope();
            var shipIt = scope.ServiceProvider.GetRequiredService<IShipItApi>();   // the ShipIt adapter, see Integrations
            var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();

            // Load Shipped orders, ask ShipIt for each tracking state, mark Delivered ones, SaveChanges.
            _logger.LogInformation("Checked {Count} shipments", count);   // counts and order refs only, no PII

            await Task.Delay(TimeSpan.FromMinutes(2), ct);
        }
    }
}

// shop-worker
builder.Services.AddWarpServer<ShopDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.AddBackgroundService<ShipItTrackingPoller>();
});
```

### Test scenario

- **Given** two `shop-worker` replicas are running.
- **When** you open `/warp/services`.
- **Then** `ShipItTrackingPoller` shows `Running on 1, Waiting 1`. Its detail page has one tab per
  server, a **Lease** panel with the holder and a countdown, and the poller's `Checked N shipments`
  lines in the log tail.
- **When** you stop the holder replica gracefully.
- **Then** the lease is released at once and the other replica takes it on its next acquire poll
  (about 15 seconds). After a hard kill the takeover waits for the 30-second lease to expire.
- **When** you point the ShipIt adapter at an address that refuses connections.
- **Then** the instance shows `Faulted` with the exception, then `Restarting`, and the restart count
  rises with a 1s, 2s, 4s ... 30s backoff. Restore the address and it returns to `Running`.

Pitfall: `ExecuteAsync` must loop until cancelled. If it returns, the supervisor treats that as a
fault.

Covered by: `SingletonAcquisitionTestsBase`, `SingletonFailoverTestsBase`, `RestartBackoffTestsBase`,
`GracefulReturnTreatedAsFaultTestsBase`, `LogCaptureTestsBase`, `CaptiveScopedDependencyTests`.

## Batches

### In the shop

On the first of each month, finance wants a sales report. Each region (EU, UK, US) is a separate query
against a large table, so the report runs as one job per region in parallel. A merge job builds the
final PDF only when every region has finished. A recurring job starts the batch.

### Wiring

```csharp
public sealed class BuildRegionSalesReport : IJob
{
    public string Region { get; set; } = string.Empty;
    public DateOnly Month { get; set; }
}

public sealed class MergeSalesReport : IJob
{
    public DateOnly Month { get; set; }
}

public sealed class StartMonthlySalesReport : IJob;

public sealed class StartMonthlySalesReportHandler(IBatchPublisher batches, TimeProvider clock)
    : IJobHandler<StartMonthlySalesReport>
{
    public async Task HandleAsync(StartMonthlySalesReport job, CancellationToken ct)
    {
        var month = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddMonths(-1);
        var regions = new List<BuildRegionSalesReport>
        {
            new() { Region = "eu", Month = month },
            new() { Region = "uk", Month = month },
            new() { Region = "us", Month = month },
        };

        var batchId = await batches.StartNew(regions, name: $"sales-report-{month:yyyy-MM}");
        await batches.ContinueBatchWith(new List<MergeSalesReport> { new() { Month = month } }, batchId);
        await batches.SaveChangesAsync(ct);
    }
}

// Registered with the other recurring jobs: 03:00 UTC on the 1st.
await recurring.AddOrUpdateRecurringJob(new StartMonthlySalesReport(), "monthly-sales-report", "0 3 1 * *");
```

The default `ContinuationOptions.OnlyOnSucceeded` is what finance wants: no report from partial data.

### Test scenario

- **Given** `shop-worker` is running.
- **When** you trigger `monthly-sales-report` from `/warp/recurring`.
- **Then** `/warp/batches/...` shows a batch named `sales-report-<month>` with three child jobs. They
  run in parallel. When all three are `Completed`, the batch is `Completed` and `MergeSalesReport`
  moves from `Awaiting` to `Enqueued` and runs.
- **When** you make the `us` job throw (for example, revoke its read permission on the sales table) and
  trigger again.
- **Then** the `us` job ends `Failed`, the batch is `Failed`, and `MergeSalesReport` stays `Awaiting`.
  Fix the cause and requeue the `us` job from its detail page: the batch completes and the merge runs.

Pitfall: forgetting `batches.SaveChangesAsync(ct)` discards the batch, in a handler as anywhere else, and
Warp logs a warning when the scope ends with unsaved staged jobs. Since 8.0 the worker does not save a
handler's DbContext: the handler commits its own work.

Covered by: `BatchIntegrationTestsBase`, `BatchPublisherUnitTestsBase`.

## DB push

### In the shop

After checkout, `shop-api` publishes `OrderPlaced` and `shop-worker` authorises the payment. The
customer waits on the order page for that result. `OrderPlaced` is a message, so the payment job only
exists once the message router in `shop-worker` has routed it, and without push the router looks only
every `MessageRoutingInterval` (10 s). With DB push the commit in `shop-api` sends a notification
(PostgreSQL `LISTEN`/`NOTIFY`, or Service Broker on SQL Server), the router runs at once, and the
routed job wakes a local worker.

The notification is sent by `publisher.SaveChangesAsync(ct)`, which the checkout code uses (see
[Messages](./orders-and-checkout.md#messages)). Committing through the DbContext's own
`db.SaveChangesAsync(ct)` stores the same rows but sends nothing.

### Wiring

```csharp
// shop-api: publishes, so it must send the notification.
builder.Services.AddWarp<ShopDbContext>(opt =>
{
    opt.UsePostgreSql();     // provider FIRST: it registers the notification transport
    opt.UseDatabasePush();
});

// shop-worker: receives the notification and wakes the router and the workers.
builder.Services.AddWarpServer<ShopDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.UseDatabasePush();      // the listener wakes the dispatcher and bare workers alike
});
```

### Test scenario

- **Given** both services run with the wiring above, and no orders have come in for two minutes.
- **When** you place an order on `shop-web`.
- **Then** on the `OrderPlaced` message's detail page (`/warp/messages/detail/{id}`), the `Routed` entry
  follows the message's creation time (**Created** in the **Details** card; hover it for the exact time) by
  well under a second. A message's history has no `Created` entry of its own. On the payment job's detail
  page (`/warp/jobs/detail/{id}`), `Processing` follows `Created` just as quickly.
- **When** you remove `UseDatabasePush()` from both services, wait two minutes, and place another order.
- **Then** the gap between the message's creation time and its `Routed` entry grows to up to the
  `MessageRoutingInterval` (10 s). The payment job itself is still picked up quickly once it exists,
  because the router wakes the local workers. So the `queues` family on `/warp/counters` shows no jump:
  queue wait starts when the job is created, after routing.

Pitfalls:

- Call the provider (`UsePostgreSql()` / `UseSqlServer()`) before `UseDatabasePush()`.
- `shop-api` needs `UseDatabasePush()` too, and must commit with `publisher.SaveChangesAsync(ct)`.
  Without the addon, or with `db.SaveChangesAsync(ct)`, nothing is sent.
- A pooler in transaction mode drops `LISTEN` silently and falls back to polling. SQL Server without
  `ENABLE_BROKER` also falls back to polling, but logs an Error (`Service Broker setup failed`).
- Push does not speed up scheduled jobs. They wait for `ScheduledJobActivation` (default every 10s).
- `Created` is the log entry for a job published directly or created by the router; a message has none.
  `Enqueued` means the job went back to the queue: a scheduled job was activated, or a worker requeued it
  for immediate pickup (a retry or wait with no delay; a delayed one logs `Scheduled`).

Covered by: `DatabasePushIntegrationTests` (both databases),
`UseDatabasePushValidationTests`, `NonServerListenerPushTestsBase`, `ListenerReconnectDrainTestsBase`,
`PushFailurePollingBackstopTestsBase`.

## Dashboard push

### In the shop

The ops team keeps the Warp dashboard open on a wall screen during sales. With polling, every open tab
refetches the statistics on its own. With dashboard push, `shop-api` sends SignalR events to every open
tab when jobs finish or messages arrive, and the counts and charts update within about a second. The events
are coalesced: at most one of each kind (`JobFinalized`, `MessageEnqueued`) every second, however many jobs
finished in between.

### Wiring

```csharp
// shop-api, which hosts the dashboard.
builder.Services.AddWarp<ShopDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.UseDatabasePush();    // jobs finish on shop-worker; this is how shop-api hears about it
    opt.AddDashboardPush();
});

app.MapWarpDashboard();       // the hub is mapped at /warp/api/hub with the same authorization
```

### Test scenario

- **Given** `shop-api` and `shop-worker` run with the wiring above, and the dashboard is open in two
  browser tabs.
- **Then** the status pill in the navbar shows **Live** in both tabs.
- **When** you place five orders on `shop-web`.
- **Then** the navbar counts and the dashboard charts move in both tabs within about a second, without
  a refresh.
- **When** you remove `UseDatabasePush()` from `shop-api` and repeat.
- **Then** the pill still shows **Live**. New `OrderPlaced` messages still update at once, because
  `shop-api` published them and `publisher.SaveChangesAsync` signals its own process. Finished jobs reach
  the navbar counts and the charts only with the next pushed event (the next order's `MessageEnqueued`
  carries fresh counts) or on a reload, because those events happen in `shop-worker` and never reach
  `shop-api`. While the hub is connected the statistics poll is off; it runs every second only while the
  hub is disconnected.

Pitfall: without DB push, dashboard push only sees events from its own process. In this shop that
means the messages `shop-api` publishes itself, and no job finishes, since `shop-api` runs no worker.

Covered by: `DashboardBroadcasterTests`, `DashboardPushIntegrationTestsBase`,
`DashboardPushRegistrationTests`, `DashboardPushAuthTests`, `NonServerListenerPushTestsBase`.
