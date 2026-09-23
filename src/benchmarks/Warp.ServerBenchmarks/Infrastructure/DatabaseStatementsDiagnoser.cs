using System.Collections.Concurrent;
using BenchmarkDotNet.Analysers;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Validators;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Warp.ServerBenchmarks.Infrastructure;

/// <summary>
/// Reports DATABASE statements per job as a first-class BenchmarkDotNet metric, on either provider.
/// <para>
/// This is the quantity worth gating on in this project. Measured across the 7.1.0 revalidation,
/// statements per job reproduced to 0.1-2.7% within a run and to within 5% between passes, while the
/// timings of those same runs spread up to 417%. Carrying the count inside BenchmarkDotNet means its
/// iteration control, outlier handling and exporters apply to the metric that actually matters.
/// </para>
/// <para>
/// Reads the database named in the environment, which is the only channel that works: BenchmarkDotNet
/// runs the benchmark in a CHILD process while diagnosers run in the HOST, so a container the child
/// created is unreachable from here.
/// </para>
/// <para>
/// Both numbers are server-wide for that instance rather than per database, because the child creates
/// a fresh database per fixture whose name the host does not know. Benchmarks therefore have to run
/// one at a time against a server — which they do, BenchmarkDotNet being sequential — and anything
/// else touching it during a run would be counted in.
/// </para>
/// </summary>
public class DatabaseStatementsDiagnoser : IDiagnoser
{
    private static readonly string? PostgresConnectionString =
        Environment.GetEnvironmentVariable(PostgresServerFixture.ConnectionStringVariable);

    private static readonly string? SqlServerConnectionString =
        Environment.GetEnvironmentVariable(PostgresServerFixture.SqlServerConnectionStringVariable);

    private readonly ConcurrentDictionary<BenchmarkCase, (long Statements, long Jobs)> _deltas = new();
    private (long Statements, long Jobs) _before;

    public IEnumerable<string> Ids => ["DatabaseStatements"];

    public IEnumerable<IExporter> Exporters => [];

    public IEnumerable<IAnalyser> Analysers => [];

    public RunMode GetRunMode(BenchmarkCase benchmarkCase) => RunMode.NoOverhead;

    public bool RequiresBlockingAcknowledgments(BenchmarkCase benchmarkCase) => false;

    public void Handle(HostSignal signal, DiagnoserActionParameters parameters)
    {
        var provider = ProviderOf(parameters.BenchmarkCase);

        if (ConnectionStringFor(provider) is null)
        {
            return;
        }

        switch (signal)
        {
            case HostSignal.BeforeActualRun:
                _before = ReadCounters(provider);
                break;

            case HostSignal.AfterActualRun:
                var after = ReadCounters(provider);
                var delta = (Statements: after.Statements - _before.Statements, Jobs: after.Jobs - _before.Jobs);

                _deltas.AddOrUpdate(
                    parameters.BenchmarkCase,
                    delta,
                    (_, existing) => (existing.Statements + delta.Statements, existing.Jobs + delta.Jobs));
                break;

            default:
                break;
        }
    }

    public IEnumerable<Metric> ProcessResults(DiagnoserResults results)
    {
        if (!_deltas.TryGetValue(results.BenchmarkCase, out var delta) || delta.Jobs <= 0)
        {
            yield break;
        }

        yield return new Metric(StatementsPerJobDescriptor.Instance, delta.Statements / (double)delta.Jobs);
    }

    public void DisplayResults(ILogger logger)
    {
    }

    public IEnumerable<ValidationError> Validate(ValidationParameters validationParameters)
    {
        if (PostgresConnectionString is null && SqlServerConnectionString is null)
        {
            const string Message =
                "Neither WARP_BENCH_POSTGRES nor WARP_BENCH_SQLSERVER is set, so statements per job "
                + "will not be reported.";

            yield return new ValidationError(false, Message);
        }
    }

    /// <summary>
    /// Which database a benchmark case runs against, from its own <c>Provider</c> parameter.
    /// <para>
    /// Read from the case rather than from configuration because one run sweeps both, so the diagnoser
    /// has to ask the right server for each row it reports.
    /// </para>
    /// </summary>
    private static BenchmarkProvider ProviderOf(BenchmarkCase benchmarkCase) =>
        benchmarkCase.Parameters.Items
            .Where(x => string.Equals(x.Name, "Provider", StringComparison.Ordinal))
            .Select(x => x.Value as BenchmarkProvider?)
            .FirstOrDefault() ?? BenchmarkProvider.PostgreSql;

    private static string? ConnectionStringFor(BenchmarkProvider provider) =>
        provider == BenchmarkProvider.SqlServer ? SqlServerConnectionString : PostgresConnectionString;

    private static (long Statements, long Jobs) ReadCounters(BenchmarkProvider provider) =>
        provider == BenchmarkProvider.SqlServer ? ReadSqlServerCounters() : ReadPostgresCounters();

    /// <summary>
    /// Numerator AND denominator from the same view over the same window, which is what makes this
    /// independent of BenchmarkDotNet's own accounting. Dividing by an invocation count was wrong twice
    /// over: the signals bracket the whole measured phase rather than one iteration, and
    /// <c>DiagnoserResults.TotalOperations</c> counts every phase including warmup and jitting.
    /// Counting the job rows actually written inside the window answers it directly, and is checkable
    /// against the lab, which measures the same thing.
    /// </summary>
    private static (long Statements, long Jobs) ReadPostgresCounters()
    {
        try
        {
            using var connection = new NpgsqlConnection(PostgresConnectionString);
            connection.Open();

            using var command = connection.CreateCommand();

            // The read excludes itself: it is issued against pg_stat_statements, so without the filter
            // the instrument would count its own traffic.
            command.CommandText = @"
                SELECT
                    COALESCE(SUM(calls) FILTER (WHERE query NOT LIKE '%pg_stat_statements%'), 0) AS statements,
                    COALESCE(SUM(rows) FILTER (WHERE query ILIKE 'INSERT INTO warp.job (%'), 0) AS jobs
                FROM pg_stat_statements";

            using var reader = command.ExecuteReader();

            return reader.Read() ? (reader.GetInt64(0), reader.GetInt64(1)) : (0, 0);
        }
        catch (NpgsqlException)
        {
            // A diagnoser must never take down the run it is observing: a missing extension or a
            // container already torn down is a lost measurement, not a failed benchmark.
            return (0, 0);
        }
    }

    /// <summary>
    /// The SQL Server analogue, and the reason a SQL Server arm can gate anything at all.
    /// <para>
    /// Allocations are identical C# on both providers and time is not gated, so a SQL Server arm
    /// without a statement count would prove only that the benchmark runs. Statements are what can
    /// catch the two providers drifting apart — which nothing currently does, and which rule 6.9
    /// records as an open risk in as many words when it applied the scalar claim predicate to both and
    /// left SQL Server's plans unmeasured.
    /// </para>
    /// <para>
    /// <c>dm_exec_query_stats</c> is a weaker instrument than <c>pg_stat_statements</c>: it sees only
    /// plans still in cache, so an eviction mid-window loses those executions and the count reads low.
    /// On an otherwise idle instance over a short window that is rare, and both sides of a comparison
    /// are exposed to it equally — but it is why the SQL Server number should not be read as directly
    /// comparable to the PostgreSQL one. <c>leaf_insert_count</c> for the job table is the denominator:
    /// rows written, measured at the storage engine rather than reconstructed from statement text.
    /// </para>
    /// </summary>
    private static (long Statements, long Jobs) ReadSqlServerCounters()
    {
        try
        {
            using var connection = new SqlConnection(SqlServerConnectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT
                    (SELECT COALESCE(SUM(execution_count), 0) FROM sys.dm_exec_query_stats) AS statements,
                    (SELECT COALESCE(SUM(leaf_insert_count), 0)
                     FROM sys.dm_db_index_operational_stats(DB_ID(), OBJECT_ID('warp.Job'), NULL, NULL)) AS jobs";

            using var reader = command.ExecuteReader();

            return reader.Read()
                ? (Convert.ToInt64(reader.GetValue(0)), Convert.ToInt64(reader.GetValue(1)))
                : (0, 0);
        }
        catch (SqlException)
        {
            return (0, 0);
        }
    }

    private sealed class StatementsPerJobDescriptor : IMetricDescriptor
    {
        public static readonly StatementsPerJobDescriptor Instance = new();

        public string Id => "StatementsPerJob";

        public string DisplayName => "Statements/job";

        public string Legend => "Database statements per job, measured over the run";

        public string NumberFormat => "N2";

        public UnitType UnitType => UnitType.Dimensionless;

        public string Unit => "stmt";

        public bool TheGreaterTheBetter => false;

        public int PriorityInCategory => 0;

        public bool GetIsAvailable(Metric metric) => true;
    }
}
