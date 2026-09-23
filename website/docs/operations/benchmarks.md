---
sidebar_position: 4
---

# Benchmarks

Memory and performance benchmarks for the Warp server. These verify that the server has no memory leaks and provide baseline allocation numbers for capacity planning.

## Test Environment

| Component | Details |
|-----------|---------|
| CPU | Intel Core Ultra 7 255U (12 cores / 14 threads) |
| RAM | 32 GB |
| OS | Windows 11 Enterprise |
| Runtime | .NET 10.0.5, X64 RyuJIT AVX2 |
| GC | Concurrent Workstation |
| Database | PostgreSQL (latest, Testcontainers) |

## Memory Per Job

How much memory does processing a single job cost?

| Operation | Allocated |
|-----------|-----------|
| DI scope create + dispose | 34 KB |
| DI scope + DB query | 79 KB |
| Full worker cycle (fetch, execute, finalize) | 358 KB |

The full worker cycle includes two transactions, two DbContext scopes, JSON deserialization, handler execution, and counter + log writes.

:::caution Figures predate a measurement fix

The tables below were produced when the benchmarks either could not run at all or reported a column
that measured the wrong process, so treat them as indicative until they are re-measured. Two defects
were found and fixed: the fixture never registered a database provider, so every server benchmark
reported `NA`; and a custom "Total Allocated (all threads)" diagnoser read the host process while the
work happened in a child, reporting `0 GB` beside a real 2.87 GB. That diagnoser has been removed as
redundant — `MemoryDiagnoser` already measures process-wide.

:::

## Server Throughput

Full server with **5 workers** and the background tasks. `Allocated` is process-wide, so it covers the worker and background-task threads rather than only the publishing thread.

| Workload | Jobs | Mean | Allocated | Per Job |
|----------|------|------|-----------------|---------|
| Simple jobs | 200 | 935 ms | 10 MB | 50 KB |
| Simple jobs | 2,000 | 6.3 s | 100 MB | 50 KB |
| Simple jobs | 10,000 | 22.5 s | 450 MB | 50 KB |
| Messages (3 handlers each) | 200 | 1.2 s | 13 MB | 66 KB |
| Mixed (jobs + messages + failures) | 200 | 862 ms | 14 MB | 69 KB |
| Idle (background tasks only) | 0 | 5.0 s | 7 MB | - |

Per-job allocation is constant at ~50 KB regardless of scale. 10x more jobs = 10x more allocation, no superlinear growth.

## Memory Leak Test

The definitive leak test: **10 workers**, 100,000 jobs processed in 10 rounds. After each round, a full GC is forced and the managed heap is measured.

| Round | Total Jobs | Time | Heap (MB) | Retained (MB) | Jobs/sec |
|-------|------------|------|-----------|----------------|----------|
| base | 0 | - | 10.8 | - | - |
| 1 | 10,000 | 23.7s | 10.9 | +0.1 | 421 |
| 2 | 20,000 | 22.4s | 10.9 | 0.0 | 447 |
| 3 | 30,000 | 23.4s | 10.9 | +0.1 | 427 |
| 4 | 40,000 | 23.9s | 10.9 | +0.1 | 419 |
| 5 | 50,000 | 21.4s | 10.9 | +0.1 | 467 |
| 6 | 60,000 | 20.2s | 10.9 | +0.1 | 496 |
| 7 | 70,000 | 20.7s | 10.9 | +0.1 | 483 |
| 8 | 80,000 | 21.2s | 10.9 | +0.1 | 471 |
| 9 | 90,000 | 21.5s | 10.9 | +0.1 | 465 |
| 10 | 100,000 | 21.5s | 11.0 | +0.2 | 465 |

**Result: No memory leak.** The heap stays at ~10.9 MB across all rounds. After 100,000 jobs, total retained memory is 0.3 MB (GC noise). Per-job retained memory is 0.00 KB. Throughput stays consistent at 420-496 jobs/sec with no degradation.

## Running Benchmarks

The benchmark project is at `src/benchmarks/Warp.ServerBenchmarks/`.

```bash
# BenchmarkDotNet — per-operation allocation
dotnet run --project benchmarks/Warp.ServerBenchmarks -- --filter *ScopeMemory*
dotnet run --project benchmarks/Warp.ServerBenchmarks -- --filter *WorkerMemory*
dotnet run --project benchmarks/Warp.ServerBenchmarks -- --filter *ServerMemory*

# Memory leak stress test (configurable)
dotnet run --project benchmarks/Warp.ServerBenchmarks -- stress
dotnet run --project benchmarks/Warp.ServerBenchmarks -- stress --workers=10 --jobs=10000 --rounds=10
```

The stress test boots a real server with Testcontainers, so Docker must be running. No external database setup needed.
