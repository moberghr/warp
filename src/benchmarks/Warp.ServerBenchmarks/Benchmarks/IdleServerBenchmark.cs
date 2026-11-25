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
/// One operation is 60 seconds of idle, after a 60 second settle period so the start-up registration and
/// first ticks are not counted. Twenty seconds was not enough: on whichever commit was measured first
/// on a cold runner, start-up work was still arriving inside the window, which read as 11 to 17% more
/// statements per second for identical code on SQL Server, the same way round every time. With no job written in the window, the diagnoser reports its counters per
/// second instead of per job.
/// </para>
/// <para>
/// Sixty seconds, measured once, because the slowest of the frequent server tasks tick once a minute:
/// any 60 s window holds exactly one tick of each, whatever the phase. This used to be 30 s measured
/// five times (a JIT pass, a warm-up and three iterations), so a once-a-minute task landed in some
/// passes and not others, which is where the 14% swings between identical builds came from, and the
/// job took 18 minutes to watch 10.
/// </para>
/// </summary>
[CiScenario(
    "Idle server",
    "A server with 10 workers and nothing to do, measured over 60 seconds after it has settled: only the background tasks and the workers' polling run.",
    "The database load a server costs around the clock, whether or not there is work. No other scenario isolates it, because each of them is dominated by the jobs it drains.",
    Order = 100)]
[CaseLabel(nameof(Provider), "PostgreSql", "PostgreSQL")]
[CaseLabel(nameof(Provider), "SqlServer", "SQL Server")]
[Config(typeof(SingleWindowBenchmarkConfig))]
public class IdleServerBenchmark : IdleServerBenchmarkBase
{
    protected override int ServerCount => 1;
}
