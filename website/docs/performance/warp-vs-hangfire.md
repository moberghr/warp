---
title: Warp vs Hangfire
description: Throughput, database load, CPU, memory and connections per job for Warp 8.0 and Hangfire on the same PostgreSQL, with the harness in the repository.
---

# Warp vs Hangfire

Both libraries were given the same jobs, the same server and worker count and the same PostgreSQL server, and each was measured from starting its server to the last job finishing. The harness is in the repository, [`src/benchmarks/Warp.Comparison`](https://github.com/moberghr/warp/tree/main/src/benchmarks/Warp.Comparison), and its README describes every step and every measured row. The [Comparison workflow](https://github.com/moberghr/warp/actions/workflows/comparison.yml) runs it on a GitHub runner; anyone can run it again.

## Setup

| | |
|---|---|
| Machine | GitHub `ubuntu-24.04` runner (4 vCPUs). PostgreSQL 18.7 in a container on the same machine, capped at 2 CPUs and 4 GB. |
| Servers and workers | 1 server × 10 workers, for every arm |
| Warp (dispatcher, default) | `AddWarpServer` with `UsePostgreSql()` and `WorkerCount = 10`, every other setting at its default. Dispatcher mode is the default since 8.0: one claim serves the worker group, and completions are written in batches. |
| Warp (single-worker) | The same with `UseDispatcher = false`: each worker claims, runs and completes its own job. This was the default before 8.0. |
| Hangfire | Hangfire 1.8.25 with Hangfire.PostgreSql 1.21.1, default filters, `WorkerCount = 10`, and `QueuePollInterval` lowered from 15 s to 1 s. |
| Jobs | 20,000 per run, enqueued before the server starts. A warm-up of 200 jobs runs first, on servers of its own. |
| Runs | 3 per arm, a fresh database each, the arms' order rotating between rounds. Each cell is the median; the range is in brackets. |

Measured in [run 45617194190](https://github.com/moberghr/warp/actions/runs/45617194190) on 25 November 2026, on the 8.0 branch before release. That run still had single-worker mode as the default and labels its arms `warp` (single-worker) and `warp-dispatcher`; the harness now names them `warp-single-worker` and `warp-dispatcher`.

## Empty jobs

The handler does nothing, so this measures the library alone: claiming a job, running it, and recording that it finished.

| | Warp (dispatcher, default) | Warp (single-worker) | Hangfire |
|---|---:|---:|---:|
| Time to drain 20,000 jobs | **2.1 s** (2.0–2.1) | 11.2 s (10.9–11.3) | 34.7 s (34.7–34.8) |
| Jobs/s, first job to last | **10,388** | 1,794 | 577 |
| Statements per job | **0.22** | 7.03 | 40.03 |
| Commits per job | **0.09** | 2.01 | 21.02 |
| WAL per job | 1,993 B | 1,984 B | 4,643 B |
| Database time per job | **0.061 ms** | 0.726 ms | 3.444 ms |
| Worker process CPU per job | **0.15 ms** | 1.04 ms | 2.45 ms |
| Allocated per job | **15.2 KB** | 130.2 KB | 75.0 KB |
| Peak connections | **5** | 14 | 13 |
| Idle connections (average) | **4.0** | 8.6 | 6.2 |

With default settings (dispatcher mode) Warp drained the queue 16 times sooner than Hangfire; in single-worker mode, 3 times sooner. Single-worker mode allocates more memory per job than Hangfire: each worker claims and finalizes its own job. That is why the dispatcher is the default from 8.0.

## Jobs that use the database

Each job reads one row and updates it, with identical SQL through Npgsql in both libraries. This is closer to a real workload, where the handler's own work shares the database with the queue.

| | Warp (dispatcher, default) | Warp (single-worker) | Hangfire |
|---|---:|---:|---:|
| Time to drain 20,000 jobs | **5.3 s** (5.2–5.3) | 13.8 s (13.8–13.9) | 36.4 s (36.1–36.6) |
| Jobs/s, first job to last | **3,892** | 1,457 | 551 |
| Statements per job (the job itself issues 2) | **3.39** | 10.03 | 43.03 |
| Commits per job | **3.17** | 5.01 | 24.02 |
| WAL per job | 2,068 B | 2,070 B | 4,462 B |
| Database time per job | **0.261 ms** | 0.855 ms | 3.561 ms |
| Worker process CPU per job | **0.41 ms** | 1.21 ms | 2.47 ms |
| Allocated per job | **25.1 KB** | 134.8 KB | 95.4 KB |
| Peak connections | 15 | 21 | **13** |
| Idle connections (average) | 10.2 | 15.8 | **6.4** |

With default settings (dispatcher mode) Warp drained the queue 7 times sooner than Hangfire; in single-worker mode, 2.6 times sooner. Hangfire held fewer connections here: Warp's handlers opened their own connection for the job's work alongside the queue's.

## What the rows mean

- **Time to drain** is the wall clock from starting the server to the last job completed. **Jobs/s, first job to last** is the job count over the time between the first and last completion, read from each library's own records.
- **Statements**, **WAL** and **database time** come from PostgreSQL's `pg_stat_statements`, **commits** from `pg_stat_database`, both for the benchmark database only, so they include everything the library sends.
- **Worker process CPU** and **allocated** are the process that runs the server, divided by the job count.
- **Peak** and **idle connections** sample `pg_stat_activity` every 50 ms.
- The harness's own queries are left out of every row.

## Reading these numbers fairly

- The runs are on PostgreSQL only. Hangfire's PostgreSQL storage is a community package, and its first-party SQL Server storage may behave differently. A SQL Server comparison has not been run.
- An empty job measures overhead, not your workload. If a job takes 2 seconds of its own, both libraries finish it in about 2 seconds; the difference is in what the queue costs the database and how much throughput is left when jobs are short.
- Hangfire's poll interval was lowered from its 15 s default, which favours Hangfire. Its default filters (automatic retry, statistics) stayed on, as they are in an application.
- One server on one shared runner. Run the [harness](https://github.com/moberghr/warp/tree/main/src/benchmarks/Warp.Comparison) on your own hardware before you rely on the numbers.

## Why the gap is this large

See [how Warp keeps the database quiet](./index.md#how-warp-keeps-the-database-quiet). In short: a batch claim, nothing written between the claim and the handler, one keep-alive statement for all running jobs, and completions written many at a time.

## Moving over

[Migrating from Hangfire](../operations/migrating-from-hangfire.md) maps the Hangfire API onto Warp's.
