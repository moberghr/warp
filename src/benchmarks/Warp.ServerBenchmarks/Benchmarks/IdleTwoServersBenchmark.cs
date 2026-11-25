using BenchmarkDotNet.Attributes;
using Warp.ServerBenchmarks.Infrastructure;

namespace Warp.ServerBenchmarks.Benchmarks;

/// <summary>
/// What two servers cost the database with nothing to do. Cluster-wide server tasks are claimed once per tick
/// for the whole cluster (rule 6.23), so the second server should add its heartbeat, its workers' polling and
/// its share of the claims, not a second copy of every task; this case is what shows it. Same window and
/// settle as <see cref="IdleServerBenchmark"/>.
/// </summary>
[CiScenario(
    "Idle cluster (2 servers)",
    "Two servers with 10 workers each and nothing to do, on one database, measured over 60 seconds after they have settled.",
    "What a second server adds to the around-the-clock load. Cluster-wide tasks run once per tick for the whole cluster, so this should grow by much less than double.",
    Order = 101)]
[CaseLabel(nameof(Provider), "PostgreSql", "PostgreSQL")]
[CaseLabel(nameof(Provider), "SqlServer", "SQL Server")]
[Config(typeof(SingleWindowBenchmarkConfig))]
public class IdleTwoServersBenchmark : IdleServerBenchmarkBase
{
    protected override int ServerCount => 2;
}
