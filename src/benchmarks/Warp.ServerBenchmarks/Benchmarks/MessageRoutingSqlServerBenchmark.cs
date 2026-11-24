using BenchmarkDotNet.Attributes;
using Warp.ServerBenchmarks.Infrastructure;

namespace Warp.ServerBenchmarks.Benchmarks;

/// <summary>
/// <see cref="MessageRoutingBenchmark"/> on SQL Server. A subclass rather than a <c>Provider</c> parameter on that class,
/// because BenchmarkDotNet puts every parameter in the case's full name and the release history compares
/// cases by it: a parameter would rename the PostgreSQL case and cut it off from its history.
/// </summary>
[CiScenario(
    "Message routing (SQL Server)",
    "The message-routing scenario on SQL Server: 10 workers process 1,000 published messages, each routed by the message router into the jobs its handlers run.",
    "The message router takes a session-scoped lock (rule 2.16), which on SQL Server is sp_getapplock on its own connection. Nothing else measures that provider's routing hop.",
    Order = 71)]
[CaseLabel(nameof(Provider), "SqlServer", "SQL Server")]
[Config(typeof(ServerBenchmarkConfig))]
public class MessageRoutingSqlServerBenchmark : MessageRoutingBenchmark
{
    /// <summary>
    /// One value, so it is no column in the report; it is here because the diagnoser and the comparer
    /// recognise a SQL Server case by a parameter of this name.
    /// </summary>
    [Params(BenchmarkProvider.SqlServer)]
    public BenchmarkProvider Provider { get; set; }

    protected override BenchmarkProvider Database => Provider;
}
