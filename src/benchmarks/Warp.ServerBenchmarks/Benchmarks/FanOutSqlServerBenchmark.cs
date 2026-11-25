using BenchmarkDotNet.Attributes;
using Warp.ServerBenchmarks.Infrastructure;

namespace Warp.ServerBenchmarks.Benchmarks;

/// <summary>
/// <see cref="FanOutBenchmark"/> on SQL Server. A subclass rather than a <c>Provider</c> parameter on that class,
/// because BenchmarkDotNet puts every parameter in the case's full name and the release history compares
/// cases by it: a parameter would rename the PostgreSQL case and cut it off from its history.
/// </summary>
[CiScenario(
    "Handler-staged jobs (SQL Server)",
    "The handler-staged scenario on SQL Server: 500 jobs whose handler stages one child job each, 1,000 jobs in all, with and without DB push (Service Broker). 10 workers.",
    "DB push on SQL Server is Service Broker with a queue per listening process (rule 2.9), a different transport from PostgreSQL's NOTIFY; this is the only scenario that runs it under load.",
    Order = 98)]
[CaseLabel(nameof(Provider), "SqlServer", "SQL Server")]
[Config(typeof(ServerBenchmarkConfig))]
public class FanOutSqlServerBenchmark : FanOutBenchmark
{
    /// <summary>
    /// One value, so it is no column in the report; it is here because the diagnoser and the comparer
    /// recognise a SQL Server case by a parameter of this name.
    /// </summary>
    [Params(BenchmarkProvider.SqlServer)]
    public BenchmarkProvider Provider { get; set; }

    protected override BenchmarkProvider Database => Provider;
}
