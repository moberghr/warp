---
sidebar_position: 4
---

import Screenshot from '@site/src/components/Screenshot';

# Applications

Every running Warp process, grouped by application. This page was called **Servers** until applications
landed; `/servers` still redirects here.

The roster merges two kinds of process, because every server is an instance but not every instance is a
server:

- **Servers** — processes running `AddWarpServer`, with worker groups and background tasks.
- **Application instances** — `AddWarp`-only processes (publishers, APIs, dashboards) that never run a
  server. They appear once `WarpConfiguration.ApplicationName` is set, and only then.

<Screenshot light="/img/screenshots/08-applications.png" dark="/img/screenshots/08-applications-dark.png" alt="Applications page listing every running Warp process grouped by application" />

Set the identity a process reports:

```csharp
builder.Services.AddWarp<AppDbContext>(options =>
{
    options.ApplicationName = "orders-api";     // cluster-wide identity; groups instances together
    options.ApplicationVersion = "1.4.2";       // per-instance, may differ mid rolling-deploy
    options.ApplicationEnvironment = "prod";
});
```

`ApplicationName` gates all of it: leave it `null` and the behaviour is exactly as before — only servers
appear, and nothing new is written.

Click an application to see its instances, and an instance to see its detail.

<Screenshot light="/img/screenshots/27-application-detail.png" dark="/img/screenshots/27-application-detail-dark.png" alt="Application detail showing instances and job execution stats" />

## Background tasks

Each Warp **server** runs a set of background tasks for orchestration and maintenance. A service-only
server (`opt.DisableWorker()`) runs only the shared ones — `Heartbeat`, `ServerCleanup`, `ExpirationCleanup`.

| Task | Default interval | Purpose |
|---|---|---|
| **Heartbeat** | 5s (`HealthCheckInterval`) | Updates the server heartbeat so other servers know it is alive; renews singleton background-service leases |
| **ScheduledJobActivation** | 10s (`ScheduledActivationInterval`) | Flips due `Scheduled` jobs to `Enqueued` — the worst-case latency between a schedule time and pickup eligibility |
| **MessageRouting** | 10s (`MessageRoutingInterval`) + signal | Routes `IMessage` jobs to their handlers by creating child jobs |
| **Orchestration** | 10s (`OrchestrationInterval`) + signal | Finalizes parent jobs when all children complete, activates continuations |
| **RecurringJobScheduler** | 15s (`RecurringJobSchedulerInterval`) | Creates job instances when a recurring cron expression fires |
| **StaleJobRecovery** | 30s (`StaleJobRecoveryInterval`) | Requeues jobs stuck in `Processing` after a worker crash, and recovers stuck webhook deliveries |
| **ServerCleanup** | 30s (`ServerCleanupInterval`) | Removes dead servers that stopped sending heartbeats |
| **AggregateCounters** | 1m (`CounterAggregationInterval`) | Rolls write-optimized `Counter` rows into `Statistic` rows for the dashboard |
| **BacklogSampler** | 60s (`BacklogSampleInterval`) | Samples per-queue backlog depth and oldest age off the worker hot path |
| **AggregateErrorGroups** | 60s (`ErrorGroupingInterval`) | Drains the error-occurrence inbox into `ErrorGroup` issues |
| **EvaluateSlos** | 1m (`SloEvaluationInterval`) | Computes attainment, error budget and burn rate for each objective |
| **ExpirationCleanup** | 5m (`ExpirationCleanupInterval`) | Deletes expired jobs, call logs, deliveries and other retention-capped rows |
| **RollupStatistics** | 10m (`StatisticRollupInterval`) | Downsamples time-bucketed statistics fine → hourly → daily |

Every interval is configurable — see [Configuration](/docs/operations/configuration#background-task-intervals).
Setting one to `null` disables that task.

The server detail page shows each task's last status, duration and run time, alongside its worker groups:

<Screenshot light="/img/screenshots/15-server-detail.png" dark="/img/screenshots/15-server-detail-dark.png" alt="Server detail with worker groups and background task status" />

Individual workers have their own page — see [Workers](/docs/dashboard/workers).

## See also

- [Multi-application observability](/docs/features/applications) — how the roster, provenance and per-app
  metrics work.
- [Pause and resume](/docs/features/pause) — pausing a server or a single worker group.
