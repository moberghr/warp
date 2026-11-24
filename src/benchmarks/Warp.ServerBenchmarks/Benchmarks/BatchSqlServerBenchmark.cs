using BenchmarkDotNet.Attributes;
using Warp.ServerBenchmarks.Infrastructure;

namespace Warp.ServerBenchmarks.Benchmarks;

/// <summary>
/// <see cref="BatchBenchmark"/> on SQL Server. A subclass rather than a <c>Provider</c> parameter on that class,
/// because BenchmarkDotNet puts every parameter in the case's full name and the release history compares
/// cases by it: a parameter would rename the PostgreSQL case and cut it off from its history.
/// </summary>
[CiScenario(
    "Batches and continuations (SQL Server)",
    "The batch scenario on SQL Server: five batches of 100 jobs, each followed by a continuation batch of 100, so 1,000 jobs pass through the Orchestrator. 10 workers.",
    "The Orchestrator's readiness queries were measured on SQL Server separately (rule 6.11) and have their own plans there. This keeps them from regressing unseen.",
    Order = 96)]
[CaseLabel(nameof(Provider), "SqlServer", "SQL Server")]
[Config(typeof(ServerBenchmarkConfig))]
public class BatchSqlServerBenchmark : BatchBenchmark
{
    /// <summary>
    /// One value, so it is no column in the report; it is here because the diagnoser and the comparer
    /// recognise a SQL Server case by a parameter of this name.
    /// </summary>
    [Params(BenchmarkProvider.SqlServer)]
    public BenchmarkProvider Provider { get; set; }

    protected override BenchmarkProvider Database => Provider;
}
