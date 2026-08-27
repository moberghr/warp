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

    // Dispatcher mode (batch-fetch instead of per-worker polling)
    options.UseDispatcher = false;

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
    options.CounterAggregationInterval = TimeSpan.FromSeconds(5);
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
| `PrefetchCount` | `int?` | `null` (= `WorkerCount`) | **Dispatcher mode only.** How many jobs the dispatcher may claim beyond the workers that are free to start one. A prefetched job is already `Processing` in the database while it waits in the in-memory buffer, so it is unavailable to other servers and its claim ages against `InvisibilityTimeout` — depth buys pickup latency at the cost of fairness across servers. Set `0` to claim only what a worker can start immediately — fairest across servers, at some throughput cost on bursty short jobs; raise it for bursty, very short jobs where the claim round-trip dominates. `null` keeps the historical behaviour of buffering one job per worker. |
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
| `UseDispatcher` | `bool` | `false` | When true, uses a batch-fetch dispatcher instead of per-worker polling |
| `CompletionBatchSize` | `int` | `50` | Dispatcher mode only. Max job completions buffered per worker before an automatic flush |
| `CompletionFlushInterval` | `TimeSpan` | `100ms` | Dispatcher mode only. Max age of the oldest buffered completion before flush |

By default, each worker polls the database independently for the next job. With `UseDispatcher = true`, a single dispatcher thread batch-fetches jobs and distributes them to workers via an in-memory channel. This reduces database load when running many workers, at the cost of slightly higher latency for the first job in a batch.

In dispatcher mode, each worker also buffers job completions and commits them as a single multi-row transaction — tune `CompletionBatchSize` / `CompletionFlushInterval` or set `CompletionBatchSize = 1` to opt out. See [Batched Completions](/docs/features/batched-completions) for trade-offs.

Use dispatcher mode when you have many workers (20+) and want to reduce database polling pressure. For most setups, the default per-worker polling is simpler and works well.

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
| `PrefetchCount` | `int?` | `null` (= `WorkerCount`) | Prefetch depth for this group, dispatcher mode only. See the server-level option above. |
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

### Job Retention

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `JobExpirationTimeout` | `TimeSpan` | `1 day` | How long completed/deleted jobs are kept before cleanup (inherited from `WarpConfiguration`) |
| `ExpirationBatchSize` | `int` | `1000` | Batch size for cleanup operations |
| `MaxExpirableJobCount` | `int?` | `null` | Max jobs with `ExpireAt` to retain. Oldest deleted first. `null` = disabled (no cap). |

:::info Failed jobs never expire
Failed jobs have `ExpireAt = null` and are never automatically deleted. They must be manually deleted or requeued from the dashboard.
:::

### Background Task Intervals

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `OrchestrationInterval` | `TimeSpan` | `10 seconds` | Fallback sweep interval for parent finalization |
| `MessageRoutingInterval` | `TimeSpan` | `10 seconds` | Message routing poll interval |
| `ScheduledActivationInterval` | `TimeSpan` | `10 seconds` | How often `ScheduledJobActivation` flips `State.Scheduled` jobs to `Enqueued`. Controls worst-case latency between a job's `ScheduleTime` and when it becomes eligible for pickup |
| `CounterAggregationInterval` | `TimeSpan` | `5 seconds` | Counter aggregation interval |
| `ServerCleanupInterval` | `TimeSpan` | `30 seconds` | Dead server cleanup interval |
| `StaleJobRecoveryInterval` | `TimeSpan` | `30 seconds` | Stale job recovery interval |
| `ExpirationCleanupInterval` | `TimeSpan` | `5 minutes` | Expiration cleanup interval |

## Queue Ordering

Queues are processed in **alphabetical order**. Use prefixes to control priority:

```csharp
options.Queues = ["a-critical", "b-default", "c-low"];
```

A worker always picks up jobs from `a-critical` before `b-default`, and `b-default` before `c-low`. Within a queue, jobs are ordered by schedule time.
