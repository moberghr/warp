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

    // Where the benchmark (child) process keeps its client-side round-trip count, named before the child
    // starts so it inherits the variable (RoundTripCounter). One file per host process.
    private static readonly string RoundTripFile = NameRoundTripFile();

    private readonly ConcurrentDictionary<BenchmarkCase, Counters> _deltas = new();
    private readonly ConcurrentDictionary<BenchmarkCase, TimeSpan> _elapsed = new();
    private readonly ConcurrentDictionary<BenchmarkCase, long> _roundTrips = new();
    private Reading? _before;
    private long _beforeTimestamp;
    private PostgresLockSampler? _lockSampler;

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
                _lockSampler = provider == BenchmarkProvider.PostgreSql ? PostgresLockSampler.Start(PostgresConnectionString!) : null;
                break;

            case HostSignal.AfterActualRun:
                // A failed read on either side leaves the case with no metric, which the comparer reports
                // as a missing measurement. Recording zero instead made one side's delta the server's whole
                // cumulative total, or negative.
                var lockWaitMs = _lockSampler?.Stop();
                _lockSampler = null;

                if (_before is not { } before || ReadCounters(provider) is not { } after)
                {
                    break;
                }

                var delta = Delta(before, after);

                // PostgreSQL keeps no cumulative lock-wait time; it is sampled across the window instead.
                if (lockWaitMs is { } sampled)
                {
                    delta = delta with { LockWaitMs = sampled };
                }

                if (before.RoundTrips is { } tripsBefore && after.RoundTrips is { } tripsAfter && tripsAfter >= tripsBefore)
                {
                    _roundTrips.AddOrUpdate(parameters.BenchmarkCase, tripsAfter - tripsBefore, (_, existing) => existing + tripsAfter - tripsBefore);
                }

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

                foreach (var metric in LoadMetrics(results.BenchmarkCase, delta, window.TotalSeconds, perSecond: true))
                {
                    yield return metric;
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

        foreach (var metric in LoadMetrics(results.BenchmarkCase, delta, delta.Jobs, perSecond: false))
        {
            yield return metric;
        }
    }

    public void DisplayResults(ILogger logger)
    {
    }

    /// <summary>
    /// The database-load columns: time the database spent, client round trips, transactions and lock waits,
    /// per job (or per second for an idle window). Each is left out when its source gave nothing, so it reads
    /// "—" rather than a zero that was never measured.
    /// </summary>
    private IEnumerable<Metric> LoadMetrics(BenchmarkCase benchmarkCase, Counters delta, double per, bool perSecond)
    {
        if (per <= 0)
        {
            yield break;
        }

        if (delta.DbTimeMs > 0)
        {
            yield return new Metric(perSecond ? PerJobDescriptor.DbTimePerSecond : PerJobDescriptor.DbTime, delta.DbTimeMs / per);
        }

        if (_roundTrips.TryGetValue(benchmarkCase, out var trips) && trips > 0)
        {
            yield return new Metric(perSecond ? PerJobDescriptor.RoundTripsPerSecond : PerJobDescriptor.RoundTrips, trips / per);
        }

        if (delta.Transactions > 0)
        {
            yield return new Metric(perSecond ? PerJobDescriptor.TransactionsPerSecond : PerJobDescriptor.Transactions, delta.Transactions / per);
        }

        if (delta.LockWaitMs >= 0)
        {
            yield return new Metric(perSecond ? PerJobDescriptor.LockWaitPerSecond : PerJobDescriptor.LockWait, delta.LockWaitMs / per);
        }

        if (delta.Deadlocks >= 0)
        {
            yield return new Metric(PerJobDescriptor.Deadlocks, delta.Deadlocks);
        }
    }

    private static string NameRoundTripFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"warpbench-roundtrips-{Environment.ProcessId}.bin");
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover file is overwritten by the child anyway.
        }

        Environment.SetEnvironmentVariable(RoundTripCounter.FileVariable, path);

        return path;
    }

    /// <summary>The child's running total, or null when it has not started counting (no fixture, no file).</summary>
    private static long? ReadRoundTrips()
    {
        try
        {
            using var stream = new FileStream(RoundTripFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> buffer = stackalloc byte[sizeof(long)];
            stream.ReadExactly(buffer);

            return BitConverter.ToInt64(buffer);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
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

    private static Reading? ReadCounters(BenchmarkProvider provider)
    {
        var totals = provider == BenchmarkProvider.SqlServer ? ReadSqlServerCounters() : ReadPostgresCounters();

        return totals is null ? null : new Reading(totals.Value, ReadRoundTrips());
    }

    /// <summary>
    /// What happened between two readings. Both providers' counters now only grow inside a window, so their
    /// totals subtract.
    /// <para>
    /// SQL Server's used not to: it was read from <c>dm_exec_query_stats</c>, which forgets a plan's count
    /// when the plan is recompiled or evicted, and comparing plan by plan only stopped that from reaching
    /// BEFORE the window. A recompile INSIDE the window (auto-update statistics on the job table every few
    /// seconds under a drain) still lost every execution before it: the same commit read 2.16 and then 3.10
    /// statements per job on single-worker SQL Server, the job claim's count moving from 65 to 1,020 per
    /// 1,000 jobs between runs. Query Store keeps per-query totals across recompiles (see
    /// <see cref="ReadSqlServerCounters"/>), so the difference of two totals is the window.
    /// </para>
    /// </summary>
    private static Counters Delta(Reading before, Reading after) => after.Totals - before.Totals;

    /// <summary>
    /// Numerator AND denominator from the same view over the same window, which is what makes this
    /// independent of BenchmarkDotNet's own accounting. Dividing by an invocation count was wrong twice
    /// over: the signals bracket the whole measured phase rather than one iteration, and
    /// <c>DiagnoserResults.TotalOperations</c> counts every phase including warmup and jitting.
    /// Counting the job rows actually written inside the window answers it directly, and is checkable
    /// against the lab, which measures the same thing.
    /// </summary>
    private static Counters? ReadPostgresCounters()
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
                    COALESCE(SUM(wal_bytes) FILTER (WHERE query NOT LIKE '%pg_stat_statements%'), 0) AS wal,
                    COALESCE(SUM(total_exec_time + total_plan_time) FILTER (WHERE query NOT LIKE '%pg_stat_statements%'), 0) AS db_ms,
                    (SELECT COALESCE(SUM(xact_commit + xact_rollback), 0) FROM pg_stat_database WHERE datname LIKE 'warpbench\_%') AS xacts,
                    (SELECT COALESCE(SUM(deadlocks), 0) FROM pg_stat_database WHERE datname LIKE 'warpbench\_%') AS deadlocks
                FROM pg_stat_statements";

            using var reader = command.ExecuteReader();

            // Lock wait is not cumulative on PostgreSQL: -1 here means "sampled separately" (PostgresLockSampler).
            return reader.Read()
                ? new Counters(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    Convert.ToInt64(reader.GetValue(3)),
                    Convert.ToDouble(reader.GetValue(4)),
                    Convert.ToInt64(reader.GetValue(5)),
                    -1,
                    Convert.ToInt64(reader.GetValue(6)))
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
    /// catch the two providers drifting apart.
    /// </para>
    /// <para>
    /// Counted from Query Store (<c>sys.query_store_runtime_stats</c>): executions and logical reads per
    /// query, kept across recompiles and plan-cache eviction, which <c>dm_exec_query_stats</c> (the old
    /// instrument) lost, reading low by however many executions a plan had before it was recompiled inside
    /// the window. The fixture turns Query Store on for every benchmark database (capture mode ALL, cleanup
    /// off). Query Store is per database and the host does not know the fixture's database name, so every
    /// <c>warpbench_*</c> database with Query Store on is read with dynamic SQL, after
    /// <c>sp_query_store_flush_db</c>. The read runs in master, where Query Store is off, so it keeps itself
    /// out of the count.
    /// </para>
    /// <para>
    /// A reading is INVALID, not low, when no such database has Query Store on or one of them has left
    /// read-write (Query Store goes read-only when its storage fills, and then stops counting): null is
    /// returned and the case reports no statement count, which the comparer fails as a missing measurement.
    /// <c>leaf_insert_count</c> for the job table is the denominator: rows written, measured at the
    /// storage engine.
    /// </para>
    /// </summary>
    private static Counters? ReadSqlServerCounters()
    {
        try
        {
            using var connection = new SqlConnection(SqlServerConnectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = SqlServerReadBatch;

            using var reader = command.ExecuteReader();

            // First result set: Query Store totals over every benchmark database, and how many databases
            // were read and how many of them are not counting.
            if (!reader.Read() || reader.GetInt32(2) == 0 || reader.GetInt32(3) > 0)
            {
                return null;
            }

            var statements = Convert.ToInt64(reader.GetValue(0));
            var buffers = Convert.ToInt64(Math.Round(Convert.ToDouble(reader.GetValue(1))));
            var cpuMs = Convert.ToDouble(reader.GetValue(4)) / 1000;

            if (!reader.NextResult() || !reader.Read())
            {
                return null;
            }

            var jobs = Convert.ToInt64(reader.GetValue(0));

            // Third: transactions started in the benchmark databases, instance-wide lock-wait time and deadlocks.
            if (!reader.NextResult() || !reader.Read())
            {
                return null;
            }

            return new Counters(
                statements,
                jobs,
                buffers,
                0,
                cpuMs,
                Convert.ToInt64(reader.GetValue(0)),
                Convert.ToDouble(reader.GetValue(1)),
                Convert.ToInt64(reader.GetValue(2)));
        }
        catch (SqlException)
        {
            return null;
        }
    }

    /// <summary>
    /// The SQL Server reading, one batch with two result sets. The first: Query Store's executions and
    /// logical reads summed over every <c>warpbench_*</c> database with Query Store on (flushed first), the
    /// number of databases read and the number whose Query Store is not read-write. The second: job rows
    /// written across those databases (<c>leaf_insert_count</c>, heap or clustered index only, since the
    /// counter is per index), resolved by name per database because the connection is to master.
    /// </summary>
    internal const string SqlServerReadBatch = @"
        SET NOCOUNT ON;
        DECLARE @flush nvarchar(max) = N'';
        DECLARE @read nvarchar(max) = N'';

        SELECT
            @flush = @flush + N'EXEC ' + QUOTENAME(name) + N'.sys.sp_query_store_flush_db; ',
            @read = @read + CASE WHEN @read = N'' THEN N'' ELSE N' UNION ALL ' END
                + N'SELECT (SELECT SUM(rs.count_executions) FROM ' + QUOTENAME(name) + N'.sys.query_store_runtime_stats rs) AS e, '
                + N'(SELECT SUM(rs.avg_logical_io_reads * rs.count_executions) FROM ' + QUOTENAME(name) + N'.sys.query_store_runtime_stats rs) AS r, '
                + N'(SELECT CASE WHEN o.actual_state = 2 THEN 0 ELSE 1 END FROM ' + QUOTENAME(name) + N'.sys.database_query_store_options o) AS bad, '
                + N'(SELECT SUM(rs.avg_cpu_time * rs.count_executions) FROM ' + QUOTENAME(name) + N'.sys.query_store_runtime_stats rs) AS cpu'
        FROM sys.databases
        WHERE name LIKE N'warpbench[_]%' AND state = 0 AND is_query_store_on = 1;

        IF @read = N''
            SELECT CAST(0 AS bigint), CAST(0 AS float), 0, 0, CAST(0 AS float);
        ELSE
        BEGIN
            EXEC (@flush);
            EXEC (N'SELECT COALESCE(SUM(e), 0), COALESCE(SUM(r), 0), COUNT(*), COALESCE(SUM(bad), 0), COALESCE(SUM(cpu), 0) FROM (' + @read + N') t');
        END

        SELECT COALESCE(SUM(os.leaf_insert_count), 0)
        FROM sys.dm_db_index_operational_stats(NULL, NULL, NULL, NULL) os
        WHERE os.index_id IN (0, 1)
          AND os.database_id > 4
          AND OBJECT_SCHEMA_NAME(os.object_id, os.database_id) = 'warp'
          AND OBJECT_NAME(os.object_id, os.database_id) = 'Job';

        -- object_name and instance_name are nchar(128), blank-padded, and a LIKE without a trailing wildcard
        -- does not match the padding: hence the RTRIMs.
        SELECT
            (SELECT COALESCE(SUM(cntr_value), 0) FROM sys.dm_os_performance_counters
             WHERE RTRIM(object_name) LIKE N'%:Databases' AND counter_name = N'Transactions/sec' AND RTRIM(instance_name) LIKE N'warpbench[_]%'),
            (SELECT CAST(COALESCE(SUM(wait_time_ms), 0) AS float) FROM sys.dm_os_wait_stats WHERE wait_type LIKE N'LCK[_]%'),
            (SELECT COALESCE(SUM(cntr_value), 0) FROM sys.dm_os_performance_counters
             WHERE RTRIM(object_name) LIKE N'%:Locks' AND counter_name = N'Number of Deadlocks/sec' AND RTRIM(instance_name) = N'_Total');";

    /// <summary>One reading: the server's totals and the benchmark process's client round trips (null when not counted).</summary>
    private sealed record Reading(Counters Totals, long? RoundTrips);

    /// <summary>A server-wide counter reading, and the difference between two.</summary>
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct Counters(
        long Statements,
        long Jobs,
        long Buffers,
        long WalBytes,
        double DbTimeMs,
        long Transactions,
        double LockWaitMs,
        long Deadlocks)
    {
        // A negative lock wait is "not measured here" (PostgreSQL samples it), and stays negative through a
        // difference so the sampled value can replace it.
        public static Counters operator -(Counters a, Counters b) =>
            new(
                a.Statements - b.Statements,
                a.Jobs - b.Jobs,
                a.Buffers - b.Buffers,
                a.WalBytes - b.WalBytes,
                a.DbTimeMs - b.DbTimeMs,
                a.Transactions - b.Transactions,
                a.LockWaitMs < 0 ? -1 : a.LockWaitMs - b.LockWaitMs,
                a.Deadlocks - b.Deadlocks);

        public static Counters operator +(Counters a, Counters b) =>
            new(
                a.Statements + b.Statements,
                a.Jobs + b.Jobs,
                a.Buffers + b.Buffers,
                a.WalBytes + b.WalBytes,
                a.DbTimeMs + b.DbTimeMs,
                a.Transactions + b.Transactions,
                a.LockWaitMs < 0 || b.LockWaitMs < 0 ? -1 : a.LockWaitMs + b.LockWaitMs,
                a.Deadlocks + b.Deadlocks);
    }

    private sealed class PerJobDescriptor : IMetricDescriptor
    {
        public static readonly PerJobDescriptor Statements = new("StatementsPerJob", "Statements/job", "Database statements per job, measured over the run", "stmt");

        public static readonly PerJobDescriptor Buffers = new("BuffersPerJob", "Buffers/job", "Database pages touched per job, cached or read", "blk");

        public static readonly PerJobDescriptor WalBytes = new("WalBytesPerJob", "WAL/job", "Write-ahead log bytes per job (PostgreSQL)", "B");

        public static readonly PerJobDescriptor StatementsPerSecond = new("StatementsPerSecond", "Statements/s", "Database statements per second, with no jobs running", "stmt");

        public static readonly PerJobDescriptor BuffersPerSecond = new("BuffersPerSecond", "Buffers/s", "Database pages touched per second, with no jobs running", "blk");

        public static readonly PerJobDescriptor WalBytesPerSecond = new("WalBytesPerSecond", "WAL/s", "Write-ahead log bytes per second, with no jobs running (PostgreSQL)", "B");

        public static readonly PerJobDescriptor DbTime = new("DbTimePerJob", "DB ms/job", "Database time per job: execution + planning time (PostgreSQL), CPU time (SQL Server)", "ms");

        public static readonly PerJobDescriptor DbTimePerSecond = new("DbTimePerSecond", "DB ms/s", "Database time per second (see DB ms/job)", "ms");

        public static readonly PerJobDescriptor RoundTrips = new("RoundTripsPerJob", "Round trips/job", "Commands sent by the benchmark process, counted at the driver", "rt");

        public static readonly PerJobDescriptor RoundTripsPerSecond = new("RoundTripsPerSecond", "Round trips/s", "Commands sent by the benchmark process per second", "rt");

        public static readonly PerJobDescriptor Transactions = new("TransactionsPerJob", "Xacts/job", "Transactions per job in the benchmark database", "tx");

        public static readonly PerJobDescriptor TransactionsPerSecond = new("TransactionsPerSecond", "Xacts/s", "Transactions per second in the benchmark database", "tx");

        public static readonly PerJobDescriptor LockWait = new("LockWaitMsPerJob", "Lock wait ms/job", "Time spent waiting for locks per job (sampled on PostgreSQL, LCK waits on SQL Server)", "ms");

        public static readonly PerJobDescriptor LockWaitPerSecond = new("LockWaitMsPerSecond", "Lock wait ms/s", "Time spent waiting for locks per second", "ms");

        public static readonly PerJobDescriptor Deadlocks = new("Deadlocks", "Deadlocks", "Deadlocks in the measured window", "n");

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
