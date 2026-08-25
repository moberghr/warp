---
sidebar_position: 3
---

# Recurring Jobs

Cron-based scheduled jobs with name, cron expression, type, next/last execution times, and the outcome of the last run.

## How Recurring Jobs Work

`AddOrUpdateRecurringJob` only registers (or updates) the recurring job definition — it does **not** create any job instances. The `RecurringJobScheduler` background task monitors all definitions and creates a new job each time the cron expression fires.

## Execution History

Each recurring job tracks its executions via `RecurringJobLog` entries. The history table shows the outcome of each scheduled run:
- Normal executions link to the job and show its current state
- If the underlying job has been deleted, the entry displays **"Cleaned up"**
- If the recurring job was disabled at the time, the entry displays an orange **"Skipped"** badge

The list page condenses this into a **Last Result** column showing the state of the most recent *real* run (skipped firings are not runs, so a disabled job keeps showing the outcome of its last actual execution). The badge links to that job. A definition that has never fired shows `—`; one whose job row has since been cleaned up shows **"Cleaned up"**.

## Dashboard Actions

- **Enable / Disable** — toggle whether the scheduler creates real jobs or skips. Disabled jobs still fire on schedule but record "Skipped" entries in the history instead. The recurring job list shows an **Enabled** or **Disabled** badge per row. A disabled definition shows `—` for **Next Execution** (it will not execute while disabled) and its **Last Execution** stays pinned to the last occurrence that actually ran — `Never` if it never ran.
- **Trigger** — immediately creates and enqueues a new job instance, regardless of the cron schedule or disabled state
- **Delete** — removes the recurring job definition (existing job instances are not affected)

For full documentation on configuring and using recurring jobs, see [Recurring Jobs](/docs/features/recurring-jobs).

import Screenshot from '@site/src/components/Screenshot';

<Screenshot light="/img/screenshots/07-recurring.png" dark="/img/screenshots/07-recurring-dark.png" alt="Recurring Jobs" />

Click a recurring job name to see its detail page with execution history:

<Screenshot light="/img/screenshots/14-recurring-detail.png" dark="/img/screenshots/14-recurring-detail-dark.png" alt="Recurring Detail" />
