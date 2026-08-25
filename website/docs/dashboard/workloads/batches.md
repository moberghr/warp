---
sidebar_position: 2
---

# Batches

Each batch displays a **stacked progress bar** with green for completed jobs and red for failed jobs, giving an at-a-glance view of batch health. The progress bar uses navigation properties to compute live counts from the current child job states, so it always reflects the real-time status of the batch.

Click through to see individual batch jobs and their states.

import Screenshot from '@site/src/components/Screenshot';

<Screenshot light="/img/screenshots/06-batches.png" dark="/img/screenshots/06-batches-dark.png" alt="Batches" />

## Batch Detail

Shows the stacked green/red progress bar, creation time, full ID, and a table of all jobs in the batch with their current state.

A **Cancel** action cancels the whole batch — every non-terminal descendant (children and pending continuations) is gracefully cancelled; jobs that already finished are left as-is. See [Job Cancellation](/docs/features/cancellation#cancelling-a-batch).

<Screenshot light="/img/screenshots/10-batch-detail.png" dark="/img/screenshots/10-batch-detail-dark.png" alt="Batch Detail" />
