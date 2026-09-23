using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Warp.Core.Handlers;
using Warp.ServerBenchmarks.Infrastructure;
using Warp.Test.Shared.Handlers;

namespace Warp.ServerBenchmarks.Benchmarks;

/// <summary>
/// What ENQUEUEING costs the caller, with nothing draining the queue.
/// <para>
/// Every other benchmark here measures a server working through a backlog. This measures the half a
/// user actually waits on: <c>Enqueue</c> plus <c>SaveChanges</c> inside their own transaction, on
/// their own request path, synchronously. It is also what a publisher-only process pays — a web
/// application that calls <c>AddWarp</c> and never runs a worker still pays this and nothing else.
/// </para>
/// <para>
/// No hosted services, so no worker can claim a row and no server task can tick. That is what makes
/// the number attributable: with a server running, the publish cost and the drain cost land in the
/// same statement count and cannot be told apart.
/// </para>
/// <para>
/// <c>BatchSize</c> is swept because the outbox batches: one <c>SaveChanges</c> over a thousand jobs
/// is a very different shape from a thousand over one each, and which of those a caller writes is a
/// choice they make without much guidance.
/// </para>
/// </summary>
[CiScenario(
    "Publishing",
    "Enqueues 1,000 jobs with no server running, saving after every job or once for all 1,000.",
    "What the calling application pays to hand work to Warp, with no worker activity mixed in.",
    Order = 60)]
[CaseLabel(nameof(BatchSize), "1", "save after every job")]
[CaseLabel(nameof(BatchSize), "1000", "one save for 1,000")]
[Config(typeof(ServerBenchmarkConfig))]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "BenchmarkDotNet manages lifecycle via [GlobalCleanup].")]
public class PublishBenchmark
{
    private PostgresServerFixture _fixture = null!;

    [Params(1_000)]
    public int JobCount { get; set; }

    [Params(1, 1_000)]
    public int BatchSize { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _fixture = new PostgresServerFixture();
        await _fixture.InitializeWithoutHostedServicesAsync();

        // Warm the JIT, the model and the connection pool, so the first measured iteration is not
        // paying for all three.
        await PublishAsync(100);
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
    public async Task PublishJobs()
    {
        await PublishAsync(JobCount);
    }

    private async Task PublishAsync(int count)
    {
        var remaining = count;

        while (remaining > 0)
        {
            var publisher = _fixture.CreatePublisher();
            var batch = Math.Min(BatchSize, remaining);

            for (var i = 0; i < batch; i++)
            {
                await publisher.Enqueue(new EmptyRequest());
            }

            await publisher.SaveChangesAsync();
            remaining -= batch;
        }
    }
}
