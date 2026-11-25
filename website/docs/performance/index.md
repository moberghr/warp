---
title: Performance
description: What Warp costs per job, how it compares, and which settings to turn on for throughput.
---

# Performance

Warp's queue lives in your application database, so every statement the queue issues competes with your own queries. Warp 8.0 was built to issue as few as possible.

## At a glance

Measured on PostgreSQL for the unreleased 8.0. Each cell is the range across repeated runs. The second column ran with 10 workers.

| | Empty jobs | Jobs that read and update a row |
|---|---|---|
| Throughput, first job to last | 4,633–7,464 jobs/s | 1,105–1,446 jobs/s |
| Database statements per job | 0.23–0.24 | 3.3–3.4 (2 are the job's own) |
| PostgreSQL CPU per job | 0.11–0.20 ms | 0.49–0.53 ms |
| Worker CPU per job | 0.12–0.13 ms | 0.52–0.57 ms |
| Allocated per job | 13.7 KB | 19–23 KB |

{/* VERIFY: figures from the comparison summary of 2026-11-25; the empty-job run did not state its worker count. Update from the detailed report. */}

- **[Warp vs Hangfire](./warp-vs-hangfire.md)**: the same jobs on the same database. Warp drained the queue 9 to 34 times sooner, depending on the workload.
- **[8.0 against 7.7](../releases.md#a-faster-worker-and-less-database-work)**: jobs per second and statements per job for 15 scenarios, on both databases, from the CI perf workflow.
- **[Memory benchmarks](../operations/benchmarks.md)**: the leak test and how to run the benchmark projects.

## How Warp keeps the database quiet

These apply to dispatcher mode (`UseDispatcher = true`) unless noted.

- **Jobs are claimed in batches.** One statement claims work for every free worker in a group, so workers do not each poll the table.
- **Nothing is written between the claim and your handler.** The "Processing" log row is written later, with the first monitor flush or with the completion.
- **One keep-alive statement per worker group.** A single loop renews every running job and reads cancellations, where each job once had its own.
- **Completions are written many at a time.** Workers hand a finished job to the group's buffer and take the next one. One statement writes the batch. See [Batched Completions](../features/batched-completions.md).
- **Handlers are dispatched by generated code.** The source generator emits a typed call for each job type, so running a job involves no reflection. This applies in both modes.
- **The worker never builds your `DbContext`.** A handler that does not touch the database does not pay for one. This applies in both modes.
- **Job indexes are partial.** Four indexes cover only the rows that are waiting, so most state changes write fewer index entries.
- **Ids are time-ordered (UUID version 7).** On PostgreSQL, inserts land on the newest index pages.
- **Idle servers stay idle.** Cluster-wide tasks run once per tick for the whole cluster, and an idle run writes nothing.

## Settings for throughput

The defaults favour simplicity: each worker polls for its own job and commits its own completion. For a busy queue, turn on the dispatcher and database push:

```csharp
builder.Services.AddWarpServer<AppDbContext>(opt =>
{
    opt.UsePostgreSql();          // or opt.UseSqlServer()

    opt.UseDispatcher = true;     // batch claim + batched completions
    opt.UseDatabasePush();        // wake on enqueue instead of polling
});
```

| Setting | What it changes | Cost |
|---|---|---|
| [`UseDispatcher`](../operations/configuration.md#dispatcher-mode) | One claim serves the whole worker group, and completions are written in batches. | A finished job shows as `Processing` until its batch is written (up to 100 ms by default). After a crash, jobs whose completion was still buffered run again. |
| [`UseDatabasePush()`](../features/db-push.md) | Workers wake within about 50 ms of an enqueue, on any server. | Needs a session-mode connection on PostgreSQL, and Service Broker on SQL Server. |
| [`CompletionBatchSize`](../features/batched-completions.md) | How many completions one statement writes (default 50). | Larger batches lock more rows per transaction. |
| [`AddWorkerGroup`](../operations/configuration.md#worker-groups) | Separate worker pools per queue, so a bulk queue cannot starve a critical one. | More groups to size. |

Handlers must be safe to run more than once in every mode. Mark the ones that are not with [`[NoRestart]`](../features/no-restart.md).

## Measuring your own workload

The repository has three benchmark projects under `src/benchmarks/`:

- `Warp.ServerBenchmarks`: full-server scenarios (throughput, statements per job, allocation), the memory leak test, and the load labs.
- `Warp.Benchmarks`: the in-memory mediator against MediatR.
- `Warp.PerfTest`: database statements per second for an idle or lightly loaded server.

Run the labs from a container on the same Docker network as the database. Going through Docker Desktop's port forward adds about 0.9 ms to every round trip, which is enough to dominate the result. `src/benchmarks/Warp.ServerBenchmarks/Lab/README.md` has the commands.

Two runs of the same workload can differ by several percent. Interleave the things you are comparing and take the median of several runs.
