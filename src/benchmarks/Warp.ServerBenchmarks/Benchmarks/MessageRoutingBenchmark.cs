using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Warp.Core.Handlers;
using Warp.ServerBenchmarks.Infrastructure;
using Warp.Test.Shared.Handlers;

namespace Warp.ServerBenchmarks.Benchmarks;

/// <summary>
/// What routing a message costs, against executing a job directly.
/// <para>
/// A message is not a job that runs: <c>MessageRouter</c> picks it up, fans it out into one child job
/// per handler, and those children are what a worker executes (rule 2.1). So one published message
/// here becomes several rows and several executions, and the per-JOB statement count is measured
/// against the messages published rather than the children spawned — which is the number a caller can
/// reason about, since the caller chose the message, not the fan-out width.
/// </para>
/// <para>
/// The reason to cover it: <c>MessageRouter</c> is the one server task that takes a SESSION-scoped
/// lock rather than a transaction-scoped one (rule 2.16), and <c>docs/perf-results.md</c> records that
/// lock as a throughput ceiling for multi-server deployments leaning on messages. Nothing else in this
/// suite exercises it at all.
/// </para>
/// </summary>
[CiScenario(
    "Message routing",
    "10 workers process 1,000 published messages, each routed by the message router into the jobs its handlers run.",
    "Messages pass through a server task before any worker sees them. This covers that extra hop.",
    Order = 70)]
[Config(typeof(ServerBenchmarkConfig))]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "BenchmarkDotNet manages lifecycle via [GlobalCleanup].")]
public class MessageRoutingBenchmark
{
    private const int PublishBatchSize = 500;

    private PostgresServerFixture _fixture = null!;

    [Params(1_000)]
    public int JobCount { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _fixture = new PostgresServerFixture();
        await _fixture.InitializeAsync(workerCount: 10, useDispatcher: false);

        await PublishAsync(50);
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
    public async Task RouteMessages()
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
                await publisher.Publish(new EmptyMessage());
            }

            await publisher.SaveChangesAsync();
            remaining -= batch;
        }
    }
}
