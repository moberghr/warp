using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using Warp.Core.Handlers;
using Warp.ServerBenchmarks.Infrastructure;
using Warp.Worker;

namespace Warp.ServerBenchmarks.Benchmarks;

/// <summary>
/// Jobs whose handler stages one child job without saving, so the worker's handler-scope save commits
/// it and must announce it (#322). Measures what announcing handler-staged jobs costs, with and without
/// DB push, under both ways of sending that announcement:
/// <list type="bullet">
///   <item><description><c>worker</c> — the worker captures and dispatches the wake-up itself, inline (the pre-#322 path).</description></item>
///   <item><description><c>interceptor</c> — the enqueue interceptor announces it and the relay publishes the NOTIFY off the saving thread, batched per queue.</description></item>
/// </list>
/// The choice is the temporary <c>WARP_EXPERIMENT_ENQUEUE_WAKE</c> switch, read when the server
/// configuration is built. A commit that predates the switch ignores it, so on the base side both
/// <c>EnqueueWake</c> cases measure the same code.
/// </summary>
[CiScenario(
    "Handler-staged jobs",
    "500 jobs whose handler stages one child job each, committed by the worker's own save: 1,000 jobs. With and without DB push, with the wake-up sent by the worker or by the enqueue interceptor and its relay. 10 workers, PostgreSQL.",
    "Every job that enqueues another from its handler announces it at commit; this is the path #322 moved.",
    Order = 97)]
[CaseLabel(nameof(Push), "False", "polling")]
[CaseLabel(nameof(Push), "True", "push")]
[Config(typeof(ServerBenchmarkConfig))]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "BenchmarkDotNet manages lifecycle via [GlobalCleanup].")]
public class FanOutBenchmark
{
    private const string WakeVariable = "WARP_EXPERIMENT_ENQUEUE_WAKE";

    private PostgresServerFixture _fixture = null!;

    [Params(500)]
    public int JobCount { get; set; }

    [Params(false, true)]
    public bool Push { get; set; }

    [Params("worker", "interceptor")]
    public string EnqueueWake { get; set; } = "worker";

    [GlobalSetup]
    public async Task Setup()
    {
        // Each case runs in its own process, so setting the variable here scopes it to this case. It
        // must be set before the server configuration is constructed, which reads it once.
        Environment.SetEnvironmentVariable(WakeVariable, EnqueueWake);

        _fixture = new PostgresServerFixture();
        await _fixture.InitializeAsync(
            workerCount: 10,
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
