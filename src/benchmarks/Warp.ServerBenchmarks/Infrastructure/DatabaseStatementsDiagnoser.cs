using System.Collections.Concurrent;
using System.Runtime.InteropServices;
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

    private readonly ConcurrentDictionary<BenchmarkCase, Counters> _deltas = new();
    private readonly ConcurrentDictionary<BenchmarkCase, TimeSpan> _elapsed = new();
    private Reading? _before;
    private long _beforeTimestamp;

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
                _beforeTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                break;

            case HostSignal.AfterActualRun:
                // A failed read on either side leaves the case with no metric, which the comparer reports
                // as a missing measurement. Recording zero instead made one side's delta the server's whole
                // cumulative total, or negative.
                if (_before is not { } before || ReadCounters(provider) is not { } after)
                {
                    break;
                }

                var delta = Delta(before, after);
                var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_beforeTimestamp);
                _deltas.AddOrUpdate(parameters.BenchmarkCase, delta, (_, existing) => existing + delta);
                _elapsed.AddOrUpdate(parameters.BenchmarkCase, elapsed, (_, existing) => existing + elapsed);
                break;

            default:
                break;
        }
    }

    public IEnumerable<Metric> ProcessResults(DiagnoserResults results)
    {
        if (!_deltas.TryGetValue(results.BenchmarkCase, out var delta))
        {
            yield break;
        }

        // A window that wrote no jobs is an idle server: its cost is per second, not per job. Timed
        // between the same two readings the counters came from, so both halves cover one window.
        if (delta.Jobs <= 0)
        {
            if (_elapsed.TryGetValue(results.BenchmarkCase, out var window) && window.TotalSeconds > 0)
            {
                yield return new Metric(PerJobDescriptor.StatementsPerSecond, delta.Statements / window.TotalSeconds);
                yield return new Metric(PerJobDescriptor.BuffersPerSecond, delta.Buffers / window.TotalSeconds);
                if (delta.WalBytes > 0)
                {
                    yield return new Metric(PerJobDescriptor.WalBytesPerSecond, delta.WalBytes / window.TotalSeconds);
                }
            }

            yield break;
        }

        yield return new Metric(PerJobDescriptor.Statements, delta.Statements / (double)delta.Jobs);
        yield return new Metric(PerJobDescriptor.Buffers, delta.Buffers / (double)delta.Jobs);

        // SQL Server has no WAL figure in dm_exec_query_stats; a zero there is "not measured", not "none".
        if (delta.WalBytes > 0)
        {
            yield return new Metric(PerJobDescriptor.WalBytes, delta.WalBytes / (double)delta.Jobs);
        }
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

    private static Reading? ReadCounters(BenchmarkProvider provider) =>
        provider == BenchmarkProvider.SqlServer ? ReadSqlServerCounters() : ReadPostgresCounters();

    /// <summary>
    /// What happened between two readings.
    /// <para>
    /// PostgreSQL's counters only grow, so its totals subtract. SQL Server's do not: a plan that is
    /// recompiled or evicted takes its whole count out of <c>dm_exec_query_stats</c>, so subtracting two
    /// totals subtracted everything that plan had done BEFORE the window as well. On an idle server,
    /// where a window holds a few hundred statements and a plan may have run a few dozen times during
    /// start-up and the settle, one recompile moved the result by several percent: identical builds
    /// read about 4% apart, which is why that row could not be gated. So SQL Server is compared plan by
    /// plan. A plan present in both readings with the same compile time contributes its difference; a
    /// plan compiled inside the window contributes all of its executions, which all happened inside it;
    /// a plan gone from the second reading contributes nothing, losing only its share of the window.
    /// </para>
    /// </summary>
    private static Counters Delta(Reading before, Reading after)
    {
        var totals = after.Totals - before.Totals;
        if (before.Plans is not { } beforePlans || after.Plans is not { } afterPlans)
        {
            return totals;
        }

        long statements = 0;
        long buffers = 0;
        foreach (var (id, plan) in afterPlans)
        {
            var sameCompile = beforePlans.TryGetValue(id, out var earlier)
                && earlier.Compiled == plan.Compiled
                && earlier.Executions <= plan.Executions;

            statements += sameCompile ? plan.Executions - earlier.Executions : plan.Executions;
            buffers += sameCompile ? plan.LogicalReads - earlier.LogicalReads : plan.LogicalReads;
        }

        return totals with { Statements = statements, Buffers = buffers };
    }

    /// <summary>
    /// Numerator AND denominator from the same view over the same window, which is what makes this
    /// independent of BenchmarkDotNet's own accounting. Dividing by an invocation count was wrong twice
    /// over: the signals bracket the whole measured phase rather than one iteration, and
    /// <c>DiagnoserResults.TotalOperations</c> counts every phase including warmup and jitting.
    /// Counting the job rows actually written inside the window answers it directly, and is checkable
    /// against the lab, which measures the same thing.
    /// </summary>
    private static Reading? ReadPostgresCounters()
    {
        try
        {
            using var connection = new NpgsqlConnection(PostgresConnectionString);
            connection.Open();

            using var command = connection.CreateCommand();

            // The read excludes itself: it is issued against pg_stat_statements, so without the filter
            // the instrument would count its own traffic.
            //
            // Buffers are the pages each statement touched, cached or read. They are the measure of work
            // a statement count cannot see: the PostgreSQL claim that took several jobs issued the SAME
            // number of statements while each one walked the whole table, 4.3 s a claim at 300k rows.
            // WAL bytes are the write side of the same question.
            command.CommandText = @"
                SELECT
                    COALESCE(SUM(calls) FILTER (WHERE query NOT LIKE '%pg_stat_statements%'), 0) AS statements,
                    COALESCE(SUM(rows) FILTER (WHERE query ILIKE 'INSERT INTO warp.job (%'), 0) AS jobs,
                    COALESCE(SUM(shared_blks_hit + shared_blks_read) FILTER (WHERE query NOT LIKE '%pg_stat_statements%'), 0) AS buffers,
                    COALESCE(SUM(wal_bytes) FILTER (WHERE query NOT LIKE '%pg_stat_statements%'), 0) AS wal
                FROM pg_stat_statements";

            using var reader = command.ExecuteReader();

            return reader.Read()
                ? new Reading(new Counters(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), Convert.ToInt64(reader.GetValue(3))), null)
                : null;
        }
        catch (NpgsqlException)
        {
            // A diagnoser must never take down the run it is observing: a missing extension or a
            // container already torn down is a lost measurement, not a failed benchmark.
            return null;
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
    /// plans still in cache, so a plan evicted mid-window loses its executions inside the window and the
    /// count reads low by that much (<see cref="Delta"/> keeps it from losing more). It is why the SQL
    /// Server number should not be read as directly comparable to the PostgreSQL one. <c>leaf_insert_count</c> for the job table is the denominator:
    /// rows written, measured at the storage engine rather than reconstructed from statement text.
    /// </para>
    /// </summary>
    private static Reading? ReadSqlServerCounters()
    {
        try
        {
            using var connection = new SqlConnection(SqlServerConnectionString);
            connection.Open();

            using var command = connection.CreateCommand();

            // One batch, two result sets. Sent as one batch so its text contains dm_exec_query_stats
            // throughout, which is how the read keeps itself out of the count.
            //
            // First, one row per cached plan statement (see Delta for why not a total).
            //
            // Second, the job rows written. Every database on the instance, resolved by name per
            // database: the connection is to master while the fixture writes to a warpbench_* database
            // whose name the host never learns, so DB_ID()/OBJECT_ID() here resolved in master and
            // counted master's inserts. index_id 0/1 is the heap or clustered index only:
            // leaf_insert_count is per index, so summing every index multiplied each job row by the
            // table's index count.
            command.CommandText = @"
                SELECT
                    CONVERT(varchar(130), qs.plan_handle, 1) + ':' + CAST(qs.statement_start_offset AS varchar(12)) AS id,
                    qs.creation_time,
                    qs.execution_count,
                    qs.total_logical_reads
                FROM sys.dm_exec_query_stats qs
                CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
                WHERE st.text NOT LIKE '%dm_exec_query_stats%';

                SELECT COALESCE(SUM(os.leaf_insert_count), 0)
                FROM sys.dm_db_index_operational_stats(NULL, NULL, NULL, NULL) os
                WHERE os.index_id IN (0, 1)
                  AND os.database_id > 4
                  AND OBJECT_SCHEMA_NAME(os.object_id, os.database_id) = 'warp'
                  AND OBJECT_NAME(os.object_id, os.database_id) = 'Job';";

            using var reader = command.ExecuteReader();

            var plans = new Dictionary<string, PlanStats>(StringComparer.Ordinal);
            long statements = 0;
            long buffers = 0;
            while (reader.Read())
            {
                var plan = new PlanStats(reader.GetDateTime(1), Convert.ToInt64(reader.GetValue(2)), Convert.ToInt64(reader.GetValue(3)));

                // A repeated key would be the view repeating a row. Added together, so the count stays
                // whole if it ever does.
                plans[reader.GetString(0)] = plans.TryGetValue(reader.GetString(0), out var same)
                    ? same with { Executions = same.Executions + plan.Executions, LogicalReads = same.LogicalReads + plan.LogicalReads }
                    : plan;
                statements += plan.Executions;
                buffers += plan.LogicalReads;
            }

            if (!reader.NextResult() || !reader.Read())
            {
                return null;
            }

            return new Reading(new Counters(statements, Convert.ToInt64(reader.GetValue(0)), buffers, 0), plans);
        }
        catch (SqlException)
        {
            return null;
        }
    }

    /// <summary>One cached plan statement: when it was compiled and what it has done since.</summary>
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct PlanStats(DateTime Compiled, long Executions, long LogicalReads);

    /// <summary>One reading of a server: its totals, and on SQL Server the plans behind them.</summary>
    private sealed record Reading(Counters Totals, Dictionary<string, PlanStats>? Plans);

    /// <summary>A server-wide counter reading, and the difference between two.</summary>
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct Counters(long Statements, long Jobs, long Buffers, long WalBytes)
    {
        public static Counters operator -(Counters a, Counters b) =>
            new(a.Statements - b.Statements, a.Jobs - b.Jobs, a.Buffers - b.Buffers, a.WalBytes - b.WalBytes);

        public static Counters operator +(Counters a, Counters b) =>
            new(a.Statements + b.Statements, a.Jobs + b.Jobs, a.Buffers + b.Buffers, a.WalBytes + b.WalBytes);
    }

    private sealed class PerJobDescriptor : IMetricDescriptor
    {
        public static readonly PerJobDescriptor Statements = new("StatementsPerJob", "Statements/job", "Database statements per job, measured over the run", "stmt");

        public static readonly PerJobDescriptor Buffers = new("BuffersPerJob", "Buffers/job", "Database pages touched per job, cached or read", "blk");

        public static readonly PerJobDescriptor WalBytes = new("WalBytesPerJob", "WAL/job", "Write-ahead log bytes per job (PostgreSQL)", "B");

        public static readonly PerJobDescriptor StatementsPerSecond = new("StatementsPerSecond", "Statements/s", "Database statements per second, with no jobs running", "stmt");

        public static readonly PerJobDescriptor BuffersPerSecond = new("BuffersPerSecond", "Buffers/s", "Database pages touched per second, with no jobs running", "blk");

        public static readonly PerJobDescriptor WalBytesPerSecond = new("WalBytesPerSecond", "WAL/s", "Write-ahead log bytes per second, with no jobs running (PostgreSQL)", "B");

        private PerJobDescriptor(string id, string displayName, string legend, string unit)
        {
            Id = id;
            DisplayName = displayName;
            Legend = legend;
            Unit = unit;
        }

        public string Id { get; }

        public string DisplayName { get; }

        public string Legend { get; }

        public string NumberFormat => "N2";

        public UnitType UnitType => UnitType.Dimensionless;

        public string Unit { get; }

        public bool TheGreaterTheBetter => false;

        public int PriorityInCategory => 0;

        public bool GetIsAvailable(Metric metric) => true;
    }
}
