using System.Collections.Concurrent;
using System.Globalization;
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
/// Reports DATABASE statements per operation as a first-class BenchmarkDotNet metric, read from
/// <c>pg_stat_statements</c> around each measured run.
/// <para>
/// This is the quantity worth gating on in this project. Measured across the 7.1.0 revalidation,
/// statements per job reproduced to 0.1-2.7% within a run and to within 5% between passes, while the
/// timings of the same runs spread up to 417%. Letting BenchmarkDotNet carry the count means its
/// iteration control, outlier handling, multimodality detection and exporters all apply to the metric
/// that actually matters, instead of that metric living in a parallel harness.
/// </para>
/// <para>
/// Reads the database named by <see cref="PostgresServerFixture.ConnectionStringVariable"/>, which is
/// the only channel that works: BenchmarkDotNet runs the benchmark in a CHILD process while diagnosers
/// run in the HOST, so a container the child created is unreachable from here. The environment gives
/// both processes the same target. The server must have been started with
/// <c>shared_preload_libraries=pg_stat_statements</c>; without it this reports nothing rather than
/// failing the run.
/// </para>
/// <para>
/// The count is server-wide for that instance, not per database, because the child creates a fresh
/// database per fixture whose name the host does not know. Benchmarks therefore have to run one at a
/// time against this server — which they do, BenchmarkDotNet being sequential — and anything else
/// touching the same PostgreSQL during a run would be counted in.
/// </para>
/// </summary>
public class PgStatStatementsDiagnoser : IDiagnoser
{
    private readonly ConcurrentDictionary<BenchmarkCase, List<double>> _perOperation = new();
    private long _before;

    private static readonly string? ConnectionString =
        Environment.GetEnvironmentVariable(PostgresServerFixture.ConnectionStringVariable);

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
                _before = ReadTotalCalls();
                break;

            case HostSignal.AfterActualRun:
                var delta = ReadTotalCalls() - _before;
                _perOperation.GetOrAdd(parameters.BenchmarkCase, _ => []).Add(delta / (double)UnitsOfWork(parameters.BenchmarkCase));
                break;
        }
    }

    public IEnumerable<Metric> ProcessResults(DiagnoserResults results)
    {
        if (!_perOperation.TryGetValue(results.BenchmarkCase, out var samples) || samples.Count == 0)
        {
            yield break;
        }

        // The MEDIAN, not the mean. A single cold first iteration is enough to drag a mean of three
        // somewhere no run actually was, and the first iteration against a fresh database reliably is
        // cold — that trap cost a whole measurement session once already.
        var ordered = samples.Order().ToArray();
        var median = ordered.Length % 2 == 1
            ? ordered[ordered.Length / 2]
            : (ordered[(ordered.Length / 2) - 1] + ordered[ordered.Length / 2]) / 2;

        yield return new Metric(StatementsPerOperationDescriptor.Instance, median);
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
                "PgStatStatementsDiagnoser has no connection string; statements per operation will not be reported.");
        }
    }

    /// <summary>
    /// How many JOBS one measured iteration moved, so the metric is per job rather than per
    /// invocation.
    /// <para>
    /// A benchmark here invokes once and drains thousands of jobs, so a raw per-invocation figure is
    /// six digits wide and comparable to nothing. Dividing by the <c>JobCount</c> parameter puts it in
    /// the same units as every published claim and as the lab's own <c>statements/job</c>, which is the
    /// whole point of measuring it. A benchmark without that parameter falls back to per invocation.
    /// </para>
    /// </summary>
    private static long UnitsOfWork(BenchmarkCase benchmarkCase)
    {
        var invocations = Math.Max(benchmarkCase.Job.Run.InvocationCount, 1);

        var jobCount = benchmarkCase.Parameters.Items
            .Where(x => string.Equals(x.Name, "JobCount", StringComparison.Ordinal))
            .Select(x => x.Value as int?)
            .FirstOrDefault();

        return invocations * Math.Max(jobCount ?? 1, 1);
    }

    private static long ReadTotalCalls()
    {
        try
        {
            using var connection = new NpgsqlConnection(ConnectionString);
            connection.Open();

            using var command = connection.CreateCommand();

            // Scoped to this database, and the read itself is excluded — it is issued against
            // pg_stat_statements, so without the filter the instrument would count itself.
            command.CommandText = """
                SELECT COALESCE(SUM(calls), 0)
                FROM pg_stat_statements
                WHERE query NOT LIKE '%pg_stat_statements%'
                """;

            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        catch (NpgsqlException)
        {
            // A diagnoser must never take down the run it is observing: a missing extension or a
            // container already torn down is a lost measurement, not a failed benchmark.
            return 0;
        }
    }

    private sealed class StatementsPerOperationDescriptor : IMetricDescriptor
    {
        public static readonly StatementsPerOperationDescriptor Instance = new();

        public string Id => "StatementsPerJob";

        public string DisplayName => "Statements/job";

        public string Legend => "Database statements per job (pg_stat_statements, median of iterations)";

        public string NumberFormat => "N2";

        public UnitType UnitType => UnitType.Dimensionless;

        public string Unit => "stmt";

        public bool TheGreaterTheBetter => false;

        public int PriorityInCategory => 0;

        public bool GetIsAvailable(Metric metric) => true;
    }
}
