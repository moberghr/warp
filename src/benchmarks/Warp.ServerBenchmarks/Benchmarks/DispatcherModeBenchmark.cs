using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Warp.Core;
using Warp.Core.Handlers;
using Warp.ServerBenchmarks.Infrastructure;

namespace Warp.ServerBenchmarks.Benchmarks;

/// <summary>
/// Head-to-head between the two worker modes end-to-end:
/// <list type="bullet">
///   <item><description><c>UseDispatcher = false</c> — each <c>WarpWorkerService</c> independently fetches + processes + commits per job (pre-dispatcher behaviour).</description></item>
///   <item><description><c>UseDispatcher = true</c> — single <c>WarpDispatcher</c> batch-fetches jobs and hands them to <c>WarpDispatcherWorker</c> instances that buffer completions (<c>CompletionBatchSize = 50</c>, default).</description></item>
/// </list>
/// Same workload, same worker count, same handler.
/// </summary>
[Config(typeof(ServerBenchmarkConfig))]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "BenchmarkDotNet manages lifecycle via [GlobalCleanup].")]
public class DispatcherModeBenchmark
{
    private PostgresServerFixture _fixture = null!;

    // 1,000 rather than 10,000, which was measured before being adopted: PayloadSizeBenchmark swept
    // both and reported 10.52 statements per job against 9.95, a 5.7% offset in a metric that costs
    // ten times as long to obtain. The smaller run reads slightly HIGH because IterationCleanup runs
    // inside the measured window, so its fixed per-iteration deletes are divided by a tenth as many
    // jobs. That offset cancels in the gate, which compares an arm against itself across two commits
    // in the same run - but it does mean these numbers are not directly comparable to the lab's or to
    // published claims, which are taken at 10,000.
    [Params(1_000)]
    public int JobCount { get; set; }

    [Params(false, true)]
    public bool UseDispatcher { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _fixture = new PostgresServerFixture();
        await _fixture.InitializeAsync(workerCount: 10, useDispatcher: UseDispatcher);

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
