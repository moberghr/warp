---
sidebar_position: 5
---

# Job Cancellation

Warp uses a `CancellationMode` enum for graceful job cancellation. When you cancel a processing job, it doesn't immediately change state — the handler gets a chance to stop cleanly.

## How It Works

1. **Cancel request**: `DeleteJob(id)` on a processing job sets `CancellationMode = Graceful`. The job stays in `Processing` state.
2. **Worker detects**: `RunJobMonitor` polls every `CancellationCheckInterval` (default 5s). When it sees `CancellationMode != None`, it cancels the handler's `CancellationToken`.
3. **Handler responds**: If the handler respects the token, it throws `OperationCanceledException`. The worker catches it and sets the job to `Deleted`.
4. **Handler ignores**: If the handler completes despite cancellation, the job is marked `Completed` — the work happened, and that's the truth.

```
DeleteJob(processingJobId)
  → CancellationMode = Graceful (state stays Processing)
  → RunJobMonitor detects CancellationMode
  → Handler's CancellationToken cancelled
  → Handler stops → state = Deleted, ExpireAt set
  → Handler finishes anyway → state = Completed
```

## Dashboard

Processing jobs with `CancellationMode = Graceful` show a **"Cancelling..."** badge in orange instead of the normal "Processing" badge. This is visible in both the job list and job detail pages.

The job remains in the Processing tab until the handler actually exits — the dashboard shows the truth about what the worker is doing.

## Writing Cancellable Handlers

Always check `CancellationToken` in long-running handlers:

```csharp
public class LongRunningHandler : IJobHandler<LongRunningRequest>
{
    public async Task HandleAsync(LongRunningRequest message, CancellationToken ct)
    {
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessItem(item);
        }
    }
}
```

If your handler ignores the token, cancellation still works — but the handler runs to completion and the job is marked `Completed`, not `Deleted`.

## Cancelling a Batch

`CancelBatch(batchId)` cancels a whole batch in one call (surfaced as a **Cancel** action on the batch detail page). It is **transitive**: it walks the `ParentJobId` tree breadth-first from the batch and gracefully cancels every non-terminal descendant — not just the direct children, but also pending continuations reached *through* children that have already finished. Jobs already in a terminal state (`Completed`, `Failed`, `Deleted`) are left untouched, so cancelling a mostly-finished batch only affects the work still outstanding.

Each cancelled job follows the same graceful path as a single `DeleteJob`: `CancellationMode = Graceful`, the handler's token is cancelled on the next monitor tick, and the job settles to `Deleted` once the handler exits (or `Completed` if it ignores the token and finishes). Passing a non-batch id throws `ArgumentException`.

## CancellationMode Enum

| Value | Description |
|-------|-------------|
| `None` (0) | No cancellation requested |
| `Graceful` (1) | Cancel token, wait for handler to exit |

The enum is designed for future extension (e.g., `Force` for thread abort).
