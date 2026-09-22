using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Warp.Core.Handlers;
using Warp.ServerBenchmarks.Infrastructure;
using Warp.Test.Shared.Handlers;

namespace Warp.ServerBenchmarks.Benchmarks;

/// <summary>
/// What a job's payload size costs, in dispatcher mode.
/// <para>
/// The zero-byte arm is the control, and it is the point of the pair rather than a filler row. The
/// 7.1.0 release claims the batched-completion column narrowing helps in proportion to payload size
/// and does nothing at all at zero length; without an arm that holds everything else constant and
/// varies only the payload, that claim rests on an assumption. With one, a change that shifts the
/// zero-byte arm is visibly not about payload.
/// </para>
/// <para>
/// Replaces the lab's <c>dispatcher-4kb</c> arm, which measured 9.9 statements per job.
/// </para>
/// </summary>
[Config(typeof(ServerBenchmarkConfig))]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "BenchmarkDotNet manages lifecycle via [GlobalCleanup].")]
public class PayloadSizeBenchmark
{
    private const int PublishBatchSize = 1000;

    private PostgresServerFixture _fixture = null!;
    private string _payload = string.Empty;

    [Params(10_000)]
    public int JobCount { get; set; }

    [Params(0, 4096)]
    public int PayloadBytes { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _payload = new string('x', PayloadBytes);

        _fixture = new PostgresServerFixture();
        await _fixture.InitializeAsync(workerCount: 16, useDispatcher: true);

        // Warm the JIT, type caches, connection pool and dispatcher channel, so the first measured
        // iteration is not paying for all of them.
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
    public async Task ProcessJobs()
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
                await publisher.Enqueue(new PayloadRequest1 { Data = _payload });
            }

            await publisher.SaveChangesAsync();
            remaining -= batch;
        }
    }
}
