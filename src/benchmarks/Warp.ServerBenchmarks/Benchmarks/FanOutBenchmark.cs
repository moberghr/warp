using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using Warp.Core.Handlers;
using Warp.ServerBenchmarks.Infrastructure;
using Warp.Worker;

namespace Warp.ServerBenchmarks.Benchmarks;

/// <summary>
/// Jobs whose handler stages one child job without saving, so the worker's handler-scope save commits
/// it and must announce it (#322). The enqueue interceptor announces it: this process's workers are
/// woken inline, and with DB push the relay publishes the NOTIFY off the worker's path, one per queue
/// per batch. Run with and without push, to guard what announcing handler-staged jobs costs.
/// </summary>
[CiScenario(
    "Handler-staged jobs",
    "500 jobs whose handler stages one child job each, committed by the worker's own save: 1,000 jobs, with and without DB push. 10 workers, PostgreSQL.",
    "Every job that enqueues another from its handler announces it at commit; this is the path #322 moved.",
    Order = 97)]
[CaseLabel(nameof(Push), "False", "polling")]
[CaseLabel(nameof(Push), "True", "push")]
[Config(typeof(ServerBenchmarkConfig))]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "BenchmarkDotNet manages lifecycle via [GlobalCleanup].")]
public class FanOutBenchmark
{
    private PostgresServerFixture _fixture = null!;

    [Params(500)]
    public int JobCount { get; set; }

    [Params(false, true)]
    public bool Push { get; set; }

    /// <summary>
    /// The database this class runs on. PostgreSQL here; the SQL Server case is a subclass with a
    /// <c>Provider</c> parameter rather than a parameter on this class, because a parameter is part of
    /// BenchmarkDotNet's case name and the release history compares cases by that name.
    /// </summary>
    protected virtual BenchmarkProvider Database => BenchmarkProvider.PostgreSql;

    [GlobalSetup]
    public async Task Setup()
    {
        _fixture = new PostgresServerFixture();
        await _fixture.InitializeAsync(
            workerCount: 10,
            provider: Database,
            configure: config =>
            {
                if (Push)
                {
                    config.UseDatabasePush();
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
    public async Task ProcessHandlerStagedJobs()
    {
        await PublishAsync(JobCount);
        await _fixture.WaitForCompletion();
    }

    private async Task PublishAsync(int jobs)
    {
        var publisher = _fixture.CreatePublisher();
        for (var i = 0; i < jobs; i++)
        {
            await publisher.Enqueue(new SpawnOneChildRequest());
        }

        await publisher.SaveChangesAsync();
    }
}
