---
sidebar_position: 8
---

import Screenshot from '@site/src/components/Screenshot';

# Background Services

`WarpBackgroundService` is a dashboard-visible analog of .NET's `BackgroundService`. Warp manages the lifecycle — restart-on-fault with exponential backoff, optional cluster-singleton coordination via lease, and automatic log capture to the database — without consuming a worker slot.

## What

Users who need long-running in-process work (Kafka consumers, periodic syncs, connection-holding daemons) currently choose between raw `BackgroundService` (invisible to operators) or a recurring `IJob` with a tight interval (wastes a worker slot, forces a single-pass shape). `WarpBackgroundService` gives the third option: a `BackgroundService` that shows up in the dashboard, restarts automatically on fault, and coordinates a single active instance across the cluster when needed.

## Quick start

Migration from `BackgroundService` is a one-line change:

```csharp
// Before
public sealed class KafkaDrainService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct) { ... }
}

// After
public sealed class KafkaDrainService : WarpBackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct) { ... }
}
```

Register via the worker builder:

```csharp
builder.Services.AddWarpWorker<AppDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.AddBackgroundService<KafkaDrainService>();
});
```

A more complete example with injected dependencies:

```csharp
public sealed class KafkaDrainService : WarpBackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<KafkaDrainService> _logger;

    public KafkaDrainService(IServiceScopeFactory scopeFactory, ILogger<KafkaDrainService> logger)
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
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // drain a batch, commit offsets, etc.
            _logger.LogInformation("Drained {Count} messages", count);
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }
}
```

`AddBackgroundService<T>()` registers `T` as a singleton and aliases it for host discovery. The four persistence tables (`Definition`/`Instance`/`Lease`/`Log`) ship with every Warp install (added by `AddWarp<TContext>`), so the only thing you do when adopting Background Services is register your concrete service classes. The first time you upgrade you'll need an EF Core migration to materialise the four tables; after that, schema changes only when Warp itself ships entity changes:

```bash
dotnet ef migrations add AddWarpBackgroundServices
dotnet ef database update
```

## Scope

`WarpBackgroundService.Scope` controls how many instances run across the cluster.

| Scope | Behavior | When to use |
|---|---|---|
| `PerServer` (default) | One instance per server process | Stateless work where N parallel copies is correct — e.g. in-process caches, per-server health probes |
| `Singleton` | Exactly one active instance cluster-wide, coordinated via a database lease | Stateful work where only one instance should run — Kafka consumer with offset ownership, distributed cron without overlap |

Override in your subclass:

```csharp
public override ServiceScope Scope => ServiceScope.Singleton;
```

## Lifecycle

Every `WarpBackgroundService` is **always-on** — there is no stop button in v1. The supervisor loop:

1. Registers the service definition in the database (upserts on first boot, compares scope on subsequent boots).
2. For `Singleton` scope: polls until it acquires the cluster lease, then sets status `Running`.
3. For `PerServer` scope: immediately sets status `Running`.
4. Invokes `ExecuteAsync(ct)`.
5. On fault (exception OR graceful return — see below): sets status `Faulted`, records the exception, waits the backoff interval, then sets status `Restarting` and loops.
6. On graceful host shutdown: cancels the `CancellationToken` passed to `ExecuteAsync`, waits up to `BackgroundServiceShutdownTimeout` (default 30s) for the method to return, deletes the instance row.

**Restart backoff:** exponential 1s → 2s → 4s → 8s → 16s → 30s (capped). The backoff index advances on each fault.

**Healthy-reset:** if `ExecuteAsync` ran continuously for ≥5 minutes before this fault, the backoff index resets to 0 and the restart count resets to 0. A transient blip on an otherwise healthy service doesn't accumulate backoff debt.

**Graceful return treated as fault.** If your `ExecuteAsync` returns without its `CancellationToken` being cancelled, the supervisor treats it as a fault — the service stopped running when it shouldn't have. This surfaces as a `Faulted` status in the dashboard. A `BackgroundService` must run until cancelled.

## Lease coordination (Singleton)

Singleton coordination uses a database lease row instead of a Medallion long-held advisory lock (long-held connection-scoped locks silently release on TCP disconnect, causing split-brain).

**Lease semantics:**

- **TTL:** 30 seconds (configurable via `opt.BackgroundServiceLeaseTtl`).
- **Renewal:** piggybacked on the `Heartbeat` server task (~3s cadence). The heartbeat SQL batch also updates `BackgroundServiceLease.LeaseExpiresAt = now + TTL` for every lease this server currently holds.
- **Loss detection:** The heartbeat computes which leases it held last beat but not this beat (expired, stolen, or deleted). Each lost-lease name is published as a `BackgroundServiceLeaseLost` signal. The affected supervisor cancels the `CancellationToken` it passed to `ExecuteAsync`, so user code observes `OperationCanceledException` and can exit cleanly.
- **Acquisition:** A waiting server polls every ~15 seconds (configurable via `opt.BackgroundServiceAcquirePollInterval`). The acquire query is an atomic UPDATE: `SET holder_server_id = @me, lease_expires_at = @now + ttl WHERE holder_server_id IS NULL OR lease_expires_at < @now`. Zero rows returned = lease still held; one row = acquired.
- **Worst-case failover:** ~30s on hard-kill (lease TTL must expire before a waiter can take over). ~0s on graceful shutdown — `StopAsync` issues a `DELETE` of the lease row immediately before waiting on `ExecuteAsync`, so a hanging service doesn't strand the lease.

## Configuration mismatch

If a service's declared `Scope` disagrees with the scope stored in the `BackgroundServiceDefinition` row (written by whichever server booted first), the supervisor refuses to start user code and sets status `ConfigurationMismatch`. This happens during rolling deploys where `Scope` changed between versions.

**What to do:** Complete the rolling deploy (all servers converge to the new scope) or roll back. The mismatch status self-resolves when all servers agree. The dashboard surfaces it loudly on the list page.

## Captive scoped dependencies (foot-gun)

`WarpBackgroundService` subclasses are registered as **singletons**. Injecting a scoped dependency (e.g. `DbContext`) directly into the constructor creates a captive-scoped dependency — the scoped service is effectively promoted to singleton lifetime, the change-tracker grows unboundedly, the connection stays pinned, and you get one of those "why is my background service eating 8 GB after a week" outages.

`ValidateScopes = true` (already enabled in development and tests) catches this at startup with a clear `InvalidOperationException`.

**Wrong — captive scoped dep:**

```csharp
public sealed class OrderSync : WarpBackgroundService
{
    private readonly AppDbContext _db;
    private readonly ILogger<OrderSync> _logger;

    // ❌ AppDbContext is Scoped; this ctor pins it for the lifetime of the host.
    public OrderSync(AppDbContext db, ILogger<OrderSync> logger)
    {
        _db = db;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _db.Orders.AddAsync(new Order { ... }, ct);
            await _db.SaveChangesAsync(ct);          // same context, hours of accumulated state
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
        }
    }
}
```

**Correct — inject `IServiceScopeFactory` and open a scope per work unit:**

```csharp
public sealed class OrderSync : WarpBackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OrderSync> _logger;

    // ✅ IServiceScopeFactory is a Singleton — safe to capture.
    public OrderSync(IServiceScopeFactory scopeFactory, ILogger<OrderSync> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Fresh scope per iteration — DbContext, change-tracker, and connection
            // are disposed at the end of the using block.
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            await db.Orders.AddAsync(new Order { ... }, ct);
            await db.SaveChangesAsync(ct);

            await Task.Delay(TimeSpan.FromMinutes(1), ct);
        }
    }
}
```

The pattern is identical to the recommended approach for plain `Microsoft.Extensions.Hosting.BackgroundService` — there is no Warp-specific twist beyond "registered as singleton, so don't capture scoped state."

## Log capture

`ILogger<T>` calls inside `ExecuteAsync` are automatically captured to `BackgroundServiceLog` rows in addition to flowing through the normal application logging stack (Serilog, console, etc.). This is wired without any code change — the supervisor adds a `BackgroundServiceLoggerProvider` to the global `ILoggerFactory` that routes log calls from the service's category into the per-instance collector.

**Level filter.** Default `MinLogLevel = Information`. Override per service:

```csharp
public override LogLevel MinLogLevel => LogLevel.Debug;
```

**Rate cap.** Sustained >100 captured entries/second triggers a 10-second drop window. One synthetic `Warning` row is emitted on entering drop mode; one synthetic `Information` row is emitted on exit with the count of dropped entries.

**Message truncation.** Messages and exception messages are capped at 4096 bytes; longer values are truncated with `…[truncated]`.

**Retention.** Per instance:
- Count cap: 1000 rows (configurable via `WarpConfiguration.BackgroundServiceLogRetentionCount`, per-service override via `LogRetentionCountOverride`).
- Age cap: 7 days (configurable via `WarpConfiguration.BackgroundServiceLogRetentionAge`, per-service override via `LogRetentionAgeOverride`).

`ExpirationCleanup` (the existing retention server task) enforces both caps.

**PII responsibility.** Log messages captured to `BackgroundServiceLog` are visible in the dashboard to anyone with dashboard access (§1.5). Do not log PII — user IDs, email addresses, payment info — at the `Information` level or above. Use opaque identifiers where possible. See §1.2 in `.claude/rules/security.md`.

## Dashboard

The `Services` entry in the dashboard nav is always present. When no `WarpBackgroundService` is registered, the list page is simply empty.

### List page (`/warp/services`)

One row per registered service name. Columns show the scope, an aggregated status summary (`Running 3/3` for per-server, `Running on server-X, 2 waiting` for singletons), aggregate restart count across instances, the last error type if any instance is currently faulted, and a configuration-mismatch indicator. Polls every ~2 s while open.

<Screenshot light="/img/screenshots/19-services-list.png" dark="/img/screenshots/19-services-list-dark.png" alt="Background services list" />

### Detail page (`/warp/services/{name}`)

Per-instance tabs — one tab per server. Each tab shows the instance's status badge, server name (resolved through the EF nav property — the Guid is still surfaced as the "Server ID" line for debugging), started-at, last-heartbeat, restart count, and the full captured exception when the instance is in `Faulted` state. The log tail combines `Lifecycle` events (Started, LeaseAcquired, LeaseLost, Faulted, Restarting, Stopped, ConfigurationMismatch) with `User` log entries from your `ILogger<T>` calls. Filter by Source or minimum Level; click an entry with an exception to expand.

Singletons additionally render a Lease panel showing the holder's server name + ID and a live countdown to `LeaseExpiresAt`. When the lease changes holder (graceful failover or TTL takeover), the panel swaps in the new holder on the next poll.

<Screenshot light="/img/screenshots/20-services-detail-singleton.png" dark="/img/screenshots/20-services-detail-singleton-dark.png" alt="Singleton background service detail with lease panel" />

For per-server scope the lease panel is omitted; each tab represents an independent instance ticking on its own host.

<Screenshot light="/img/screenshots/21-services-detail-perserver.png" dark="/img/screenshots/21-services-detail-perserver-dark.png" alt="Per-server background service detail with multiple instances" />

REST polling only in v1. Dashboard push integration is deferred.

## Telemetry

Four counters emitted via `WarpTelemetry.Meter` (meter name `"Warp"`):

| Counter | Tags | Description |
|---|---|---|
| `warp.background_services.started` | `service_name` | Total `ExecuteAsync` invocations, including restarts. |
| `warp.background_services.faulted` | `service_name`, `exception_type` | Total faults (exception or graceful return). `exception_type` is the short CLR type name. |
| `warp.background_services.lease_lost` | `service_name` | Total singleton lease-loss events detected by the Heartbeat task. |
| `warp.background_services.restarts` | `service_name` | Total restart-backoff entries (increments each time the supervisor enters the backoff-wait path). |

Wire into your OpenTelemetry pipeline via the standard `Warp` meter source.
