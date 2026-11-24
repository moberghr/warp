using BenchmarkDotNet.Attributes;
using Warp.ServerBenchmarks.Infrastructure;

namespace Warp.ServerBenchmarks.Benchmarks;

/// <summary>
/// <see cref="CompletionBatchBenchmark"/> on SQL Server. A subclass rather than a <c>Provider</c> parameter on that class,
/// because BenchmarkDotNet puts every parameter in the case's full name and the release history compares
/// cases by it: a parameter would rename the PostgreSQL case and cut it off from its history.
/// </summary>
[CiScenario(
    "Completion batching (SQL Server)",
    "The completion-batching scenario on SQL Server: a dispatcher with 10 workers drains 1,000 jobs, writing each completion on its own or grouping up to 50 into one statement.",
    "SQL Server writes a batch through its own statement (OPENJSON with pinned joins, rule 6.13), whose plan regressed once already; nothing else measures it under a batching workload.",
    Order = 21)]
[CaseLabel(nameof(Provider), "SqlServer", "SQL Server")]
[Config(typeof(ServerBenchmarkConfig))]
public class CompletionBatchSqlServerBenchmark : CompletionBatchBenchmark
{
    /// <summary>
    /// One value, so it is no column in the report; it is here because the diagnoser and the comparer
    /// recognise a SQL Server case by a parameter of this name.
    /// </summary>
    [Params(BenchmarkProvider.SqlServer)]
    public BenchmarkProvider Provider { get; set; }

    protected override BenchmarkProvider Database => Provider;
}
