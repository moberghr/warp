---
sidebar_position: 1
---

# Configuration

## Core Configuration (`WarpConfiguration`)

Used by the publisher side (`AddWarp<TContext>`):

```csharp
builder.Services.AddWarp<AppDbContext>(options =>
{
    options.Schema = "warp";      // Database schema for all Warp tables (default: "warp", null for default schema)
    options.DefaultQueue = "default"; // Queue name when none specified (default: "default")
    options.JobExpirationTimeout = TimeSpan.FromDays(1); // How long completed/deleted jobs kept (default: 1 day)
});
```

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `Schema` | `string?` | `"warp"` | Database schema for all Warp tables. Set to `null` for the database's default schema. |
| `DefaultQueue` | `string` | `"default"` | Queue used when no queue is specified at publish time. The implicit worker group follows it when `Queues` is left untouched (see the worker options below). |
| `JobExpirationTimeout` | `TimeSpan` | `1 day` | How long completed/deleted jobs are kept before cleanup. Failed jobs never expire. |
| `RequireDatabaseAtStartup` | `bool` | `true` | Whether the host refuses to start when Warp cannot reach the database at startup. See [Starting without the database](#starting-without-the-database). |

### Starting without the database

By default a Warp server writes its registration (and checks the server version, on PostgreSQL, or the compatibility level, on SQL Server) inside `StartAsync`, so the host refuses to start while the database is unreachable. A non-server process with `ApplicationName` set does the same for its instance row.

Set `RequireDatabaseAtStartup = false` to let the host come up anyway:

```csharp
builder.Services.AddWarpServer<AppDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.RequireDatabaseAtStartup = false;
});
```

- A startup step that fails because the database **cannot be reached** (a connection, DNS, socket or timeout failure, a server not accepting connections yet, or a database that does not exist yet) is retried in the background with backoff (1 s doubling to 30 s), the provider's check first, then the registration. PostgreSQL failures are told apart by SQLSTATE, SQL Server ones by error number. The first such failure logs **one Warning** with the exception; the retries log at Debug. Each attempt opens a bare connection first (one pooled connection open, also on a healthy start), so EF Core does not log a connection Error on every attempt.
- Until the server is registered, Warp's server does not run: no workers, no server tasks, no background services, no counter flusher, no notification listener, and the SLO and metric-board seeders wait too. Once it is registered, everything starts as if startup had succeeded. Dashboard push and the adapter, endpoint and client recording flushers serve your own traffic and already tolerate a database that is down; they are not held back.
- Any other failure still fails startup: a server version Warp does not support, an authentication or permission error. If a background retry reaches the database and the step then fails, it is logged at Error and the server stays stopped in this process.
- Stopping the host while it waits is clean: the retries stop and nothing is deregistered, because nothing was registered.
- Publishing is unaffected: it writes `Job` rows on your own context when you save it, and fails there if the database is down.

Typical uses: an application's controller tests that boot `Program.cs` without a database (see [Testing apps that use Warp](testing-apps.md)), or a service started before its database in a container group.

`RequireDatabaseAtStartup` is a Core setting: set it on either `AddWarp` or `AddWarpServer`.

### Custom application metrics

Set on `AddWarp` (or `AddWarpServer`, which inherits them) and only meaningful where `opt.AddMetrics(...)` ran. See [Custom application metrics](/docs/features/custom-metrics).

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `MetricFlushInterval` | `TimeSpan` | `30 seconds` | How often each process writes the metrics it aggregated in memory as partial `metric_point` rows, and reads its observable instruments. A flush also runs on graceful shutdown; **an ungraceful exit loses at most one interval.** An idle interval does no database work. Must be greater than zero (`AddMetrics` throws otherwise). Raise it on emit-only processes to cut insert volume. |
| `MetricRollupInterval` | `TimeSpan?` | `1 minute` | How often the `MetricRollup` server task compacts closed 5-minute buckets and rolls them up to hourly and daily rows. `null` disables the rollup loop (metric rows then accumulate). It runs on every `AddWarpServer` process, including `DisableWorker()` ones. Tier ages come from `FineResolutionRetention` (6h), `HourlyStatisticsRetention` (7d) and `DailyStatisticsRetention` (90d, `null` keeps forever). |

To keep application versions apart in the stored series (old vs new during a rolling deploy), call `ByVersion()` inside `AddMetrics` on the hosts that should record it. It is off by default and needs `ApplicationName` and `ApplicationVersion`. See [Comparing versions](/docs/features/custom-metrics#comparing-versions).

### Webhooks

Set on `AddWarp` (or `AddWarpServer`, which inherits them). The webhook engine is always on; see [Outbound Webhooks](/docs/features/webhooks).

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `WebhookAttemptTimeout` | `TimeSpan` | `30 seconds` | Budget for one delivery attempt: sending the request, receiving the response headers and reading the captured prefix of the body. An attempt past it is cancelled and counts as a failed attempt (retried per the delivery's `RetrySchedule`). Enforced with a cancellation token, not `HttpClient.Timeout`, so a `warp-webhooks` client you configure keeps its own settings; that client's `Timeout` (100 s by default) still applies to the send, so raise it too before going above 100 s. If the headers arrived in time and only the body capture runs out, the status code still decides the outcome. Must be at least 1 ms (or `Timeout.InfiniteTimeSpan` to disable); `AddWarp` throws otherwise. New in 8.1. |
| `WebhookDeliveryRetention` | `TimeSpan` | `30 days` | How long a delivery row is kept (`ExpireAt = CreatedAt + retention`); also the default call-log retention of the `warp-webhooks` adapter. |
| `WebhookDeliveryRetentionCount` | `int?` | `null` | Count cap for settled deliveries (oldest deleted first). `Pending` deliveries are never count-trimmed. `null` disables it. |
| `WebhookEndpointMonitorInterval` | `TimeSpan?` | `30 seconds` | How often the `WebhookEndpointMonitor` server task runs: it releases the parked deliveries of recovered endpoints, probes failing endpoints that are due and raises the still-failing alert. With no tracked endpoint it reads one small table. `null` turns it off (parked deliveries then wait). Must be greater than zero when set. New in 8.1. See [Endpoint health](/docs/features/webhooks#endpoint-health). |
| `WebhookEndpointProbeInterval` | `TimeSpan` | `5 minutes` | How long a failing endpoint waits between probes (a probe re-sends its oldest parked delivery once). Must be greater than zero. New in 8.1. |
| `WebhookEndpointStillFailingAfter` | `TimeSpan` | `30 days` | How long an endpoint must have been failing before the `WebhookEndpointStillFailing` alert and `IWebhookEndpointHealthHandler.OnEndpointStillFailingAsync` fire, once per failing period. Must be greater than zero. New in 8.1. |
| `WebhookStuckDeliveryGrace` | `TimeSpan` | `10 minutes` | How far past `NextAttemptAt` a `Pending` delivery must be before the stuck-delivery sweep gives it a fresh executor job. |

`opt.AddWebhooks(w => ...)` takes the per-process options: `UseCustomSigner<T>()`, `OnDeliveryExhausted<T>()` and, new in 8.1:

- `PublicAddressesOnly` (default **`true`** since 8.1) — applies where attempts run, so keep it on in every server that runs a worker; set it to `false` only for local development and for tests that stub the `warp-webhooks` client. With it on, `SendAsync` also rejects non-public IP-literal and `localhost` URLs. See [Public addresses only](/docs/features/webhooks#public-addresses-only).
- `TrackEndpointHealth` (default `false`) — applies where deliveries are sent: stamped on each delivery at `SendAsync`. See [Endpoint health](/docs/features/webhooks#endpoint-health).
- `DeduplicateByEventId` (default `false`) — applies where deliveries are sent. See [Deduplication](/docs/features/webhooks#deduplication).
- `OnEndpointHealthChanged<T>()` — registers an `IWebhookEndpointHealthHandler`.

### Naming Conventions

Warp's entity configurations do **not** hardcode table or column names. If you use a naming convention plugin (e.g., `UseSnakeCaseNamingConvention()`), it will transform Warp's tables and columns just like your own entities:

```csharp
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString)
           .UseSnakeCaseNamingConvention());
```

This produces tables like `warp.job`, `warp.job_log`, `warp.server`, etc.

## Retry Configuration

Configure retry behavior via `opt.AddRetry()` inside the `AddWarpServer` lambda:

```csharp
builder.Services.AddWarpServer<AppDbContext>(opt =>
{
    opt.AddRetry(options =>
    {
        options.MaxRetries = 3;               // Default max retries (default: 0)
        options.Delays = [15, 60, 300];       // Retry delays in seconds (default: [15, 60, 300])
        options.JitterFactor = 0.2;           // Random ±20% jitter on each delay (default: 0, no jitter)
    });
});
```

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `MaxRetries` | `int` | `0` | Default max retries when no `[Retry]` attribute is present |
| `Delays` | `int[]` | `[15, 60, 300]` | Delay in seconds between retries. Last value is reused if fewer delays than retries |
| `JitterFactor` | `double` | `0.0` | Multiplicative jitter applied to each delay: `delay * (1 + JitterFactor * rand(-1, 1))`. Clamped to `[0, 1]`. Global only — no per-job override. Helps avoid retry thundering herds |

Per-job override via `[Retry]` on the handler class or the job class, or per-enqueue via metadata — `enqueue-time metadata > handler > job type > these global options`, resolved once at the job's first execution and written onto the row. A retry policy is atomic per rung: the winning rung supplies both `MaxRetries` and `Delays`. See [Jobs](/docs/patterns/jobs#retries) and [Where do I declare the policy?](/docs/features/mutex#where-do-i-declare-the-policy-contract-vs-handler).

## Concurrency Configuration

Enable concurrency control (Mutex + Semaphore) via `opt.AddConcurrency()` inside the `AddWarpServer` lambda:

```csharp
builder.Services.AddWarpServer<AppDbContext>(opt =>
{
    opt.AddConcurrency();
});
```

No options — just register and use `.WithMutex("key")` / `[Mutex("key")]` for at-most-one, or `.WithSemaphore("key", N)` / `[Semaphore("key", N)]` for at-most-N concurrent jobs. The attributes go on the job/message type, on the handler class, or on both (the handler wins) — every policy addon resolves the same way, `enqueue-time metadata > handler > contract > global default`, once at the job's first execution. See [Where do I declare the policy?](/docs/features/mutex#where-do-i-declare-the-policy-contract-vs-handler) and [Concurrency control](/docs/features/mutex) for details.

## Circuit Breaker Configuration

Enable the circuit breaker via `opt.AddCircuitBreaker()` inside the `AddWarpServer` lambda:

```csharp
builder.Services.AddWarpServer<AppDbContext>(opt =>
{
    opt.AddCircuitBreaker(options =>
    {
        options.Threshold = 5;                          // default: 3
        options.Duration = TimeSpan.FromMinutes(1);     // default: 1 minute
        options.ResetJitter = TimeSpan.FromSeconds(10); // default: 10s
    });
});
```

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `Threshold` | `int` | `3` | Consecutive failures before the circuit opens |
| `Duration` | `TimeSpan` | `1 minute` | How long the circuit stays open before the half-open probe window |
| `ResetJitter` | `TimeSpan` | `10 seconds` | Jitter added to each rescheduled `ScheduleTime` so rescheduled jobs don't all hit the downstream at the exact moment the circuit expires |

Overrides on `[CircuitBreaker]` — on the handler class or on the job/message type, handler first — use `Group`, `Threshold`, `DurationSeconds`, and `ResetJitterSeconds`; there is no enqueue-time rung for the breaker (see [Precedence](/docs/features/circuit-breaker#contract-or-handler)). The `CircuitBreakerState` entity is part of Warp's base schema (registered by `AddWarp` unconditionally), so no separate migration is required when you turn the addon on. See [Circuit Breaker](/docs/features/circuit-breaker) for details.

## NoRestart Configuration

Enable the stale-recovery opt-out via `opt.AddNoRestart()` inside the `AddWarpServer` lambda:

```csharp
builder.Services.AddWarpServer<AppDbContext>(opt =>
{
    opt.AddNoRestart();
});
```

No options. Register it to make `[NoRestart]` / `[Restart]` attributes take effect at publish time. `.WithRestart(bool)` works without the addon. See [NoRestart](/docs/features/no-restart) for details.

The fleet-wide default is controlled by `WarpServerConfiguration.RestartStaleJobsByDefault` (default `true`). Flip to `false` to fail stale jobs on crash unless they explicitly opt in.

## Calling both `AddWarp` and `AddWarpServer`

`WarpServerConfiguration` extends `WarpConfiguration`, so a server lambda can set any Core setting and most hosts only ever call `AddWarpServer`. If you call **both** — shared setup registers `AddWarp`, and the server host adds `AddWarpServer` — each Core setting belongs to exactly one of the two lambdas:

```csharp
// Shared setup, used by every process
builder.Services.AddWarp<AppDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.ApplicationName = "orders";
    opt.AdapterCallLogRetention = TimeSpan.FromDays(3);
});

// Server host only — server settings here, Core settings stay above
builder.Services.AddWarpServer<AppDbContext>(opt =>
{
    opt.WorkerCount = 10;
});
```

Warp folds the two together at registration, in either call order, so the server tasks and the Core services always read the same values. Setting the **same** Core setting in both lambdas to **different** values throws at startup, naming the property and both values:

```csharp
builder.Services.AddWarp<AppDbContext>(opt => opt.Schema = "warp");
builder.Services.AddWarpServer<AppDbContext>(opt => opt.Schema = "jobs");
// InvalidOperationException: Warp configuration conflict: 'Schema' is set to 'warp' in the
// AddWarp lambda and to 'jobs' in the AddWarpServer lambda. It is a Core-level setting shared
// by both, so set it in exactly one of them.
```

Nothing distinguishes which lambda you meant to win, so Warp refuses to pick rather than let half your configuration be silently ignored. Setting the same value in both is fine. Server-only settings (`WorkerCount`, the polling and task intervals, `InvisibilityTimeout`, …) exist only on `WarpServerConfiguration` and are never involved.

## Server Configuration (`WarpServerConfiguration`)

Extends `WarpConfiguration`. Used by a Warp server (`AddWarpServer<TContext>`):

```csharp
builder.Services.AddWarpServer<AppDbContext>(options =>
{
    // Worker — runs by default. Call options.DisableWorker() (or set RunWorker = false) for a
    // service-only server that runs background services but processes no jobs.
    options.RunWorker = true;
    options.WorkerCount = 10;
    options.PollingInterval = TimeSpan.FromSeconds(1);    // floor (default is 10s)
    options.MaxPollingInterval = TimeSpan.FromSeconds(30); // ceiling for exponential backoff
    options.PollingIntervalFactor = 2.0;                   // multiplier on each empty poll (1.0 disables backoff)
    options.Queues = ["a-critical", "b-default", "c-low"];

    // Dispatcher mode (default): one batch claim per worker group, batched completions.
    // false = single-worker mode, each worker claims and completes its own job.
    options.UseDispatcher = true;

    // Cancellation
    options.CancellationCheckInterval = TimeSpan.FromSeconds(5);

    // Server identity
    options.ServerName = "my-api-server";
    options.ServerId = Guid.NewGuid(); // Auto-generated, rarely needs override

    // Health & crash recovery
    options.HealthCheckInterval = TimeSpan.FromSeconds(5);
    options.HealthCheckTimeout = TimeSpan.FromMinutes(5);
    options.InvisibilityTimeout = TimeSpan.FromMinutes(5);

    // Job retention
    options.JobExpirationTimeout = TimeSpan.FromDays(1);
    options.ExpirationBatchSize = 1000;
    options.MaxExpirableJobCount = null; // Null = unlimited

    // Background task intervals
    options.OrchestrationInterval = TimeSpan.FromSeconds(10);
    options.MessageRoutingInterval = TimeSpan.FromSeconds(10);
    options.ScheduledActivationInterval = TimeSpan.FromSeconds(10);
    options.CounterAggregationInterval = TimeSpan.FromMinutes(1);
    options.CounterBufferFlushInterval = TimeSpan.FromSeconds(2);
    options.ServerCleanupInterval = TimeSpan.FromSeconds(30);
    options.StaleJobRecoveryInterval = TimeSpan.FromSeconds(30);
    options.ExpirationCleanupInterval = TimeSpan.FromMinutes(5);

    // Inherited from WarpConfiguration
    options.DefaultQueue = "default";
});
```

### Worker

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `RunWorker` | `bool` | `true` | Whether this server runs the job worker. Call `DisableWorker()` (which sets this to `false`) for a service-only server: background services + server infrastructure, no worker hosts, no job-only server tasks, no `Worker`/`WorkerGroup` rows. **Do not** set `WorkerCount = 0` while leaving the worker enabled — `AddWarpServer` throws at registration for that contradiction (it would orchestrate jobs but never execute them). Use `DisableWorker()` instead. |
| `WorkerCount` | `int` | `min(CPU * 5, 20)` | Number of concurrent worker threads |
| `WebhookWorkerCount` | `int` | `4` | Workers in the dedicated group that runs webhook attempts (polls only `warp:webhooks`); the default group no longer polls it, so slow receivers cannot take job workers. `0` restores the pre-8.1 shape (the default group drains `warp:webhooks`). Ignored when `Queues` or an `AddWorkerGroup` group lists `warp:webhooks` (explicit routing wins). Must be 0 or more. New in 8.1. See [Webhook workers](/docs/features/webhooks#webhook-workers). |
| `PrefetchCount` | `int?` | `null` (= 0, no prefetch) | **Dispatcher mode only.** How many jobs the dispatcher may claim beyond the workers that are free to start one. Unset or `0` claims only what a worker can start immediately, the fairest across servers. A positive value buffers jobs ahead so a worker that finishes starts its next job without a claim round trip: about 16% more empty jobs per second at `WorkerCount` on one server. A prefetched job is already `Processing` in the database while it waits in the in-memory buffer, so other servers cannot take it, it waits behind this server's running jobs, and its claim ages against `InvisibilityTimeout`. Raise it for many very short jobs on one server or evenly loaded servers; leave it at 0 for long-running jobs. Before 8.0, unset meant `WorkerCount`. |
| `PollingInterval` | `TimeSpan` | `10 seconds` | Delay between polls when no jobs are available. Also serves as the floor for exponential backoff — it resets to this floor the moment a job is processed, and in-process enqueue signals (and DB push) shortcut it entirely. |
| `MaxPollingInterval` | `TimeSpan` | `30 seconds` | Upper bound on the polling delay during idle periods. The delay grows from `PollingInterval` by `PollingIntervalFactor` on each empty poll, clamped to this value, and resets instantly when a job is processed. |
| `PollingIntervalFactor` | `double` | `2.0` | Multiplier applied to the current polling delay on each consecutive empty poll. Set to `1.0` (or lower) to disable exponential backoff — the delay stays at `PollingInterval`. |
| `Queues` | `string[]` | follows `DefaultQueue` | Queues this worker subscribes to, processed in alphabetical order. Left untouched, the implicit group polls `[DefaultQueue]` — the queue untargeted publishes actually land on — so setting only `DefaultQueue` cannot strand jobs. An explicitly set value wins outright and is never widened. |

### Exponential Polling Backoff

On idle queues, the poll delay grows geometrically from `PollingInterval` (floor) toward `MaxPollingInterval` (ceiling) by `PollingIntervalFactor` on each consecutive empty poll. The delay resets to the floor instantly when any job is processed, so latency remains bounded by `PollingInterval` under load.

With defaults (`10s` → `30s`, factor `2.0`), an idle worker backs off through `10s → 20s → 30s` before capping at 30s. A burst of work resets it back to 10s immediately — and an in-process enqueue signal (or DB push) shortcuts the wait entirely, so the backoff governs how often an idle worker asks the database, not how long a newly enqueued job waits.

To disable backoff entirely, set `PollingIntervalFactor = 1.0`. The delay then stays at `PollingInterval` on every poll.

Paused workers/groups always poll at the floor (no backoff while paused).

### Handler Logging

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `EnableHandlerLogging` | `bool` | `true` | When true, handler `ILogger` output is captured and stored in the JobLog table. Set to `false` to suppress handler log capture (lifecycle events like Created/Completed are always recorded). |

### Cancellation

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `CancellationCheckInterval` | `TimeSpan` | `5 seconds` | How often the worker checks if a running job has been cancelled. Also refreshes the keep-alive timestamp. |

### Dispatcher Mode

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `UseDispatcher` | `bool` | `true` | One dispatcher per worker group claims jobs in batches and completions are written in batches. `false` selects single-worker mode (the default before 8.0). |
| `CompletionBatchSize` | `int` | `50` | Dispatcher mode only. Max job completions buffered per worker before an automatic flush |
| `CompletionFlushInterval` | `TimeSpan` | `100ms` | Dispatcher mode only. Max age of the oldest buffered completion before flush |

By default (since 8.0) each worker group has one dispatcher that claims jobs for all its free workers in one statement and hands them to the workers through an in-memory channel. Finished jobs are buffered per group and written in one statement — tune `CompletionBatchSize` / `CompletionFlushInterval`, or set `CompletionBatchSize = 1` to start a write as soon as anything is buffered. See [Batched Completions](/docs/features/batched-completions) for the trade-offs. In the [Warp vs Hangfire](/docs/performance/warp-vs-hangfire) run, dispatcher mode issued 0.22 statements and allocated 15 KB per empty job, against 7.03 statements and 130 KB in single-worker mode.

What dispatcher mode changes, compared with single-worker mode:

- A finished job shows as `Processing` until its batch is written (up to `CompletionFlushInterval`, 100 ms by default).
- After a crash, jobs whose completion was still buffered run again (stale-job recovery requeues them unless they are `[NoRestart]`).
- Prefetch is off by default: the dispatcher claims only for workers that are free. [`PrefetchCount`](#worker) buffers jobs ahead for more throughput on many short jobs, at the cost that a prefetched job is `Processing` on the server that claimed it, so it waits behind that server's running jobs even while another server has idle workers, and waits for crash recovery if that server dies.

Set `UseDispatcher = false` for single-worker mode, where each worker claims, runs and completes one job at a time and writes its completion straight away.

### Log Flushing

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `LogFlushInterval` | `TimeSpan` | `1 second` | How often the job monitor drains buffered handler `ILogger` output into the JobLog table. Lower values surface dashboard logs faster at the cost of more DB writes. |

### Worker Groups

By default, all workers share the same queues and polling interval. Use worker groups for fine-grained control:

```csharp
builder.Services.AddWarpServer<AppDbContext>(options =>
{
    // Top-level settings become the first worker group
    options.WorkerCount = 5;
    options.Queues = ["critical"];
    options.PollingInterval = TimeSpan.FromMilliseconds(100);

    // Additional groups
    options.AddWorkerGroup(group =>
    {
        group.WorkerCount = 2;
        group.Queues = ["reports", "default"];
        group.PollingInterval = TimeSpan.FromSeconds(5);
        group.MaxPollingInterval = TimeSpan.FromSeconds(60);
        group.PollingIntervalFactor = 2.0;
    });
});
```

This creates 7 workers total: 5 polling `critical` every 100ms, and 2 polling `reports`/`default` every 5s. All workers share the same server identity and health monitoring.

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `WorkerCount` | `int` | `min(CPU * 5, 20)` | Number of workers in this group |
| `PrefetchCount` | `int?` | `null` (= 0, no prefetch) | Prefetch depth for this group, dispatcher mode only. See the server-level option above. |
| `Queues` | `string[]` | `["default"]` | Queues this group subscribes to |
| `PollingInterval` | `TimeSpan` | `10 seconds` | Delay between polls for this group. Also the floor for exponential backoff. |
| `MaxPollingInterval` | `TimeSpan` | `30 seconds` | Upper bound on the polling delay during idle periods for this group |
| `PollingIntervalFactor` | `double` | `2.0` | Multiplier on each consecutive empty poll for this group. Set to `1.0` to disable backoff. |

### Server Identity

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `ServerName` | `string?` | `null` (uses `MachineName.ServerId`) | Display name shown in the dashboard |
| `ServerId` | `Guid` | Auto-generated | Unique server ID. Override only if you need deterministic IDs |

### Health & Crash Recovery

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `HealthCheckInterval` | `TimeSpan` | `5 seconds` | How often the health manager runs (heartbeat, stale job detection, cleanup) |
| `HealthCheckTimeout` | `TimeSpan` | `5 minutes` | Time without heartbeat before a server is considered dead and removed |
| `InvisibilityTimeout` | `TimeSpan` | `5 minutes` | Time without keep-alive before a processing job is considered stale and requeued. Workers refresh keep-alive every `InvisibilityTimeout / 5` |
| `ClockSkewWarningThreshold` | `TimeSpan?` | `1 second` | How far a server's clock may be from the database's before it logs a Warning, raises a `ClockSkewDetected` notification and is flagged on the dashboard. Warp corrects for the offset whatever its size (see [Clock skew](crash-recovery.md#clock-skew-between-servers)); `null` turns the report off |

### Job Retention

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `JobExpirationTimeout` | `TimeSpan` | `1 day` | How long completed/deleted jobs are kept before cleanup (inherited from `WarpConfiguration`) |
| `ExpirationBatchSize` | `int` | `1000` | Batch size for cleanup operations |
| `MaxExpirableJobCount` | `int?` | `null` | Max jobs with `ExpireAt` to retain. Oldest deleted first. `null` = disabled (no cap). |

:::info[Failed jobs never expire]
Failed jobs have `ExpireAt = null` and are never automatically deleted. They must be manually deleted or requeued from the dashboard.
:::

### Background Task Intervals

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `OrchestrationInterval` | `TimeSpan` | `10 seconds` | How often every batch and message in flight is checked for completion. A finished, deleted or recovered job has its parent checked at once; this sweep is the backstop for a wake that never arrived |
| `MessageRoutingInterval` | `TimeSpan` | `10 seconds` | Message routing poll interval |
| `ScheduledActivationInterval` | `TimeSpan` | `10 seconds` | How often `ScheduledJobActivation` flips `State.Scheduled` jobs to `Enqueued`. Controls worst-case latency between a job's `ScheduleTime` and when it becomes eligible for pickup |
| `CounterAggregationInterval` | `TimeSpan?` | `1 minute` | How often `CounterAggregator` folds pending `Counter` rows into `Statistic` totals. Dashboard counter graphs refresh at this cadence. `null` disables the scheduled run |
| `CounterBufferFlushInterval` | `TimeSpan` | `2 seconds` | How often this process writes its in-memory counter increments out as `Counter` rows. **This is the window in which an ungraceful exit loses metrics** — a graceful stop flushes. Must be greater than zero (validated at `AddWarpServer`). See [how counters reach the database](/docs/features/queue-metrics#how-counters-reach-the-database) |
| `ServerCleanupInterval` | `TimeSpan` | `30 seconds` | Dead server cleanup interval |
| `StaleJobRecoveryInterval` | `TimeSpan` | `30 seconds` | Stale job recovery interval |
| `ExpirationCleanupInterval` | `TimeSpan` | `5 minutes` | Expiration cleanup interval |
| `MetricRollupInterval` | `TimeSpan?` | `1 minute` | How often `MetricRollup` compacts and downsamples custom application metric rows (inherited from `WarpConfiguration`; see [Custom application metrics](#custom-application-metrics)). `null` disables the scheduled run |

## Queue Ordering

Queues are processed in **alphabetical order**. Use prefixes to control priority:

```csharp
options.Queues = ["a-critical", "b-default", "c-low"];
```

A worker always picks up jobs from `a-critical` before `b-default`, and `b-default` before `c-low`. Within a queue, jobs are ordered by schedule time.

A queue is drained **completely** before the next one is looked at — priority here is strict, not weighted, so a permanently busy `a-critical` starves `b-default` indefinitely. Webhook deliveries are not affected since 8.1: they run on their own group (`WebhookWorkerCount`). With `WebhookWorkerCount = 0` the implicit group binds `[DefaultQueue, "warp:webhooks"]` and webhook deliveries wait for the default queue to drain. If one queue must not be starved by another, give it its own worker group (`opt.AddWorkerGroup()`) with its own workers rather than relying on ordering.

Ordering is **ordinal** (`StringComparer.Ordinal`), not your database's collation — for ASCII queue names the two agree, which is every name Warp itself produces. A worker subscribed to several queues issues one claim statement per queue and spends its budget down queue by queue; a worker subscribed to one queue — the common case — issues exactly one.
