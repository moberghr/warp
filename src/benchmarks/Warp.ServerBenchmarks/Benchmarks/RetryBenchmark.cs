using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Warp.Core.Handlers;
using Warp.Core.Retry;
using Warp.ServerBenchmarks.Infrastructure;
using Warp.Test.Shared.Handlers;

namespace Warp.ServerBenchmarks.Benchmarks;

/// <summary>
/// The retry path: a failing job rescheduled and run again until its retries are spent.
/// <para>
/// The failing-jobs scenario ends each job at Failed on its first attempt, so it never reaches what
/// <c>AddRetry()</c> adds: the retry bookkeeping in the job's metadata, the reschedule back to Enqueued,
/// the second and third claim and execution. Delays are zero so the benchmark measures the path, not the
/// clock; a real schedule only spaces the same work out.
/// </para>
/// </summary>
[CiScenario(
    "Retries",
    "10 workers drain 1,000 jobs whose handler always throws, with AddRetry() set to two immediate retries, so every job runs three times before it ends Failed.",
    "A retry reschedules the job and runs it again, which costs more than a failure that ends at once. This keeps that path from quietly getting more expensive.",
    Order = 55)]
[Config(typeof(ServerBenchmarkConfig))]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "BenchmarkDotNet manages lifecycle via [GlobalCleanup].")]
public class RetryBenchmark
{
    private const int PublishBatchSize = 1000;

    private PostgresServerFixture _fixture = null!;

    [Params(1_000)]
    public int JobCount { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _fixture = new PostgresServerFixture();
        await _fixture.InitializeAsync(
            workerCount: 10,
            useDispatcher: false,
            configure: x => x.AddRetry(o =>
            {
                o.MaxRetries = 2;
                o.Delays = [0, 0];
            }));

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
    public async Task RetryFailingJobs()
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
