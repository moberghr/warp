---
title: Warp vs Hangfire
description: Measured throughput, database load and memory per job for Warp 8.0 and Hangfire on PostgreSQL.
---

# Warp vs Hangfire

Both libraries were given the same jobs, the same worker count and the same PostgreSQL server, and each run was measured from the first job starting to the last one finishing.

:::info[Preliminary figures]

These are the first numbers from the comparison harness, measured for the unreleased 8.0. Each cell is the range across repeated runs. The full report, with hardware, versions and the harness source, will replace this page when it is finished.

:::

{/* VERIFY: all figures on this page come from the two summary tables of 2026-11-25; replace them from the detailed report, and fill in hardware, PostgreSQL version, Hangfire and Hangfire.PostgreSql versions, Warp configuration and repeat count. */}

## Summary

| | Warp 8.0 | Hangfire |
|---|---|---|
| Drain 50,000 empty jobs | **7.2–10.6 s** | 228–244 s |
| Drain 20,000 jobs that read and update a row | **14.0–17.7 s** | 151–155 s |
| Database statements per empty job | **0.23–0.24** | 42.8–43.0 |
| Memory allocated per empty job | **13.7 KB** | 79.7 KB |

With empty jobs Warp finished the queue 21 to 34 times sooner. With jobs that do their own database work it finished 9 to 11 times sooner.

## Run 1: empty jobs

50,000 jobs whose handler does nothing. This measures the library alone: claiming a job, running it, and recording that it finished.

| | Warp 8.0 | Hangfire |
|---|---|---|
| Time to drain 50,000 jobs | **7.2–10.6 s** | 228–244 s |
| Throughput, first job to last | **4,633–7,464 jobs/s** | 205–219 jobs/s |
| Throughput, p10–p90 | **7,294–9,272 jobs/s** | 227–242 jobs/s |
| Worker CPU per job | **0.12–0.13 ms** | 3.5–3.6 ms (includes enqueue) |
| PostgreSQL CPU per job | **0.11–0.20 ms** | 4.4–4.8 ms |
| Statements per job | **0.23–0.24** | 42.8–43.0 |
| Commits per job | **0.17** | 19.9–20.0 |
| WAL written per job | **~1,990 B** | ~5,920 B |
| Allocated per job | **13.7 KB** | 79.7 KB |
| Peak database connections | **18** | 30 |

Warp issues fewer than one statement per job because it claims jobs in batches and writes completions in batches, so one statement covers many jobs.

The Hangfire worker CPU figure includes enqueueing the jobs; the Warp figure does not, so that row is not a like-for-like comparison.

## Run 2: jobs that use the database

Each job reads one row and updates it, with identical SQL in both libraries. 10 workers, warm start. This is closer to a real workload, where the handler's own work shares the database with the queue.

| Time to drain | Warp 8.0 | Hangfire |
|---|---|---|
| 1,000 jobs | **1.5–1.9 s** | ~12 s |
| 5,000 jobs | **5.0–5.5 s** | 56–70 s |
| 20,000 jobs | **14.0–17.7 s** | 151–155 s |

| Per job, 20,000 jobs | Warp 8.0 | Hangfire |
|---|---|---|
| Throughput, first job to last | **1,105–1,446 jobs/s** | 130–134 jobs/s |
| PostgreSQL CPU | **0.49–0.53 ms** | 5.5–5.8 ms |
| Worker CPU | **0.52–0.57 ms** | 6.0–6.4 ms |
| Statements (the job itself issues 2) | **3.3–3.4** | 45–46 |
| WAL written | **~2.1 KB** | ~6 KB |
| Allocated | **19–23 KB** | ~106 KB |

Of Warp's 3.3 to 3.4 statements per job, two are the handler's own read and update.

## What the rows mean

- **Throughput, first job to last** is the job count divided by the whole run, including ramp-up. **p10–p90** is the rate over the middle of the run.
- **Worker CPU** and **PostgreSQL CPU** are processor time divided by the job count. PostgreSQL CPU is what the queue costs the database you share with the rest of your application.
- **Statements** and **commits** are counted by PostgreSQL, so they include everything each library sends.
- **WAL** is the write-ahead log volume, which is what replication and backups have to carry.
- **Allocated** is managed memory allocated by the worker process, divided by the job count.

## Reading these numbers fairly

- The runs are on PostgreSQL only. Hangfire's PostgreSQL storage is a community package, and its first-party SQL Server storage may behave differently. A SQL Server comparison has not been run.
- A benchmark of empty jobs measures overhead, not your workload. If a job takes 2 seconds of its own, both libraries finish it in about 2 seconds; the difference is in what the queue costs the database and how much throughput is left when jobs are short.
- These are results from one machine. Run them on your own hardware before you rely on them.

## Why the gap is this large

See [how Warp keeps the database quiet](./index.md#how-warp-keeps-the-database-quiet). In short: a batch claim, nothing written between the claim and the handler, one keep-alive statement for all running jobs, and completions written many at a time.

## Moving over

[Migrating from Hangfire](../operations/migrating-from-hangfire.md) maps the Hangfire API onto Warp's.
