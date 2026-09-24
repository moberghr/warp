using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Warp.ServerBenchmarks.Infrastructure;

namespace Warp.ServerBenchmarks.Benchmarks;

/// <summary>
/// What a server costs the database with nothing to do.
/// <para>
/// This is the load an operator pays around the clock, whether or not work arrives, and it is where
/// several recent changes made their difference: the separate lock pool (the server tasks take advisory
/// locks too) and the capped statistics rollup. No other scenario isolates it — each of them drains jobs,
/// and a server's background work is a small share of that.
/// </para>
/// <para>
/// One operation is 30 seconds of idle, after a settle period so the start-up registration and first
/// ticks are not counted. With no job written in the window, the diagnoser reports its counters per
/// second instead of per job.
/// </para>
/// </summary>
[CiScenario(
    "Idle server",
    "A server with 10 workers and nothing to do, measured over 30 seconds after it has settled: only the background tasks and the workers' polling run.",
    "The database load a server costs around the clock, whether or not there is work. No other scenario isolates it, because each of them is dominated by the jobs it drains.",
    Order = 100)]
[CaseLabel(nameof(Provider), "PostgreSql", "PostgreSQL")]
[CaseLabel(nameof(Provider), "SqlServer", "SQL Server")]
[Config(typeof(ServerBenchmarkConfig))]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "BenchmarkDotNet manages lifecycle via [GlobalCleanup].")]
public class IdleServerBenchmark
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(20);

    private PostgresServerFixture _fixture = null!;

    [Params(BenchmarkProvider.PostgreSql, BenchmarkProvider.SqlServer)]
    public BenchmarkProvider Provider { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _fixture = new PostgresServerFixture();
        await _fixture.InitializeAsync(workerCount: 10, useDispatcher: false, provider: Provider);

        // Start-up registers the server, its workers and groups, and runs every task's first tick; none
        // of that is the steady state being measured.
        await Task.Delay(Settle);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _fixture.DisposeAsync();
    }

    [Benchmark]
    public async Task StayIdle()
    {
        await Task.Delay(Window);
    }
}
