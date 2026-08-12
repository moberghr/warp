---
sidebar_position: 2
---

# Recurring Jobs

Recurring jobs execute on a cron schedule. Warp handles scheduling, deduplication, and execution history.

## Register a Recurring Job

```csharp
await recurringPublisher.AddOrUpdateRecurringJob(
    new CleanupSessions(),
    name: "session-cleanup",
    cron: "0 * * * *");  // Every hour
```

`AddOrUpdateRecurringJob` only registers (or updates) the definition — it does **not** create a job. The `RecurringJobScheduler` background task creates jobs when the cron time arrives.

:::info Saves immediately
`AddOrUpdateRecurringJob` acquires a distributed lock on the job name and calls `SaveChanges` internally. You do **not** need to call `SaveChanges` after this method. The lock prevents race conditions when multiple app instances register the same recurring job concurrently.
:::

## How It Works

1. **Registration**: `AddOrUpdateRecurringJob` stores the cron expression, message payload, and type. Sets `NextExecution` to the next cron occurrence.
2. **Scheduling**: `RecurringJobScheduler` polls every 15 seconds. When `NextExecution <= now`, it creates a job with `ScheduleTime = now` (ready for immediate execution) and updates `NextExecution` to the next cron occurrence.
3. **Deduplication**: Before creating a new job, the scheduler checks the most recent `RecurringJobLog` entry. If that job is still `Enqueued` or `Processing`, it skips — no duplicate jobs.
4. **Execution**: The created job is a regular job. Workers pick it up, execute the handler, and it follows the normal lifecycle. Once the scheduler's transaction commits, it fires a `JobEnqueued` wake — the in-process signal that shortcuts an idle worker's polling backoff, plus the cross-process push notification when `UseDatabasePush()` is enabled — so a firing is claimed promptly instead of waiting out the backoff.

## Execution History

Each job created by the scheduler is logged in `RecurringJobLog`. The dashboard shows execution history on the recurring job detail page — including jobs that have been cleaned up (shown as "Cleaned up").

The `RecurringJobLog` has a FK to `Job` with `SET NULL` cascade. When a job expires and is cleaned up, the log entry survives with `JobId = null`. The last 100 entries per recurring job are retained.

The recurring job **list** condenses this into a **Last Result** column — the state of the most recent real run, carried on `RecurringJobModel` as `HasLastRun` / `LastJobId` / `LastState`. A skipped firing is not a run, so a disabled recurring job keeps showing the outcome of its last actual execution rather than blanking out. `HasLastRun = false` means the definition has never fired; `HasLastRun = true` with a null `LastState` means it fired but the job row has since been cleaned up.

## Cron Expressions

Standard 5-part cron (minute, hour, day, month, weekday) and 6-part with seconds:

```
* * * * *       Every minute
0 * * * *       Every hour
0 9 * * *       Daily at 9 AM
0 0 * * 1       Every Monday at midnight
*/5 * * * *     Every 5 minutes
0 9 * * 1-5     Weekdays at 9 AM
```

## Manual Trigger

Trigger a recurring job immediately from the dashboard or via the API:

```csharp
var svc = serviceProvider.GetRequiredService<IRecurringJobService>();
await svc.TriggerRecurringJob(id);
```

## Enable / Disable

Disable a recurring job to temporarily stop it from creating new jobs. The scheduler still fires on schedule, but instead of creating a real job, it records a **skipped** entry in the execution history. This keeps the cron schedule in sync — when you re-enable, the job resumes from the next natural cron occurrence with no catchup burst.

```
POST /api/recurring/{id}/disable
POST /api/recurring/{id}/enable
```

Or use the Enable/Disable button on the dashboard.

### How It Works

1. **Disable** sets `DisabledAt` timestamp on the recurring job
2. **Scheduler** still picks up the job when `NextExecution <= now`, but sees `DisabledAt != null`
3. Instead of creating a job, it creates a `RecurringJobLog` entry with `Skipped = true` and `JobId = null`
4. `NextExecution` and `LastExecution` advance normally
5. **Enable** clears `DisabledAt` — next cron tick creates a real job again

### Behavior

| Scenario | What happens |
|----------|-------------|
| Disable | Scheduler creates "Skipped" log entries instead of jobs |
| Enable | Next cron tick creates a real job as normal |
| Manual Trigger while disabled | Creates a real job — explicit trigger ignores disabled state |
| `AddOrUpdateRecurringJob` while disabled | Updates the definition (cron, payload) but does not change disabled state |

### Execution History

Skipped executions appear in the dashboard history with an orange **Skipped** badge, giving full visibility into what would have run. This is useful for auditing and confirming the schedule is correct before re-enabling.

## Updating a Recurring Job

Call `AddOrUpdateRecurringJob` again with the same name. The cron expression, payload, and type are updated. `NextExecution` is recalculated.

```csharp
// Change from hourly to every 30 minutes
await recurringPublisher.AddOrUpdateRecurringJob(
    new CleanupSessions(),
    name: "session-cleanup",
    cron: "*/30 * * * *");
```

## Deleting a Recurring Job

```csharp
await recurringJobService.DeleteRecurringJob(id);
```

Or use the delete button on the dashboard.
