using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Warp.Core.Handlers;
using Warp.ServerBenchmarks.Infrastructure;
using Warp.Test.Shared.Handlers;

namespace Warp.ServerBenchmarks.Benchmarks;

/// <summary>
/// What a FAILING job costs, against the succeeding job every other benchmark here measures.
/// <para>
/// This is the path with no coverage and the most write amplification. A failure writes a failure
/// <c>JobLog</c> rather than a completion one, and — since error grouping is always on (rule 8.29) —
/// it also appends a row to the <c>ErrorOccurrence</c> inbox, one per occurrence, which the aggregator
/// later drains. That inbox row is deliberately a row and not a buffered counter, because an
/// occurrence has to survive the process; so a change that made failures more expensive would show
/// here and nowhere else in this suite.
/// </para>
/// <para>
/// The contrast worth reading is against <c>DispatcherModeBenchmark</c> at the same worker count: the
/// difference between the two is what a failure costs over a success.
/// </para>
/// </summary>
[Config(typeof(ServerBenchmarkConfig))]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "BenchmarkDotNet manages lifecycle via [GlobalCleanup].")]
public class FailurePathBenchmark
{
    private const int PublishBatchSize = 1000;

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
    public async Task ProcessFailingJobs()
    {
        await PublishAsync(JobCount);
        await _fixture.WaitForCompletion();
    }

    private async Task PublishAsync(int count)
    {
        var remaining = count;

        while (remaining > 0)
        {
            var publisher = _fixture.CreatePublisher();
            var batch = Math.Min(PublishBatchSize, remaining);

            for (var i = 0; i < batch; i++)
            {
                await publisher.Enqueue(new ThrowExceptionRequest());
            }

            await publisher.SaveChangesAsync();
            remaining -= batch;
        }
    }
}
