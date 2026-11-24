using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using Warp.ServerBenchmarks.Infrastructure;

namespace Warp.ServerBenchmarks.Benchmarks;

/// <summary>
/// The idle measurement shared by the one- and two-server scenarios: <see cref="ServerCount"/> servers of 10
/// workers each on one database, 60 seconds of nothing after a 60 second settle (see
/// <see cref="IdleServerBenchmark"/> for why those lengths). Two classes rather than a parameter, so each runs
/// as its own CI job inside the timeout and the one-server case keeps the name its history is recorded under.
/// </summary>
[MeasuredPerSecond]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "BenchmarkDotNet manages lifecycle via [GlobalCleanup].")]
public abstract class IdleServerBenchmarkBase
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(60);

    private PostgresServerFixture _fixture = null!;

    [Params(BenchmarkProvider.PostgreSql, BenchmarkProvider.SqlServer)]
    public BenchmarkProvider Provider { get; set; }

    protected abstract int ServerCount { get; }

    [GlobalSetup]
    public async Task Setup()
    {
        _fixture = new PostgresServerFixture();
        await _fixture.InitializeAsync(workerCount: 10, useDispatcher: false, provider: Provider, serverCount: ServerCount);

        // Start-up registers the servers, their workers and groups, and runs every task's first tick; none
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
