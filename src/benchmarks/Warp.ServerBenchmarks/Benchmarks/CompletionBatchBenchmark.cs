using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Warp.Core;
using Warp.Core.Handlers;
using Warp.ServerBenchmarks.Infrastructure;

namespace Warp.ServerBenchmarks.Benchmarks;

/// <summary>
/// Measures the impact of per-worker completion batching in dispatcher mode.
/// <para>
/// Baseline (<c>CompletionBatchSize = 1</c>): every job completion opens its own
/// transaction and commits immediately — same behaviour as before the feature.
/// </para>
/// <para>
/// Batched (<c>CompletionBatchSize = 50</c>): completions are buffered per worker
/// and flushed as a single multi-row transaction on size / time / idle / shutdown.
/// </para>
/// <para>
/// Both variants run with <c>UseDispatcher = true</c> (the batching is dispatcher-only).
/// Workload: <see cref="JobCount"/> empty-handler jobs, 10 workers, handler logging off.
/// </para>
/// </summary>
[Config(typeof(ServerBenchmarkConfig))]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "BenchmarkDotNet manages lifecycle via [GlobalCleanup].")]
public class CompletionBatchBenchmark
{
    private PostgresServerFixture _fixture = null!;

    [Params(1_000)]
    public int JobCount { get; set; }

    [Params(1, 50)]
    public int CompletionBatchSize { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _fixture = new PostgresServerFixture();
        await _fixture.InitializeAsync(workerCount: 10, useDispatcher: true, completionBatchSize: CompletionBatchSize);

        // Warmup: prime JIT, type caches, connection pool, dispatcher channel.
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

    [IterationCleanup]
    public void AfterIteration()
    {
        _fixture.CleanJobTables().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Publishes <see cref="JobCount"/> no-op jobs and waits for every one to reach Completed.
    /// Wall-clock captures end-to-end throughput across workers + background tasks.
    /// </summary>
    [Benchmark]
    public async Task ProcessJobs()
    {
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
