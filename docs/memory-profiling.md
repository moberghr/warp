# Memory Profiling Report

Profiled on 2026-04-15. Measures memory allocation, retention, and leak behavior of the Warp server under sustained load.

## Test Environment

| Component | Details |
|-----------|---------|
| CPU | Intel Core Ultra 7 255U (12 cores / 14 threads) |
| RAM | 32 GB |
| OS | Windows 11 Enterprise |
| .NET SDK | 10.0.201 |
| Runtime | .NET 10.0.5, X64 RyuJIT AVX2 |
| GC | Concurrent Workstation |
| Database | PostgreSQL (latest, via Testcontainers) |
| BenchmarkDotNet | 0.14.0 |

## Tooling

Benchmarks live in `src/benchmarks/Warp.ServerBenchmarks/`. Three types of measurements:

- **BenchmarkDotNet benchmarks** (`[MemoryDiagnoser]`) — per-operation allocation tracking
- **`MemoryDiagnoser`** — its `Allocated` column is process-wide, covering the worker and background-task threads, not only the thread BenchmarkDotNet invokes. `AllocationAttributionBenchmark` is the check that this holds: it allocates the same amount on the benchmark thread and on another, and the two rows must agree.
- **Stress test** — standalone test that runs N rounds of jobs, measuring heap retention after each round via `GC.GetTotalMemory()` with aggressive collection

### How to run

```bash
# BenchmarkDotNet benchmarks
dotnet run --project benchmarks/Warp.ServerBenchmarks -- --filter *ScopeMemory*
dotnet run --project benchmarks/Warp.ServerBenchmarks -- --filter *WorkerMemory*
dotnet run --project benchmarks/Warp.ServerBenchmarks -- --filter *ServerMemory*

# Stress test (configurable workers, jobs per round, rounds)
dotnet run --project benchmarks/Warp.ServerBenchmarks -- stress --workers=10 --jobs=10000 --rounds=10
```

## Results

### 1. Scope Lifecycle (create / resolve / dispose)

Every worker cycle and background task iteration creates a DI scope, resolves services, and disposes. This is the fundamental operation that could leak if scopes are not properly disposed.

| Method | Mean | Allocated |
|--------|------|-----------|
| CreateAndDisposeScope | 16.15 us | 34.02 KB |
| CreateScopeAndQuery | 1,145.32 us | 79.34 KB |
| CreateScopeAndResolvePublisher | 16.80 us | 34.34 KB |

**Findings:**
- Scope create/resolve/dispose costs a flat 34 KB — consistent, no accumulation.
- Adding a DB query adds ~45 KB (EF Core query compilation, connection checkout, result materialization).
- Publisher resolution costs the same as a plain scope — DI wiring is lightweight.

### 2. Worker Execution Path (per-job)

Calls `GetAndProcessJob()` directly on the benchmark thread — isolates a single worker cycle with no background task noise. Measures the full path: fetch (row lock + transaction), deserialize, execute handler, finalize state, write logs + counters.

| Method | Mean | Allocated |
|--------|------|-----------|
| GetAndProcessJob | 16.86 ms | 358.02 KB |

**Findings:**
- A single job cycle allocates 358 KB end-to-end.
- Includes 2 transactions, 2 DbContext scopes, JSON deserialization, handler execution, counter + log writes.

### 3. Full Server (5 workers, all background tasks)

Boots a real server with 5 workers + the background tasks against PostgreSQL. `Allocated` is process-wide, so it includes those threads.

| Method | JobCount | Mean | Total Allocated (all threads) | Per-Job |
|--------|----------|------|-------------------------------|---------|
| ServerIdle | - | 5,009 ms | 6.95 MB | - |
| ProcessJobs | 200 | 935 ms | 10.14 MB | 50.7 KB |
| ProcessJobs | 2,000 | 6,275 ms | 99.67 MB | 49.8 KB |
| ProcessJobs | 10,000 | 22,530 ms | 450 MB | 49.7 KB |
| ProcessMessages | 200 | 1,241 ms | 13.12 MB | 65.6 KB |
| ProcessMixed | 200 | 862 ms | 13.78 MB | 68.9 KB |

**Findings:**
- Per-job allocation is constant at ~50 KB regardless of scale (200 vs 10,000 jobs). This confirms no accumulation.
- Idle server allocates ~7 MB over 5 seconds (background tasks creating/disposing scopes at 100ms-500ms intervals). This is normal GC churn, not retention.
- Message routing adds ~15 KB/job overhead (MessageRoutingTask + OrchestrationTask + child job creation).
- Allocation scales linearly: 10x more jobs = 10x more allocation. No superlinear growth.

### 4. Memory Leak Stress Test (10 workers, 100,000 jobs)

The definitive leak test. Boots a server with 10 workers, processes 100,000 jobs in 10 rounds of 10,000 each. After each round: forces full GC (aggressive Gen2 collection + finalizers) and measures heap size.

```
Round    Jobs       Time    Heap (MB)  Delta (MB)  Retained (MB)  Jobs/sec
────────────────────────────────────────────────────────────────────────────
base     0          -       10.8       -           -              -
1        10,000     23.7s   10.9       +0.1        +0.1           421
2        20,000     22.4s   10.9       -0.1         0.0           447
3        30,000     23.4s   10.9        0.0        +0.1           427
4        40,000     23.9s   10.9        0.0        +0.1           419
5        50,000     21.4s   10.9        0.0        +0.1           467
6        60,000     20.2s   10.9        0.0        +0.1           496
7        70,000     20.7s   10.9        0.0        +0.1           483
8        80,000     21.2s   10.9        0.0        +0.1           471
9        90,000     21.5s   10.9        0.0        +0.1           465
10       100,000    21.5s   11.0       +0.1        +0.2           465
────────────────────────────────────────────────────────────────────────────
Baseline heap:    10.8 MB
Final heap:       11.1 MB
Total retained:   +0.3 MB
Per-job retained: 0.00 KB
```

**Findings:**
- **No memory leak.** Heap is stable at ~10.9 MB across all 10 rounds. After 100,000 jobs, only 0.3 MB retained — within normal GC noise.
- **Per-job retained memory: 0.00 KB** — DI scopes are disposed correctly, EF Core change trackers are cleaned up, AsyncLocal state (`JobLogContext.Current`, `JobExecutionContext.Current`) is cleared.
- **Throughput is consistent** at 420-496 jobs/sec with no degradation over time. If GC pressure were growing, throughput would decrease in later rounds.
- **10 workers + 9 background tasks** all running concurrently with zero accumulation.

## Conclusion

The Warp server has no detectable memory leaks under sustained load. Key design decisions that contribute to this:

- **Scoped DI lifetimes**: Worker scopes and handler scopes are created and disposed per job. Background task scopes are created and disposed per iteration.
- **Separate worker and handler scopes**: The handler's DbContext change tracker is disposed with the handler scope before the worker finalizes job state — preventing cross-scope entity tracking leaks.
- **AsyncLocal cleanup in finally blocks**: `JobLogContext.Current` and `JobExecutionContext.Current` are cleared in both success, error, cancellation, and finally paths in `WarpWorkerService.GetAndProcessJob()`.
- **Activity disposal**: `WarpTelemetry` activities are stopped and disposed in the finally block.
- **Mutex lock handles**: Released via `IAsyncDisposable` in the finally block.
