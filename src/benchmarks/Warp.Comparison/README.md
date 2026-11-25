# Warp vs Hangfire

A comparison anyone can read and run again: one program, `Warp.Comparison`, that puts Warp and Hangfire through the
same steps against the same PostgreSQL, and a workflow, `.github/workflows/comparison.yml`, that runs it on a GitHub
runner. The published figures come from that workflow; the run's page has the table and the raw results.

## What is compared

Three arms, each with the same number of servers and workers (default 1 server × 10 workers):

| Arm | Configuration |
|---|---|
| `warp-dispatcher` | `AddWarpServer` with `UsePostgreSql()`, `WorkerCount` and `UseDispatcher = true`, every other setting at its default. Dispatcher mode is the default since 8.0: one claim serves the worker group and completions are written in batches. |
| `warp-single-worker` | The same with `UseDispatcher = false`: each worker claims, runs and completes its own job (the default before 8.0). |
| `hangfire` | Hangfire 1.8.25 with Hangfire.PostgreSql 1.21.1, its default filters (automatic retry, statistics, continuations), `WorkerCount`, and `QueuePollInterval` at 1 s instead of 15 s, so an idle worker looks again after a second. |

Two jobs, identical in both libraries (`Jobs.cs`):

- **noop**: an empty handler. It measures the library alone: claiming a job, running it, recording that it finished.
- **work**: reads one row and updates it (`SELECT` then `UPDATE` on a 1,000-row table, the same SQL through Npgsql in both), so the job's own database work shares the server with the queue.

## How a run goes

1. A fresh database. The library creates its schema; for `work` the row table is filled.
2. **Warm-up**, outside the measurement, on servers of its own: 200 jobs through the whole path, so the measured run does not pay for compiling code, building the EF model or the library's first-use setup.
3. **Preload**: N jobs are enqueued with no server running (Warp: one save per 1,000 jobs; Hangfire: 8 parallel clients, since it has no batch enqueue).
4. **The window**: the servers start, the harness waits until all N jobs have completed, the servers stop.

## What is measured, over the window

| Row | Source |
|---|---|
| Time to drain | Wall clock from starting the servers to the last job completed. |
| Jobs/s, first job to last | N divided by the time between the first and last completion, read from the library's own records (Warp's `Completed` log rows, Hangfire's `Succeeded` states). |
| Jobs/s, p10 to p90 | The rate over the middle 80% of completions, without ramp-up and tail. |
| Statements, WAL, database time | `pg_stat_statements` for the benchmark database: calls, `wal_bytes`, execution and planning time. |
| Commits | `pg_stat_database.xact_commit` for the benchmark database. |
| Worker process CPU, allocated | `Process.TotalProcessorTime` and `GC.GetTotalAllocatedBytes` of the process running the servers. |
| Peak and idle connections | `pg_stat_activity` for the benchmark database, sampled every 50 ms. |

The harness's own work is left out of every row: its queries carry the tag `comparison-harness` (left out of the
statements), run on a connection with that application name (left out of the connections), and are subtracted from
the commits; the counters are read from a connection to the server's default database. Each row is divided by N.

## Run it

On GitHub: Actions › Comparison › Run workflow (inputs: jobs, servers, workers, rounds, arms, job kinds), or
`gh workflow run comparison.yml -f workers=10 -f servers=1`.

Locally, against a PostgreSQL with `pg_stat_statements` loaded (`.github/scripts/start-bench-postgres.sh` starts one):

```bash
dotnet build -c Release src/benchmarks/Warp.Comparison/Warp.Comparison.csproj
bin=src/benchmarks/Warp.Comparison/bin/Release/net10.0/Warp.Comparison
docker exec warpbench psql -U postgres -c "CREATE DATABASE cmp_hangfire"
$bin run --engine=hangfire --job=noop --jobs=20000 --servers=1 --workers=10 \
  --conn="Host=localhost;Username=postgres;Password=postgres;Database=cmp_hangfire" --out=results/hangfire.json
$bin summarize --results=results
```

Use a fresh database per run.

## Reading it fairly

- Both libraries run in one process on the same machine as PostgreSQL, which is capped at 2 CPUs and 4 GB.
- An empty job measures overhead, not your workload: a job that takes two seconds takes two seconds in either.
- Hangfire's PostgreSQL storage is a community package; its first-party SQL Server storage may behave differently. No SQL Server comparison is run here.
- Hangfire is referenced as a NuGet package (LGPL-3.0); none of its code is copied into this repository.
