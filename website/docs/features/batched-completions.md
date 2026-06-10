---
sidebar_position: 10
---

# Batched Completions (Dispatcher Mode)

When running in dispatcher mode (`UseDispatcher = true`), each worker buffers job completions in memory and flushes them as a single multi-row transaction. This collapses N per-job commits into one, cutting database round-trips on high-throughput workloads.

## Setup

Batched completions are automatic when dispatcher mode is enabled. Tune the batch size and flush interval if the defaults don't fit your workload:

```csharp
builder.Services.AddWarpServer<AppDbContext>(config =>
{
    config.UseDispatcher = true;
    config.CompletionBatchSize = 50;                         // default: 50
    config.CompletionFlushInterval = TimeSpan.FromMilliseconds(100); // default: 100ms
});
```

Opt out by setting `CompletionBatchSize = 1` — each completion flushes immediately, equivalent to the non-dispatcher path.

## How It Works

Each `WarpDispatcherWorker` owns its own in-memory batch. When a handler finishes:

1. The worker builds a `PendingCompletion` containing the mutated `Job` row (with its new terminal state), the counters to insert, and any JobLog entries.
2. The completion is added to the worker's batch. If the batch is full or the flush interval has elapsed, the batch commits immediately. Otherwise it waits for more completions.
3. When the worker runs out of channel work and is about to suspend, it drains any buffered completions first. The worker never blocks on `WaitToReadAsync` with unsaved completions.
4. On graceful shutdown, `StopAsync` calls a final `FlushAsync()` so SIGTERM doesn't strand the in-flight batch.

`FlushAsync` always commits to completion — there is no caller-visible way to cancel a drained batch. The DB operations inside use `CancellationToken.None` so a shutdown mid-flush still persists. The transactional commit + split-on-poison recursion means a single bad row in a batch of 50 isolates and drops only the poison entry; the other 49 good rows still commit.

## Poison Entry Isolation

If a batch commit fails with `DbUpdateException` (e.g. one of the 50 rows was deleted by another server between processing and flush), `FlushRangeAsync` recursively splits the batch in half and retries each half. Single-entry failures are logged and dropped — `StaleJobRecovery` will later observe the orphaned `Processing` row and decide whether to requeue or fail it based on the `CanBeRestarted` metadata.

Non-`DbUpdateException` failures (connection drops, timeouts) propagate up and the drained entries are lost for this flush cycle. `StaleJobRecovery` handles those too.

## Trade-offs

Batched completions are a throughput optimization, not a latency optimization. Consequences:

- **At-least-once semantics** — if the process crashes between handler completion and the batch flush, the job is observed as still `Processing` by the stale-recovery task and (depending on `CanBeRestarted`) requeued. For non-idempotent handlers, pair with `[NoRestart]` or a handler-internal idempotency check.
- **Delayed visibility** — a job's terminal state doesn't appear in the dashboard until the batch commits (up to `CompletionFlushInterval`). For 100ms flush intervals this is imperceptible; if you raise it significantly, operators see stale "Processing" rows.
- **Batch size vs. lock contention** — larger batches amortize round-trips but hold row-level locks on more rows per transaction. 50 is a sensible default for PostgreSQL and SQL Server; tune down if you see lock-wait metrics climbing.

## When To Use

Enable `UseDispatcher = true` with batched completions when:

- **High-throughput** — workers process hundreds of jobs per second per server.
- **Short-running handlers** — the DB round-trip for the completion write is a meaningful fraction of total handler time.
- **Idempotent or `[NoRestart]`-protected handlers** — the at-least-once trade-off is acceptable.

For low-throughput or long-running handlers, the non-dispatcher mode (one commit per job) is simpler and has the same per-job latency.

## Configuration

| Option | Type | Default | Description |
|---|---|---|---|
| `UseDispatcher` | `bool` | `false` | Enables dispatcher mode + batched completions |
| `CompletionBatchSize` | `int` | `50` | Max completions buffered before an automatic flush |
| `CompletionFlushInterval` | `TimeSpan` | `100ms` | Max age of the oldest buffered completion before flush |
