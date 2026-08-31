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

Each job created by the scheduler is logged in `RecurringJobLog`. The dashboard shows execution history on the recurring job detail page, and the **list** condenses it into a **Last Result** column — the outcome of the most recent real run, carried on `RecurringJobModel` as `HasLastRun` / `LastJobId` / `LastState` / `LastRunCleanedUp`. A skipped firing is not a run, so a disabled recurring job keeps showing the outcome of its last actual execution rather than blanking out. The **Last Execution** timestamp links to that run's job, alongside the Last Result badge.

The `RecurringJobLog` has a FK to `Job` with `SET NULL` cascade. When a job expires and is cleaned up, the log entry survives with `JobId = null`. The last 100 entries per recurring job are retained.

### Outcomes outlive their jobs

`JobExpirationTimeout` defaults to **1 day**, so for anything less frequent than daily the job row is normally gone before the next firing. To keep the history meaningful, `ExpirationCleanup` **stamps the outcome onto `RecurringJobLog.FinalState` immediately before deleting the job** — both in the age-based sweep and in the count-based one (`MaxExpirableJobCount`). The dashboard then reads the live `Job.CurrentState` while the row exists and falls back to `FinalState` once it doesn't, rendering e.g. **`Completed` (cleaned up)**: the result is shown, but it is not a link, because there is no job detail page left to open.

The stamp happens at cleanup time rather than at finalization deliberately — recording it when the job finishes would put a lookup on the worker's hot path.

This also un-hides a case the old bare "Cleaned up" label conflated with success: **failed jobs never auto-expire**, so anything swept was either `Completed` or `Deleted` — and a `Deleted` recurring run (a skip-mode concurrency or rate-limit refusal, or a graceful cancellation) used to be indistinguishable from a clean success.

Four states are distinguishable on the list:

| Last Result | Meaning |
|---|---|
| `—` | `HasLastRun = false` — the definition has never actually fired |
| badge, linked | the job row is still there; click through to its detail page |
| badge + `(cleaned up)` | the job row was swept, but its outcome was preserved |
| `Cleaned up` | swept before 6.1, when outcomes were not yet preserved — unrecoverable |

`FinalState` is one nullable column, so the upgrade is additive: run your usual `dotnet ef migrations add` / `database update` (added in **6.1**). Runs swept before the upgrade keep reading as the bare `Cleaned up` — nothing backfills them, because the information is genuinely gone.

## Cron Expressions

The dashboard's **Schedule** column reads the expression back in plain English — `0 8 * * *` shows as "At 08:00 AM" — with the raw expression on hover. Scanning a list of schedules is what that column is for, and the prose answers it faster; the expression is what you need when reading or copying the real thing, which is a hover away.

**The column header is the switch.** It names whichever half is in the cell (**Schedule** for the reading, **Cron** for the expression) and clicking it swaps the two, so it can never label the wrong one. The choice persists per browser, so anyone who thinks in cron flips it once. Neither half is ever unreachable — whichever is not in the cell is the hint.

An expression that cannot be parsed has no reading, so it always displays as the expression itself. The detail page header shows both at once, no hover needed.

Standard 5-part cron (minute, hour, day, month, weekday) and 6-part with seconds:

```
* * * * *       Every minute
0 * * * *       Every hour
0 9 * * *       Daily at 9 AM
0 0 * * 1       Every Monday at midnight
*/5 * * * *     Every 5 minutes
0 9 * * 1-5     Weekdays at 9 AM
```

## Policy attributes on recurring job types

Recurring firings are staged directly by the scheduler — they never pass through the publish pipeline — but contract-declared policy is resolved at execution, so `[Mutex]`, `[Semaphore]`, `[RateLimit]`, `[Timeout(Scope = PerAttempt)]`, `[Retry]` and `[CircuitBreaker]` on the job type all apply to firings. A recurring job is the most likely thing you want to mutex: with `[Mutex]` on the type, a firing that starts while the previous one is still running is skipped (`Deleted`) or requeued per the configured `ConcurrencyMode`.

One exception: `[Timeout(Scope = Total)]` measures from enqueue and needs a publish-time deadline, which this path cannot produce — the attribute is refused on recurring firings (Warp logs a one-time warning), and the firing then behaves as if unattributed: a configured `PerAttempt` global default still applies to it. Use `Scope = PerAttempt`.

## Manual Trigger

Trigger a recurring job immediately — from code, from the dashboard, or over the API. `IRecurringJobService` keys on the **name** you registered the definition under, so a code caller needs nothing it does not already have:

```csharp
var svc = serviceProvider.GetRequiredService<IRecurringJobService>();
await svc.TriggerRecurringJob("session-cleanup");
```

The definition's cron schedule is untouched: the trigger stages one extra job with `ScheduleTime = now`, writes its `RecurringJobLog` entry, and fires the `JobEnqueued` wake, so a worker claims it without waiting out its polling backoff. `NextExecution` still points at the next natural cron occurrence.

An explicit trigger deliberately **ignores `DisabledAt`** — it is an operator override, so a disabled definition still produces a real job (see [Behavior](#behavior)).

A name no definition matches throws `ArgumentException`. The name is trimmed before lookup, so surrounding whitespace never causes a miss.

:::info The name is the identity
Every single-definition method on `IRecurringJobService` — `TriggerRecurringJob`, `EnableRecurringJob`, `DisableRecurringJob`, `DeleteRecurringJob`, `GetRecurringJob`, `GetRecurringJobHistory` — takes the registered name. It is unique, it is what your code already holds, and unlike the table's surrogate id it survives a delete-and-re-register unchanged. Names are trimmed, must be non-empty, and are capped at 200 characters (the name also names the registration's distributed lock).
:::

## Enable / Disable

Disable a recurring job to temporarily stop it from creating new jobs. The scheduler still fires on schedule, but instead of creating a real job, it records a **skipped** entry in the execution history. This keeps the cron schedule in sync — when you re-enable, the job resumes from the next natural cron occurrence with no catchup burst.

```
POST /api/recurring/{id}/disable
POST /api/recurring/{id}/enable
```

Or use the Enable/Disable button on the dashboard.

:::note `{id}` in the dashboard API
A recurring job name may contain `/` and spaces, so the REST routes carry it as its URL-safe base64 (the same encoding the endpoints and applications routes use): base64 of the UTF-8 bytes with `+`→`-`, `/`→`_`, and trailing `=` trimmed. `session-cleanup` becomes `c2Vzc2lvbi1jbGVhbnVw`. An id that does not decode, or a name no definition matches, answers `404`.
:::

### How It Works

1. **Disable** sets `DisabledAt` timestamp on the recurring job
2. **Scheduler** still picks up the job when `NextExecution <= now`, but sees `DisabledAt != null`
3. Instead of creating a job, it creates a `RecurringJobLog` entry with `Skipped = true` and `JobId = null`
4. `NextExecution` advances normally, so the skip cadence stays cron-paced — a frozen `NextExecution` would leave the row permanently due and record one skipped entry per scheduler tick instead of one per occurrence
5. `LastExecution` does **not** advance — it names the last occurrence that actually ran, and a skip runs nothing. A disabled definition that never fired keeps reading `Never`
6. **Enable** clears `DisabledAt` — next cron tick creates a real job again

The dashboard hides `NextExecution` for a disabled definition (it renders `—`) rather than showing a firing time that will not produce a job. The column is still maintained in the database; only the display is suppressed.

The scheduler's entry in the server-task history counts the two outcomes separately — `Scheduled 3 recurring jobs, skipped 1 disabled` — so a tick that only skipped disabled definitions never reads as if it had scheduled work. A tick with nothing due writes no message at all.

### Behavior

| Scenario | What happens |
|----------|-------------|
| Disable | Scheduler creates "Skipped" log entries instead of jobs; `LastExecution` stops advancing and the dashboard shows `—` for next execution |
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
await recurringJobService.DeleteRecurringJob("session-cleanup");
```

Or use the delete button on the dashboard.
