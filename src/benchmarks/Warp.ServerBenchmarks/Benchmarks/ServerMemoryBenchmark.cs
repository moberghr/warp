using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Warp.Core;
using Warp.Core.Handlers;
using Warp.ServerBenchmarks.Infrastructure;

namespace Warp.ServerBenchmarks.Benchmarks;

/// <summary>
/// Full server benchmarks — boots a real Warp server with workers and all 9 background tasks.
/// Measures total memory allocation per workload across ALL threads.
///
/// [MemoryDiagnoser] tracks the benchmark thread (publishing + waiting).
/// MemoryDiagnoser's Allocated is process-wide, so it already covers the worker and background-task
/// threads where this server does its work (see AllocationAttributionBenchmark).
/// </summary>
[Config(typeof(ServerBenchmarkConfig))]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "BenchmarkDotNet manages lifecycle via [GlobalCleanup].")]
public class ServerMemoryBenchmark
{
    private PostgresServerFixture _fixture = null!;
    private long _heapBefore;

    [Params(10_000)]
    public int JobCount { get; set; }

    [Params(false, true)]
    public bool UseDispatcher { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _fixture = new PostgresServerFixture();
        await _fixture.InitializeAsync(workerCount: 10, useDispatcher: UseDispatcher);

        // Warmup: process some jobs to prime JIT, type caches, connection pool
        var publisher = _fixture.CreatePublisher();
        for (var i = 0; i < 100; i++)
        {
            await publisher.Enqueue(new EmptyRequest());
        }

        await publisher.SaveChangesAsync();
        await _fixture.WaitForCompletion();
        await _fixture.CleanJobTables();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _fixture.DisposeAsync();
    }

    [IterationSetup]
    public void BeforeIteration()
    {
        // Force GC and record heap size BEFORE work — compare with after to detect retention
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        _heapBefore = GC.GetTotalMemory(true);
    }

    [IterationCleanup]
    public void AfterIteration()
    {
        // Force GC and measure heap AFTER work — the delta shows retained (leaked) memory
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        var heapAfter = GC.GetTotalMemory(true);
        var retainedMb = (heapAfter - _heapBefore) / (1024.0 * 1024.0);
        Console.WriteLine($"  >> Heap before: {_heapBefore / (1024.0 * 1024.0):F1} MB, after: {heapAfter / (1024.0 * 1024.0):F1} MB, retained: {retainedMb:+0.0;-0.0;0.0} MB");

        _fixture.CleanJobTables().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Publishes N simple jobs (empty handler) and waits for completion.
    /// Exercises: worker fetch → transaction → deserialize → execute → finalize cycle.
    /// </summary>
    [Benchmark]
    public async Task ProcessJobs()
    {
        // Batch inserts to avoid DB command timeout on large counts
        const int batchSize = 1000;
        var remaining = JobCount;
        while (remaining > 0)
        {
            var publisher = _fixture.CreatePublisher();
            var count = Math.Min(batchSize, remaining);
            for (var i = 0; i < count; i++)
            {
                await publisher.Enqueue(new EmptyRequest());
            }

            await publisher.SaveChangesAsync();
            remaining -= count;
        }

        await _fixture.WaitForCompletion();
    }
}
