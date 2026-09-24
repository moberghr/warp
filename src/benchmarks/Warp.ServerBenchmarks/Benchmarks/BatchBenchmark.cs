using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Warp.Core.Handlers;
using Warp.ServerBenchmarks.Infrastructure;

namespace Warp.ServerBenchmarks.Benchmarks;

/// <summary>
/// Batches and continuations, which run through the Orchestrator.
/// <para>
/// A batch is a parent job that groups its children and completes when they all have; a continuation
/// is a batch that starts only once its parent has. Both are decided by the <c>Orchestrator</c> server
/// task, woken on every job finalization — work no other scenario reaches. Each iteration publishes five
/// batches of 100 jobs, each with a continuation batch of 100, so 1,000 jobs pass through both paths.
/// </para>
/// </summary>
[CiScenario(
    "Batches and continuations",
    "Five batches of 100 jobs, each followed by a continuation batch of 100 that starts when the first finishes: 1,000 jobs, with the Orchestrator completing every batch and releasing every continuation. 10 workers.",
    "Batch completion and continuations are decided by the Orchestrator server task, which no other scenario reaches.",
    Order = 95)]
[Config(typeof(ServerBenchmarkConfig))]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "BenchmarkDotNet manages lifecycle via [GlobalCleanup].")]
public class BatchBenchmark
{
    private const int Batches = 5;

    private PostgresServerFixture _fixture = null!;

    [Params(1_000)]
    public int JobCount { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _fixture = new PostgresServerFixture();
        await _fixture.InitializeAsync(workerCount: 10, useDispatcher: false);

        await PublishAsync(100);
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
    public async Task ProcessBatches()
    {
        await PublishAsync(JobCount);
        await _fixture.WaitForCompletion();
    }

    private async Task PublishAsync(int jobs)
    {
        // Half the jobs in the first batch of each pair, half in its continuation.
        var perBatch = jobs / Batches / 2;
        var publisher = _fixture.CreateBatchPublisher();

        for (var i = 0; i < Batches; i++)
        {
            var parent = await publisher.StartNew(Enumerable.Range(0, perBatch).Select(_ => new EmptyRequest()).ToList());
            await publisher.ContinueBatchWith(Enumerable.Range(0, perBatch).Select(_ => new EmptyRequest()).ToList(), parent);
        }

        await publisher.SaveChangesAsync();
    }
}
