using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Warp.Core.Handlers;
using Warp.Core.Helper;
using Warp.ServerBenchmarks.Infrastructure;
using Warp.Test.Shared.Handlers;

namespace Warp.ServerBenchmarks.Benchmarks;

/// <summary>
/// What a backlog of future-dated jobs costs when it all comes due at once.
/// <para>
/// Scheduled jobs land in <c>State.Scheduled</c> and are flipped to <c>Enqueued</c> by
/// <c>ScheduledJobActivation</c> (rule 2.8). The audit in <c>docs/perf-results.md</c> lists that task
/// among the sweeps that are "unbounded but bounded in practice" — it has no <c>LIMIT</c>, and what
/// keeps it small is the assumption that only so many rows come due at the same instant. This arm
/// stops assuming: every job is scheduled for the same moment, so the activation has the whole
/// backlog to flip in one statement.
/// </para>
/// <para>
/// The jobs are published a second in the past, not the future. Waiting out a real delay would measure
/// the clock rather than the code, and a row whose <c>ScheduleTime</c> has already passed is exactly
/// what the activation sweep exists to pick up.
/// </para>
/// </summary>
[CiScenario(
    "Scheduled backlog",
    "1,000 jobs are scheduled for the same moment, already past, so the activation task moves the whole backlog to Enqueued at once; 10 workers then drain it.",
    "The activation sweep has no row limit. This shows what a large backlog coming due at once costs.",
    Order = 80)]
[Config(typeof(ServerBenchmarkConfig))]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "BenchmarkDotNet manages lifecycle via [GlobalCleanup].")]
public class ScheduledActivationBenchmark
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
    public async Task ActivateAndProcess()
    {
        await PublishAsync(JobCount);
        await _fixture.WaitForCompletion();
    }

    private async Task PublishAsync(int count)
    {
        var scheduleTime = DateTime.UtcNow.AddSeconds(-1);
        var remaining = count;

        while (remaining > 0)
        {
            var publisher = _fixture.CreatePublisher();
            var batch = Math.Min(PublishBatchSize, remaining);

            for (var i = 0; i < batch; i++)
            {
                await publisher.Enqueue(new EmptyRequest(), new JobParameters { ScheduleTime = scheduleTime });
            }

            await publisher.SaveChangesAsync();
            remaining -= batch;
        }
    }
}
