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
| `DefaultQueue` | `string` | `"default"` | Queue used when no queue is specified at publish time |
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

Configure retry behavior via `opt.AddRetry()` inside the `AddWarpWorker` lambda:

```csharp
builder.Services.AddWarpWorker<AppDbContext>(opt =>
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

Per-job override via `[Retry]` attribute on handler or job class, or per-enqueue via metadata. See [Jobs](/docs/patterns/jobs#retries).

## Concurrency Configuration

Enable concurrency control (Mutex + Semaphore) via `opt.AddConcurrency()` inside the `AddWarpWorker` lambda:

```csharp
builder.Services.AddWarpWorker<AppDbContext>(opt =>
{
    opt.AddConcurrency();
});
```

No options — just register and use `.WithMutex("key")` / `[Mutex("key")]` for at-most-one, or `.WithSemaphore("key", N)` / `[Semaphore("key", N)]` for at-most-N concurrent jobs at publish time. See [Concurrency control](/docs/features/mutex) for details.

## Circuit Breaker Configuration

Enable the circuit breaker via `opt.AddCircuitBreaker()` inside the `AddWarpWorker` lambda:

```csharp
builder.Services.AddWarpWorker<AppDbContext>(opt =>
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

Per-handler overrides on `[CircuitBreaker]` use `Group`, `Threshold`, `DurationSeconds`, and `ResetJitterSeconds`. The `CircuitBreakerState` entity is part of Warp's base schema (registered by `AddWarp` unconditionally), so no separate migration is required when you turn the addon on. See [Circuit Breaker](/docs/features/circuit-breaker) for details.

## NoRestart Configuration

Enable the stale-recovery opt-out via `opt.AddNoRestart()` inside the `AddWarpWorker` lambda:

```csharp
builder.Services.AddWarpWorker<AppDbContext>(opt =>
{
    opt.AddNoRestart();
});
```

No options. Register it to make `[NoRestart]` / `[Restart]` attributes take effect at publish time. `.WithRestart(bool)` works without the addon. See [NoRestart](/docs/features/no-restart) for details.

The fleet-wide default is controlled by `WarpWorkerConfiguration.RestartStaleJobsByDefault` (default `true`). Flip to `false` to fail stale jobs on crash unless they explicitly opt in.

## Worker Configuration (`WarpWorkerConfiguration`)

Extends `WarpConfiguration`. Used by the worker side (`AddWarpWorker<TContext>`):

```csharp
builder.Services.AddWarpWorker<AppDbContext>(options =>
{
    // Worker
    options.WorkerCount = 10;
    options.PollingInterval = TimeSpan.FromSeconds(1);    // floor
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
    options.HealthCheckInterval = TimeSpan.FromSeconds(3);
    options.HealthCheckTimeout = TimeSpan.FromMinutes(5);
    options.InvisibilityTimeout = TimeSpan.FromMinutes(5);

    // Job retention
    options.JobExpirationTimeout = TimeSpan.FromDays(1);
    options.ExpirationBatchSize = 1000;
    options.MaxExpirableJobCount = null; // Null = unlimited

    // Background task intervals
    options.OrchestrationInterval = TimeSpan.FromSeconds(10);
    options.MessageRoutingInterval = TimeSpan.FromSeconds(1);
    options.ScheduledActivationInterval = TimeSpan.FromSeconds(5);
    options.CounterAggregationInterval = TimeSpan.FromSeconds(5);
    options.ServerCleanupInterval = TimeSpan.FromSeconds(30);
    options.StaleJobRecoveryInterval = TimeSpan.FromSeconds(30);
    options.ExpirationCleanupInterval = TimeSpan.FromSeconds(60);

    // Inherited from WarpConfiguration
    options.DefaultQueue = "default";
});
```

### Worker

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `WorkerCount` | `int` | `min(CPU * 5, 20)` | Number of concurrent worker threads |
| `PollingInterval` | `TimeSpan` | `1 second` | Delay between polls when no jobs are available. Also serves as the floor for exponential backoff. |
| `MaxPollingInterval` | `TimeSpan` | `30 seconds` | Upper bound on the polling delay during idle periods. The delay grows from `PollingInterval` by `PollingIntervalFactor` on each empty poll, clamped to this value, and resets instantly when a job is processed. |
| `PollingIntervalFactor` | `double` | `2.0` | Multiplier applied to the current polling delay on each consecutive empty poll. Set to `1.0` (or lower) to disable exponential backoff — the delay stays at `PollingInterval`. |
| `Queues` | `string[]` | `["default"]` | Queues this worker subscribes to. Processed in alphabetical order |

### Exponential Polling Backoff

On idle queues, the poll delay grows geometrically from `PollingInterval` (floor) toward `MaxPollingInterval` (ceiling) by `PollingIntervalFactor` on each consecutive empty poll. The delay resets to the floor instantly when any job is processed, so latency remains bounded by `PollingInterval` under load.

With defaults (`1s` → `30s`, factor `2.0`), an idle worker backs off through `1s → 2s → 4s → 8s → 16s → 30s` before capping at 30s. A burst of work resets it back to 1s immediately.

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
builder.Services.AddWarpWorker<AppDbContext>(options =>
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
| `Queues` | `string[]` | `["default"]` | Queues this group subscribes to |
| `PollingInterval` | `TimeSpan` | `1 second` | Delay between polls for this group. Also the floor for exponential backoff. |
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
| `HealthCheckInterval` | `TimeSpan` | `3 seconds` | How often the health manager runs (heartbeat, stale job detection, cleanup) |
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
| `MessageRoutingInterval` | `TimeSpan` | `1 second` | Message routing poll interval |
| `ScheduledActivationInterval` | `TimeSpan` | `5 seconds` | How often `ScheduledJobActivation` flips `State.Scheduled` jobs to `Enqueued`. Controls worst-case latency between a job's `ScheduleTime` and when it becomes eligible for pickup |
| `CounterAggregationInterval` | `TimeSpan` | `5 seconds` | Counter aggregation interval |
| `ServerCleanupInterval` | `TimeSpan` | `30 seconds` | Dead server cleanup interval |
| `StaleJobRecoveryInterval` | `TimeSpan` | `30 seconds` | Stale job recovery interval |
| `ExpirationCleanupInterval` | `TimeSpan` | `60 seconds` | Expiration cleanup interval |

## Queue Ordering

Queues are processed in **alphabetical order**. Use prefixes to control priority:

```csharp
options.Queues = ["a-critical", "b-default", "c-low"];
```

A worker always picks up jobs from `a-critical` before `b-default`, and `b-default` before `c-low`. Within a queue, jobs are ordered by schedule time.
