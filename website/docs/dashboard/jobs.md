---
sidebar_position: 2
---

# Jobs

Browse jobs by state using the left sidebar: Enqueued, Scheduled, Processing, Completed, Failed, Awaiting, Deleted.

Each state shows a count. Bulk requeue or delete with checkboxes.

Under a separator below them sits [Retrying](/docs/dashboard/retrying) — jobs waiting on their next
attempt. It is a filtered view over Scheduled and Enqueued rather than a state of its own, so those
rows are counted in both places.

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
- **Flow** — Trace ID, spawned-by link, message link, continuations and spawned jobs (the first 50 of each, with the total)
- **Trace** — All jobs sharing the same TraceId (click to navigate)
- **Sibling Jobs** — Other jobs from the same message
- **Child Jobs** — Continuation jobs waiting on this one

**Right column:**
- **Reported Progress** — One bar per name the handler passed to `IJobContext.ReportProgress`, showing its latest value. Refreshes every 2 seconds until the job finishes
- **History** — Colored state cards (Created → Processing → Completed/Failed) with timestamps and durations
- **Handler Output** — Pipeline behavior logs and ILogger output captured during execution, newest first, with previous/next paging (100 lines per page by default). Until the job finishes, the first page refreshes every 2 seconds
- **Exception** — Full stack trace on failed jobs

### Running Job

While a job runs, the newest handler output sits at the top and the first page refreshes every 2 seconds, so you can watch it work. A chatty job pages back through older lines with the previous and next buttons; those pages hold still while you read them. Background tabs pause until you return.

<Screenshot light="/img/screenshots/30-job-detail-running.png" dark="/img/screenshots/30-job-detail-running-dark.png" alt="Running Job Detail" />

### Completed Job with Trace

<Screenshot light="/img/screenshots/03-job-detail-trace.png" dark="/img/screenshots/03-job-detail-trace-dark.png" alt="Job Detail with Trace" />

### Failed Job with Exception

<Screenshot light="/img/screenshots/09-job-detail-failed.png" dark="/img/screenshots/09-job-detail-failed-dark.png" alt="Failed Job Detail" />
