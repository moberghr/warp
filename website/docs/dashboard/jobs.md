---
sidebar_position: 2
---

# Jobs

Browse jobs by state using the left sidebar: Enqueued, Scheduled, Processing, Completed, Failed, Awaiting, Deleted.

Each state shows a count. Bulk requeue or delete with checkboxes.

### Retrying

Below the state list, under a separator, sits **Retrying** — jobs whose last attempt threw and which
are waiting to run again. It is a **filtered view, not a state**: there is no `State.Retrying`, so
every job listed here is *also* listed under Scheduled (or Enqueued, when the retry schedule is
empty). The counts in the sidebar therefore do not sum to the job total, which is why the entry is
separated from the states above it.

The list adds two columns: **Attempt** (`#3 (2 failed)` — the attempt about to run, and how many have
already failed) and **Next attempt**, the instant the retry is scheduled for.

**Next attempt** counts down live and reads `due now` once it passes — the attempt is waiting to be
activated and claimed, not finished. The same is true of the **Scheduled** column on the Scheduled
list. See [Timestamps and countdowns](/docs/dashboard/overview#timestamps-and-countdowns).

The tab is shown by default. A deployment that never retries can hide it:

```csharp
app.MapWarpDashboard(o => o.ShowRetries(false));
```

:::note Why a config switch rather than auto-detection
Unlike the other addon-driven pages, this one has no service to probe: retrying jobs are found by
reading `Job.Metadata`, which any dashboard can do whether or not `AddRetry()` ran in *this* process.
Auto-detection would also be the wrong signal — in a dashboard-only deployment the workers hold the
addons and the dashboard holds none, so the tab would be hidden over rows sitting in the database.
See [Dashboard nav declarations](#dashboard-nav-declarations).
:::

### Dashboard nav declarations

Most nav items are shown when the matching addon is registered **in the dashboard's own process**.
That inference is wrong for a dashboard-only host (`AddWarp` + a provider, no `AddWarpServer`): the
addons live in the worker processes, so pages get hidden over data that exists. Where the dashboard
can still serve the page, the host can say so explicitly:

```csharp
app.MapWarpDashboard(o => o
    .ConfigureMenu(m => m.Pages(WarpDashboardPage.Dashboard, WarpDashboardPage.Jobs))
    .ShowAdapters()      // outbound calls are recorded by the workers, not here
    .ShowEndpoints()
    .ShowClient()
    .ShowSlo()
    .ShowRetries(false)); // ...and this one turns a shown-by-default tab off
```

These sit beside `ConfigureMenu` on the same options object deliberately: `ConfigureMenu` decides
*where* a nav item sits, these decide *whether* it exists at all, and both are "how the dashboard
looks" rather than how it is secured.

An explicit declaration wins; without one, detection behaves exactly as before.

There is deliberately **no** `ShowSagas`, `ShowConcurrency` or `ShowRateLimits`. For those three the
probed service *is* the page's query service (`ISagaQueryService`, `IConcurrencyLimitManager`,
`IRateLimitManager`), and only `AddSagas()` / `AddConcurrency()` / `AddRateLimit()` register it —
never `AddWarp`. Forcing those nav items on would produce a page whose every request answers 404. To
show them, register the addon in the dashboard process.

Processing jobs that are being gracefully cancelled display a **"Cancelling..."** badge instead of the normal Processing badge. This indicates the worker has received the cancellation signal (`CancellationMode = Graceful`) and the handler's `CancellationToken` has been triggered, but the handler has not yet completed.

### Failed Jobs Type Filter

The Failed state includes a type count bar at the top of the job list. Each bar segment represents a job type with its failure count. Click a type to filter the list to only that type. When a type filter is active, **"Delete All"** and **"Requeue All"** buttons appear, allowing bulk operations on all failed jobs of that specific type.

### Requeue Behavior

Requeueing a job resets its `ScheduleTime` to now, so the job executes immediately rather than retaining its original schedule time.

import Screenshot from '@site/src/components/Screenshot';

<Screenshot light="/img/screenshots/02-jobs-failed.png" dark="/img/screenshots/02-jobs-failed-dark.png" alt="Failed Jobs" />

## Job Detail

Click any job to see its full detail in a two-column layout:

**Left column:**
- **Payload** — The serialized job data
- **Details** — Type, handler, timestamps, retry count
- **Flow** — Trace ID, spawned-by link, message link, continuation link
- **Trace** — All jobs sharing the same TraceId (click to navigate)
- **Sibling Jobs** — Other jobs from the same message
- **Child Jobs** — Continuation jobs waiting on this one

**Right column:**
- **History** — Colored state cards (Created → Processing → Completed/Failed) with timestamps and durations
- **Handler Output** — Pipeline behavior logs and ILogger output captured during execution
- **Exception** — Full stack trace on failed jobs

### Completed Job with Trace

<Screenshot light="/img/screenshots/03-job-detail-trace.png" dark="/img/screenshots/03-job-detail-trace-dark.png" alt="Job Detail with Trace" />

### Failed Job with Exception

<Screenshot light="/img/screenshots/09-job-detail-failed.png" dark="/img/screenshots/09-job-detail-failed-dark.png" alt="Failed Job Detail" />
