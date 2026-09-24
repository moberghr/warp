using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Warp.Core;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.ServerBenchmarks.Infrastructure;

namespace Warp.ServerBenchmarks.Benchmarks;

/// <summary>
/// The claim against a job table that already holds a large history.
/// <para>
/// Every other scenario starts each iteration from an almost empty table, so none of them exercises
/// how the claim scales with table size or which plan the planner picks for it. Both claim regressions
/// on record needed a big table to show: the multi-value queue predicate read 615x the buffers at 300k
/// rows (rule 6.9), and the plan that re-ran the LIMIT subquery walked the table on every claim.
/// </para>
/// <para>
/// The history is completed jobs, which is what a real deployment carries: they expire a day after
/// completion, and a busy server holds hundreds of thousands at any time. They are seeded once and then
/// analysed, as autovacuum eventually would; each iteration deletes only the jobs it created.
/// </para>
/// </summary>
[CiScenario(
    "Large table",
    "10 workers drain 1,000 jobs from a job table that already holds 100,000 completed jobs, and from an empty one for contrast.",
    "Every other scenario starts from an almost empty table, so none of them shows how the claim scales or which plan the database picks. Both claim regressions on record needed a big table to appear.",
    Order = 90)]
[CaseLabel(nameof(HistoryRows), "0", "empty table")]
[CaseLabel(nameof(HistoryRows), "100000", "100,000 completed jobs")]
[Config(typeof(ServerBenchmarkConfig))]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "BenchmarkDotNet manages lifecycle via [GlobalCleanup].")]
public class LargeTableBenchmark
{
    private const int PublishBatchSize = 1000;
    private const int SeedBatchSize = 5000;

    private PostgresServerFixture _fixture = null!;
    private DateTime _seededAt;

    [Params(1_000)]
    public int JobCount { get; set; }

    [Params(0, 100_000)]
    public int HistoryRows { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _fixture = new PostgresServerFixture();
        await _fixture.InitializeAsync(workerCount: 10, useDispatcher: false);

        await SeedHistoryAsync();

        await PublishAsync(100);
        await _fixture.WaitForCompletion();
        await _fixture.CleanJobTables(_seededAt);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _fixture.DisposeAsync();
    }

    [IterationCleanup]
    public void AfterIteration()
    {
        _fixture.CleanJobTables(_seededAt).GetAwaiter().GetResult();
    }

    [Benchmark]
    public async Task ProcessJobs()
    {
        await PublishAsync(JobCount);
        await _fixture.WaitForCompletion();
    }

    private async Task SeedHistoryAsync()
    {
        var completedAt = DateTime.UtcNow.AddHours(-1);
        var type = typeof(EmptyRequest).AssemblyQualifiedName;

        for (var seeded = 0; seeded < HistoryRows; seeded += SeedBatchSize)
        {
            await using var scope = _fixture.Host.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<TestContext>();
            var count = Math.Min(SeedBatchSize, HistoryRows - seeded);

            context.Set<Job>().AddRange(Enumerable.Range(0, count).Select(_ => new Job
            {
                Id = Guid.NewGuid(),
                Kind = JobKind.Job,
                CurrentState = State.Completed,
                Type = type,
                Message = "{}",
                CreateTime = completedAt,
                ScheduleTime = completedAt,
                Queue = "default",
            }));
            await context.SaveChangesAsync();
        }

        if (HistoryRows > 0)
        {
            // The statistics a deployment would have once autovacuum caught up, rather than the
            // freshly-loaded state no real table stays in for long.
            await using var scope = _fixture.Host.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<TestContext>().Database.ExecuteSqlRawAsync("ANALYZE warp.job");
        }

        _seededAt = DateTime.UtcNow;
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
                await publisher.Enqueue(new EmptyRequest());
            }

            await publisher.SaveChangesAsync();
            remaining -= batch;
        }
    }
}
