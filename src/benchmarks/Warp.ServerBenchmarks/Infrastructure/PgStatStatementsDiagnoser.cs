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
using Npgsql;

namespace Warp.ServerBenchmarks.Infrastructure;

/// <summary>
/// Reports DATABASE statements per job as a first-class BenchmarkDotNet metric, read from
/// <c>pg_stat_statements</c> around the measured run.
/// <para>
/// This is the quantity worth gating on in this project. Measured across the 7.1.0 revalidation,
/// statements per job reproduced to 0.1-2.7% within a run and to within 5% between passes, while the
/// timings of those same runs spread up to 417%. Carrying the count inside BenchmarkDotNet means its
/// iteration control, outlier handling and exporters apply to the metric that actually matters.
/// </para>
/// <para>
/// Reads the database named by <see cref="PostgresServerFixture.ConnectionStringVariable"/>, which is
/// the only channel that works: BenchmarkDotNet runs the benchmark in a CHILD process while diagnosers
/// run in the HOST, so a container the child created is unreachable from here. The server must have
/// been started with <c>shared_preload_libraries=pg_stat_statements</c>; without it this reports
/// nothing rather than failing the run.
/// </para>
/// <para>
/// Both numbers are server-wide for that instance rather than per database, because the child creates
/// a fresh database per fixture whose name the host does not know. Benchmarks therefore have to run
/// one at a time against this server — which they do, BenchmarkDotNet being sequential — and anything
/// else touching the same PostgreSQL during a run would be counted in.
/// </para>
/// </summary>
public class PgStatStatementsDiagnoser : IDiagnoser
{
    private static readonly string? ConnectionString =
        Environment.GetEnvironmentVariable(PostgresServerFixture.ConnectionStringVariable);

    private readonly ConcurrentDictionary<BenchmarkCase, (long Statements, long Jobs)> _deltas = new();
    private (long Statements, long Jobs) _before;

    public IEnumerable<string> Ids => ["PgStatStatements"];

    public IEnumerable<IExporter> Exporters => [];

    public IEnumerable<IAnalyser> Analysers => [];

    public RunMode GetRunMode(BenchmarkCase benchmarkCase) => RunMode.NoOverhead;

    public bool RequiresBlockingAcknowledgments(BenchmarkCase benchmarkCase) => false;

    public void Handle(HostSignal signal, DiagnoserActionParameters parameters)
    {
        if (ConnectionString is null)
        {
            return;
        }

        switch (signal)
        {
            case HostSignal.BeforeActualRun:
                _before = ReadCounters();
                break;

            case HostSignal.AfterActualRun:
                var after = ReadCounters();
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
        if (ConnectionString is null)
        {
            yield return new ValidationError(
                false,
                $"{PostgresServerFixture.ConnectionStringVariable} is not set, so statements per job will not be reported.");
        }
    }

    /// <summary>
    /// Reads the statement count and the number of job rows written, in one query over one window.
    /// <para>
    /// Taking the numerator AND the denominator from the same view is what makes this independent of
    /// BenchmarkDotNet's own accounting, and both alternatives to it were tried and were wrong.
    /// <c>BeforeActualRun</c>/<c>AfterActualRun</c> bracket the whole measured phase rather than one
    /// iteration, so dividing by a single iteration's jobs over-reported by the iteration count — 42
    /// statements per job against the lab's 13.6 for the same work. Dividing by
    /// <c>DiagnoserResults.TotalOperations</c> then under-reported at 5.3, because that counts every
    /// phase including warmup and jitting. Counting the job rows actually inserted inside the window
    /// answers the question directly, and is checkable against the lab, which measures the same thing.
    /// </para>
    /// <para>
    /// The read excludes itself: it is issued against <c>pg_stat_statements</c>, so without the filter
    /// the instrument would count its own traffic.
    /// </para>
    /// </summary>
    private static (long Statements, long Jobs) ReadCounters()
    {
        try
        {
            using var connection = new NpgsqlConnection(ConnectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    COALESCE(SUM(calls) FILTER (WHERE query NOT LIKE '%pg_stat_statements%'), 0) AS statements,
                    COALESCE(SUM(rows) FILTER (WHERE query ILIKE 'INSERT INTO warp.job (%'), 0) AS jobs
                FROM pg_stat_statements
                """;

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

    private sealed class StatementsPerJobDescriptor : IMetricDescriptor
    {
        public static readonly StatementsPerJobDescriptor Instance = new();

        public string Id => "StatementsPerJob";

        public string DisplayName => "Statements/job";

        public string Legend => "Database statements per job (pg_stat_statements, measured over the run)";

        public string NumberFormat => "N2";

        public UnitType UnitType => UnitType.Dimensionless;

        public string Unit => "stmt";

        public bool TheGreaterTheBetter => false;

        public int PriorityInCategory => 0;

        public bool GetIsAvailable(Metric metric) => true;
    }
}
