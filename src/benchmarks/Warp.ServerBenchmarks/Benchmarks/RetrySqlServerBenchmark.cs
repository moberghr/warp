using BenchmarkDotNet.Attributes;
using Warp.ServerBenchmarks.Infrastructure;

namespace Warp.ServerBenchmarks.Benchmarks;

/// <summary>
/// <see cref="RetryBenchmark"/> on SQL Server. A subclass rather than a <c>Provider</c> parameter on that class,
/// because BenchmarkDotNet puts every parameter in the case's full name and the release history compares
/// cases by it: a parameter would rename the PostgreSQL case and cut it off from its history.
/// </summary>
[CiScenario(
    "Retries (SQL Server)",
    "The retry scenario on SQL Server: 10 workers drain 1,000 always-failing jobs with two immediate retries, so every job runs three times before it ends Failed.",
    "A retry reschedules and re-claims the job; on SQL Server that re-claim goes through its own claim statement. This covers that provider's retry path.",
    Order = 56)]
[CaseLabel(nameof(Provider), "SqlServer", "SQL Server")]
[Config(typeof(ServerBenchmarkConfig))]
public class RetrySqlServerBenchmark : RetryBenchmark
{
    /// <summary>
    /// One value, so it is no column in the report; it is here because the diagnoser and the comparer
    /// recognise a SQL Server case by a parameter of this name.
    /// </summary>
    [Params(BenchmarkProvider.SqlServer)]
    public BenchmarkProvider Provider { get; set; }

    protected override BenchmarkProvider Database => Provider;
}
