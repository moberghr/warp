---
title: Performance
description: What Warp costs per job, how it compares, and which settings to turn on for throughput.
---

# Performance

Warp's queue lives in your application database, so every statement the queue issues competes with your own queries. Warp 8.0 was built to issue as few as possible.

## At a glance

Measured on PostgreSQL 18 for 8.0, 1 server × 10 workers in dispatcher mode (the default), 20,000 jobs, the median of 3 runs on a GitHub runner ([run](https://github.com/moberghr/warp/actions/runs/45617194190)).

| | Empty jobs | Jobs that read and update a row |
|---|---|---|
| Throughput, first job to last | 10,388 jobs/s | 3,892 jobs/s |
| Database statements per job | 0.22 | 3.39 (2 are the job's own) |
| Database time per job | 0.061 ms | 0.261 ms |
| Worker CPU per job | 0.15 ms | 0.41 ms |
| Allocated per job | 15.2 KB | 25.1 KB |
| Peak database connections | 5 | 15 |

- **[Warp vs Hangfire](./warp-vs-hangfire.md)**: the same jobs on the same database, with the harness in the repository. With default settings (dispatcher mode) Warp drained the queue 7 to 16 times sooner; in single-worker mode, 2.6 to 3 times sooner.
- **[8.0 against 7.7](../releases.md#a-faster-worker-and-less-database-work)**: statements, WAL and connections per job for 19 scenarios, on both databases, from the CI perf workflow.
- **[Memory benchmarks](../operations/benchmarks.md)**: the leak test and how to run the benchmark projects.

## How Warp keeps the database quiet

These apply to dispatcher mode, the default since 8.0, unless noted.

- **Jobs are claimed in batches.** One statement claims work for every free worker in a group, so workers do not each poll the table.
- **Nothing is written between the claim and your handler.** The "Processing" log row is written later, with the first monitor flush or with the completion.
- **One keep-alive statement per worker group.** A single loop renews every running job and reads cancellations, where each job once had its own.
- **Completions are written many at a time.** Workers hand a finished job to the group's buffer and take the next one. One statement writes the batch. See [Batched Completions](../features/batched-completions.md).
- **Handlers are dispatched by generated code.** The source generator emits a typed call for each job type, so running a job involves no reflection. This applies in both modes.
- **The worker never builds your `DbContext`.** A handler that does not touch the database does not pay for one. This applies in both modes.
- **Job indexes are partial.** Four job indexes cover only the rows they are read for (waiting, scheduled, with a parent, with an expiry), so most state changes write fewer index entries.
- **Ids are time-ordered (UUID version 7).** On PostgreSQL, inserts land on the newest index pages.
- **Idle servers stay idle.** Cluster-wide tasks run once per tick for the whole cluster, and an idle run writes nothing.

## Settings for throughput

The dispatcher is on by default: one claim serves a worker group and completions are written in batches. For a busy queue, add database push, which is opt-in because it needs more from the database and the connection:

```csharp
builder.Services.AddWarpServer<AppDbContext>(opt =>
{
    opt.UsePostgreSql();          // or opt.UseSqlServer()

    opt.UseDatabasePush();        // wake on enqueue instead of polling
});
```

| Setting | What it changes | Cost |
|---|---|---|
| [`UseDispatcher`](../operations/configuration.md#dispatcher-mode) (default `true`) | One claim serves the whole worker group, and completions are written in batches. Set it to `false` for single-worker mode, where each worker claims and completes its own job. | A finished job shows as `Processing` until its batch is written (up to 100 ms by default). After a crash, jobs whose completion was still buffered run again. |
| [`PrefetchCount`](../operations/configuration.md#worker) (default 0) | Buffers jobs ahead of the workers, so a worker that finishes starts its next job without a claim round trip: about 16% more empty jobs per second at `WorkerCount` on one server. | A prefetched job waits behind its own server's running jobs even while another server is idle, and waits for crash recovery if that server dies. Keep it at 0 for long-running jobs or several servers. |
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
