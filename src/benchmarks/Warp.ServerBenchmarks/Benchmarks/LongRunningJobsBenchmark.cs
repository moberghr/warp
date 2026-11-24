using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using Warp.Core.Handlers;
using Warp.ServerBenchmarks.Infrastructure;

namespace Warp.ServerBenchmarks.Benchmarks;

/// <summary>
/// What a server costs while its jobs are RUNNING, rather than starting and finishing.
/// <para>
/// Every other scenario drains jobs that finish in a millisecond, so no job is ever still running when the
/// worker group's monitor ticks (rule 6.14): the keep-alive renewal, the cancellation check and the
/// handler-log flush never run in them. Here 20 workers hold 20 jobs at a time, each running a second and
/// logging every 100 ms, with <c>CancellationCheckInterval</c> at 250 ms and <c>LogFlushInterval</c> at
/// 100 ms, so each job sees about four renewals and ten log flushes while it runs. Those intervals are
/// shorter than production's (5 s and 1 s) only to keep the run short; the work per tick is the same, and
/// a monitor that went back to a statement per job per tick shows here as statements per job rising with
/// the number of jobs running at once.
/// </para>
/// <para>
/// Statements per SECOND while they run is not a separate column: the run is the 100 jobs, so it is
/// statements per job times jobs per second.
/// </para>
/// </summary>
[CiScenario(
    "Long-running jobs",
    "20 workers run 100 jobs of one second each, 20 at a time, every job logging a line every 100 ms, with the keep-alive check every 250 ms and the log flush every 100 ms, in both worker modes.",
    "Running jobs are renewed, checked for cancellation and have their logs flushed by one monitor per worker group (rule 6.14). Every other scenario's jobs finish before the monitor ticks, so only this one measures that work.",
    Order = 85)]
[CaseLabel(nameof(UseDispatcher), "False", "single-worker")]
[CaseLabel(nameof(UseDispatcher), "True", "dispatcher")]
[CaseLabel(nameof(Provider), "PostgreSql", "PostgreSQL")]
[CaseLabel(nameof(Provider), "SqlServer", "SQL Server")]
[Config(typeof(ServerBenchmarkConfig))]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "BenchmarkDotNet manages lifecycle via [GlobalCleanup].")]
public class LongRunningJobsBenchmark
{
    private const int Workers = 20;
    private const int DurationMs = 1_000;
    private const int LogIntervalMs = 100;

    private PostgresServerFixture _fixture = null!;

    [Params(100)]
    public int JobCount { get; set; }

    [Params(false, true)]
    public bool UseDispatcher { get; set; }

    [Params(BenchmarkProvider.PostgreSql, BenchmarkProvider.SqlServer)]
    public BenchmarkProvider Provider { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _fixture = new PostgresServerFixture();
        await _fixture.InitializeAsync(
            workerCount: Workers,
            useDispatcher: UseDispatcher,
            provider: Provider,
            captureHandlerLogs: true,
            configure: config =>
            {
                config.CancellationCheckInterval = TimeSpan.FromMilliseconds(250);
                config.LogFlushInterval = TimeSpan.FromMilliseconds(100);
            });

        await PublishAsync(Workers);
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
    public async Task RunLongJobs()
    {
        await PublishAsync(JobCount);
        await _fixture.WaitForCompletion();
    }

    private async Task PublishAsync(int count)
    {
        var publisher = _fixture.CreatePublisher();
        for (var i = 0; i < count; i++)
        {
            await publisher.Enqueue(new LongRunningRequest { DurationMs = DurationMs, LogIntervalMs = LogIntervalMs });
        }

        await publisher.SaveChangesAsync();
    }
}
