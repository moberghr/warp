using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Warp.Core;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Services;
using Warp.ServerBenchmarks.Infrastructure;

namespace Warp.ServerBenchmarks.Benchmarks;

/// <summary>
/// A drain with the work that runs BESIDE it in production: people watching the dashboard, and the server's
/// aggregation tasks folding what the drain records.
/// <para>
/// <b>Dashboard</b>: 10 simulated tabs each read the dashboard status (<c>GetWarpStatus</c>, about twenty
/// queries) and the first page of the job list every second, as the SPA does without push (rule 2.10), the way
/// the lab's <c>--tabs</c> does. Those queries read the same job table the claim and completion write, so a
/// status query that got more expensive, or a plan that started scanning while the table churns, shows here.
/// </para>
/// <para>
/// <b>Rollups</b>: <c>CounterAggregator</c>, <c>StatisticRollup</c>, <c>MetricRollup</c> and
/// <c>ErrorGroupAggregator</c> every second instead of every one to ten minutes, so they run several times
/// inside each drain against the counter rows it is writing. The counter fold has real work (the drain's
/// counters); the two rollups mostly find nothing old enough to roll and measure what an idle pass costs
/// while the tables are busy. This is the cost of those tasks under load, which the idle scenarios only see
/// with nothing to fold.
/// </para>
/// <para>
/// <b>None</b> is the same drain with neither, for contrast within the scenario. The case is a parameter of
/// one class because the three share everything but the background work.
/// </para>
/// <para>
/// <b>Reading it.</b> The background work runs every SECOND, not per job: over a drain it adds (seconds of
/// drain × work per second), so per job it shrinks when the drain gets faster and grows when it gets slower,
/// with no change to what the dashboard or the folds cost. The per-job columns are therefore the drain's cost
/// under that load, and the report adds each case's background work PER SECOND of drain, against <b>None</b>
/// on the same side (<see cref="BackgroundCostAttribute"/>): that is the figure to read for "did the
/// dashboard or the folds get more expensive". The dashboard tabs' queries are the workload here, so they are
/// counted like Warp's own; only the harness's polling and cleanup are left out (<see cref="Harness"/>).
/// Its PostgreSQL gates were calibrated by no-op runs (statements under 3%; performance-worker.md §6.24).
/// </para>
/// </summary>
[CiScenario(
    "Background load",
    "10 workers drain 2,000 empty jobs alone, while 10 dashboard tabs poll status and the job list every second, or while the counter, statistic, metric and error-group folds run every second.",
    "Dashboard reads and the aggregation tasks share the job and counter tables with the drain. Each draining scenario runs without them, and the idle scenarios run them with nothing to read or fold.",
    Order = 92)]
[BackgroundCost(nameof(Load), nameof(BackgroundLoad.None))]
[CaseLabel(nameof(Load), "None", "drain alone")]
[CaseLabel(nameof(Load), "Dashboard", "10 dashboard tabs")]
[CaseLabel(nameof(Load), "Rollups", "folds every second")]
[CaseLabel(nameof(Provider), "PostgreSql", "PostgreSQL")]
[CaseLabel(nameof(Provider), "SqlServer", "SQL Server")]
[Config(typeof(ServerBenchmarkConfig))]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "BenchmarkDotNet manages lifecycle via [GlobalCleanup].")]
public class BackgroundLoadBenchmark
{
    private const int PublishBatchSize = 1000;
    private const int Tabs = 10;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FoldInterval = TimeSpan.FromSeconds(1);

    private PostgresServerFixture _fixture = null!;

    public enum BackgroundLoad
    {
        None = 1,
        Dashboard = 2,
        Rollups = 3,
    }

    [Params(2_000)]
    public int JobCount { get; set; }

    [Params(BackgroundLoad.None, BackgroundLoad.Dashboard, BackgroundLoad.Rollups)]
    public BackgroundLoad Load { get; set; }

    [Params(BenchmarkProvider.PostgreSql, BenchmarkProvider.SqlServer)]
    public BenchmarkProvider Provider { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _fixture = new PostgresServerFixture();
        await _fixture.InitializeAsync(
            workerCount: 10,
            useDispatcher: false,
            provider: Provider,
            configure: config =>
            {
                if (Load == BackgroundLoad.Rollups)
                {
                    config.CounterAggregationInterval = FoldInterval;
                    config.StatisticRollupInterval = FoldInterval;
                    config.MetricRollupInterval = FoldInterval;
                    config.ErrorGroupingInterval = FoldInterval;
                }
            });

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
    public async Task DrainWithBackgroundLoad()
    {
        using var stopPolling = new CancellationTokenSource();
        var pollers = Load == BackgroundLoad.Dashboard
            ? Task.WhenAll(Enumerable.Range(0, Tabs).Select(_ => PollAsync(stopPolling.Token)))
            : Task.CompletedTask;

        await PublishAsync(JobCount);
        await _fixture.WaitForCompletion();

        await stopPolling.CancelAsync();
        await pollers;
    }

    // One tab: status and the first page of the job list, then a second's wait. A failed read is counted
    // and printed, not thrown: on SQL Server without RCSI a dashboard read is sometimes chosen as a deadlock
    // victim against the drain's writes, which a real tab survives (it reads again next second), and a
    // throw here would turn that into an NA case.
    private async Task PollAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var scope = _fixture.Host.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IDashboardStatsService>().GetWarpStatus();
                await scope.ServiceProvider.GetRequiredService<IJobQueryService>().GetJobsList(new BaseListRequest { PageSize = 20 }, State.Processing);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Console.WriteLine($"// dashboard poll failed: {e.GetBaseException().GetType().Name}: {e.GetBaseException().Message}");
            }

            try
            {
                await Task.Delay(PollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
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
