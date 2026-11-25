---
sidebar_position: 2
---

# Crash Recovery

Warp uses a **sliding invisibility timeout** to detect and recover from worker/server crashes.

## How It Works

1. When a worker picks up a job, it sets `LastKeepAlive = now` on the job row.
2. During execution, the worker group's `RunningJobMonitor` refreshes `LastKeepAlive` every `CancellationCheckInterval` (default 5 seconds).
3. If the worker crashes, the keep-alive stops. After 5 minutes, the job's `LastKeepAlive` becomes stale.
4. The health manager detects stale jobs and requeues them automatically.

## Key Properties

- **Per-job detection**: Unlike server-level heartbeats, this detects individual worker crashes within a live server.
- **No lost retries**: Crash requeues do NOT count against `MaxRetries`. The job didn't fail — the server died.
- **Long-running jobs are safe**: The keep-alive refreshes continuously, so a job running for hours won't be falsely requeued.
- **Concurrent safety**: Row locking prevents multiple health managers from double-requeuing the same job.
- **Recovered jobs are picked up immediately**: a requeue announces itself like any other enqueue, so a worker is woken as soon as the recovery transaction commits rather than waiting out its polling backoff (up to `MaxPollingInterval`, which `UseDatabasePush()` raises to 5 minutes).

## Configuration

```csharp
builder.Services.AddWarpServer<AppDbContext>(options =>
{
    // How long before a stale job is requeued (default: 5 minutes)
    options.InvisibilityTimeout = TimeSpan.FromMinutes(5);

    // Server heartbeat timeout (default: 5 minutes)
    options.HealthCheckTimeout = TimeSpan.FromMinutes(5);

    // How often health checks run (default: 10 seconds)
    options.HealthCheckInterval = TimeSpan.FromSeconds(10);
});
```

## Clock skew between servers

Recovery compares times that other servers wrote: a job's keep-alive, a server's heartbeat, a lease's expiry. A server whose own clock is wrong would misjudge them. One running ahead would requeue jobs that are still running, remove live servers and fire scheduled jobs early. One running behind would leave crashed work alone for longer.

Warp does not trust any one server's clock for this:

- The heartbeat writes the **database's** time, not the server's.
- Each heartbeat (and server startup) also measures how far the server's clock is from the database's. Every server component then works on the database's clock: keep-alives, claim stamps, stale and cleanup cutoffs, schedule activation, recurring firings and lease expiry. Timers and durations stay on the local clock.
- Past `ClockSkewWarningThreshold` (1 second by default) the server logs a Warning and raises a `ClockSkewDetected` [operational notification](../features/operational-notifications.md) once per excursion. The Applications page and the server's detail page then show a "clock … ahead/behind" badge.

The correction is only as fresh as the last heartbeat, so still keep server clocks in sync with NTP. With the heartbeat disabled (`HealthCheckInterval = null`) nothing is measured and the local clock is used.

**During a rolling deploy from 7.7:** a 7.7 server writes and compares its own clock. While one is alive, 8.0 servers do the same: the offset is measured and stored but not applied, and the heartbeat writes the server's own time, as 7.7 did. They switch to the database's clock once no server from before 8.0 is alive. See [Upgrading to 8.0](upgrading-to-8.md#rolling-deploy-77-and-80-side-by-side).

**Processes without a server** (`AddWarp` only, with `ApplicationName` set) write the database's time on their application-instance heartbeat when a database provider (`UsePostgreSql()` / `UseSqlServer()`) is registered, so a process whose clock lags the database is not swept as stale while alive; while a 7.7 server is alive they write their own time, as 7.7 did. Without a provider they write their own time.

**Not covered:** a process that only publishes stamps its own clock on the jobs it schedules. A future `ScheduleTime` from a skewed publisher is honoured as written.

## Timeline Example

```
T=0:00  Worker picks up Job, sets LastKeepAlive
T=0:05  RunningJobMonitor refreshes LastKeepAlive
T=0:10  RunningJobMonitor refreshes LastKeepAlive
T=0:12  Server crashes. Keep-alive stops.
T=5:12  Health manager: job has no keep-alive for >5 min
        → Job requeued (State = Enqueued, RetriedTimes unchanged)
        → Another worker picks it up and completes it
```
