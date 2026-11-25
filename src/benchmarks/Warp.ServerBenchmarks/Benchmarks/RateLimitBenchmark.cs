using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using Warp.Core.Handlers;
using Warp.Core.Helper;
using Warp.Core.RateLimit;
using Warp.ServerBenchmarks.Infrastructure;
using Warp.Test.Shared.Handlers;

namespace Warp.ServerBenchmarks.Benchmarks;

/// <summary>
/// What a hot rate-limit key costs while it is over its limit.
/// <para>
/// Every throttled job is claimed, takes the key's lock, reads the bucket and is refused — all spent on a
/// job that did not run. <c>Backlog</c> is the shape that hurts: a backlog many windows deep in
/// <c>Wait</c> mode, where every refused job used to be rescheduled onto the SAME reopening instant, so
/// the whole remaining backlog re-executed at every window (quadratic in its depth). Its window is longer
/// than <c>ScheduledActivationInterval</c>, like any per-minute limit. <c>ShortWindow</c> is kept beside
/// it on purpose: a window SHORTER than the activation interval is the regime where activation, not the
/// limit, bounds throughput, and a change that helps the first can hurt the second.
/// </para>
/// <para>
/// <b>The clock is scaled down, the shape is not.</b> What costs is how many windows deep the backlog is
/// and which of window and activation interval is the longer, not how many seconds a window lasts. So the
/// windows are 2 s and 1 s, with the activation interval set per shape to sit on the right side of them,
/// and a run takes about 18 s. With the production-sized 10 s on both it took 90 s per run to wait out the
/// same nine windows, 41 minutes for the CI job, measuring the wall clock.
/// </para>
/// <para>
/// Each iteration publishes onto a fresh key, so no iteration opens inside the previous one's spent window
/// and no process-local memory of an earlier key carries over.
/// </para>
/// </summary>
[CiScenario(
    "Rate limit under a backlog",
    "16 workers drain jobs over one rate-limit key. Backlog: 1,000 Wait-mode jobs at 100 per 2 s, ten windows deep, activation every 1 s. ShortWindow: 500 Wait-mode jobs at 50 per 1 s, activation every 2 s. Skip: 2,000 Skip-mode jobs at 50 per 2 s.",
    "The cost of every throttled execution, and how many times a backlog re-executes before it drains.",
    Order = 41,
    ClockBound = true)]
[CaseLabel(nameof(Shape), "Backlog", "Wait, 1,000 jobs at 100/2s")]
[CaseLabel(nameof(Shape), "ShortWindow", "Wait, 500 jobs at 50/1s")]
[CaseLabel(nameof(Shape), "Skip", "Skip, 2,000 jobs at 50/2s")]
[Config(typeof(ClockBoundBenchmarkConfig))]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "BenchmarkDotNet manages lifecycle via [GlobalCleanup].")]
public class RateLimitBenchmark
{
    private const int PublishBatchSize = 1000;

    private PostgresServerFixture _fixture = null!;
    private int _iteration;

    [Params("Backlog", "ShortWindow", "Skip")]
    public string Shape { get; set; } = "Backlog";

    [GlobalSetup]
    public async Task Setup()
    {
        _fixture = new PostgresServerFixture();
        // Backlog: window (2 s) longer than activation. ShortWindow: window (1 s) shorter than it.
        var activationInterval = string.Equals(Shape, "ShortWindow", StringComparison.Ordinal)
            ? TimeSpan.FromSeconds(2)
            : TimeSpan.FromSeconds(1);
        await _fixture.InitializeAsync(
            workerCount: 16,
            configure: x =>
            {
                x.AddRateLimit();
                x.ScheduledActivationInterval = activationInterval;
            });

        // JIT the policy pipeline, the lock provider and the reject path outside the measured window: a
        // limit low enough that most of the warm-up is refused, on a key no iteration uses.
        await PublishAsync(200, "rl-warmup", 20, TimeSpan.FromSeconds(1), RateLimitMode.Skip);
        await _fixture.WaitForCompletion();
        await _fixture.CleanJobTables();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _fixture.DisposeAsync();
    }

    [IterationSetup]
    public void BeforeIteration()
    {
        _iteration++;
    }

    [IterationCleanup]
    public void AfterIteration()
    {
        _fixture.CleanJobTables().GetAwaiter().GetResult();
    }

    [Benchmark]
    public async Task ProcessJobs()
    {
        var key = $"rl-{Shape}-{_iteration}";
        var (jobs, count, window, mode) = Shape switch
        {
            "ShortWindow" => (500, 50, TimeSpan.FromSeconds(1), RateLimitMode.Wait),
            "Skip" => (2000, 50, TimeSpan.FromSeconds(2), RateLimitMode.Skip),
            _ => (1000, 100, TimeSpan.FromSeconds(2), RateLimitMode.Wait),
        };

        await PublishAsync(jobs, key, count, window, mode);
        await _fixture.WaitForCompletion(TimeSpan.FromMinutes(3));
    }

    private async Task PublishAsync(int count, string key, int limit, TimeSpan window, RateLimitMode mode)
    {
        var remaining = count;
        while (remaining > 0)
        {
            var publisher = _fixture.CreatePublisher();
            var batch = Math.Min(PublishBatchSize, remaining);

            for (var i = 0; i < batch; i++)
            {
                // Applied at publish, the top precedence rung (§8.8), so the shape asked for is the shape
                // that executes. DelayRequest of 0 ms: the handler is not what is being measured.
                var parameters = new JobParameters().WithRateLimit(key, limit, window, mode);
                await publisher.Enqueue(new DelayRequest { DelayMs = 0 }, parameters);
            }

            await publisher.SaveChangesAsync();
            remaining -= batch;
        }
    }
}
