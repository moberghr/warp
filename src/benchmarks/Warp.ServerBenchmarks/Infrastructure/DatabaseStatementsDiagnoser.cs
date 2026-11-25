using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
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
/// else touching it during a run would be counted in. The harness's own statements (waiting for a drain,
/// cleaning between iterations) are marked and left out (<see cref="Harness"/>).
/// </para>
/// <para>
/// The benchmark process WAITS at each signal until the readings are taken
/// (<see cref="RequiresBlockingAcknowledgments"/>). Without that BenchmarkDotNet runs the child without
/// acknowledgements, so the first iteration started while the "before" reading was still being taken, and
/// the extra iteration the memory diagnoser runs after the window started while the "after" one was.
/// </para>
/// </summary>
public class DatabaseStatementsDiagnoser : IDiagnoser
{
    /// <summary>
    /// How long PostgreSQL may hold a backend's table counters before publishing them: a backend that has
    /// gone idle within a second of its last flush waits <c>PGSTAT_IDLE_INTERVAL</c> (10 s) before the next.
    /// The job count (<c>n_tup_ins</c>) is read only after this much quiet, or the last iteration's inserts
    /// could still be pending in the publisher's idle backend.
    /// </summary>
    private static readonly TimeSpan PostgresStatsSettle = TimeSpan.FromSeconds(11);

    private static readonly string? PostgresConnectionString =
        Environment.GetEnvironmentVariable(PostgresServerFixture.ConnectionStringVariable);

    private static readonly string? SqlServerConnectionString =
        Environment.GetEnvironmentVariable(PostgresServerFixture.SqlServerConnectionStringVariable);

    // Where the benchmark (child) process keeps its client-side counts, named before the child starts so it
    // inherits the variable (RoundTripCounter). One file per host process.
    private static readonly string RoundTripFile = NameRoundTripFile();

    private readonly ConcurrentDictionary<BenchmarkCase, Counters> _deltas = new();
    private readonly ConcurrentDictionary<BenchmarkCase, TimeSpan> _elapsed = new();
    private readonly ConcurrentDictionary<BenchmarkCase, ClientCounts> _client = new();
    private readonly ConcurrentDictionary<BenchmarkCase, bool> _invalid = new();
    private Reading? _before;
    private long _beforeTimestamp;
    private PostgresLockSampler? _lockSampler;
    private ConnectionSampler? _connectionSampler;
    private readonly ConcurrentDictionary<BenchmarkCase, ConnectionSampler.Result> _connections = new();

    public IEnumerable<string> Ids => ["DatabaseStatements"];

    public IEnumerable<IExporter> Exporters => [];

    public IEnumerable<IAnalyser> Analysers => [];

    public RunMode GetRunMode(BenchmarkCase benchmarkCase) => RunMode.NoOverhead;

    // True: the benchmark waits while the readings are taken (see the class summary).
    public bool RequiresBlockingAcknowledgments(BenchmarkCase benchmarkCase) => true;

    /// <summary>
    /// Whether a case reports its counters per second (an idle server, which writes no jobs) rather than per
    /// job. Declared by the benchmark (<see cref="MeasuredPerSecondAttribute"/>), not inferred from a window
    /// that wrote no jobs: that also described a draining case whose job count failed to read, which then
    /// reported per-second figures and was compared as if nothing had gone wrong.
    /// </summary>
    public static bool IsPerSecond(Type? benchmarkType) =>
        benchmarkType?.GetCustomAttribute<MeasuredPerSecondAttribute>(inherit: true) is not null;

    public void Handle(HostSignal signal, DiagnoserActionParameters parameters)
    {
        var provider = ProviderOf(parameters.BenchmarkCase);

        if (ConnectionStringFor(provider) is null)
        {
            return;
        }

        var countsJobs = provider == BenchmarkProvider.PostgreSql && !IsPerSecond(parameters.BenchmarkCase.Descriptor.Type);

        switch (signal)
        {
            case HostSignal.BeforeActualRun:
                // Outside the window: let PostgreSQL publish the warm-up's table counters first, so they do
                // not land inside it.
                if (countsJobs)
                {
                    Thread.Sleep(PostgresStatsSettle);
                }

                _before = ReadCounters(provider, countsJobs);
                _beforeTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                _lockSampler = provider == BenchmarkProvider.PostgreSql ? PostgresLockSampler.Start(PostgresConnectionString!) : null;
                _connectionSampler = ConnectionSampler.Start(provider, ConnectionStringFor(provider)!);
                break;

            case HostSignal.AfterActualRun:
                // A failed read on either side leaves the case with no metric, which the comparer reports
                // as a missing measurement. Recording zero instead made one side's delta the server's whole
                // cumulative total, or negative.
                var lockWaitMs = _lockSampler?.Stop();
                _lockSampler = null;
                if (_connectionSampler?.Stop() is { } connections)
                {
                    _connections[parameters.BenchmarkCase] = connections;
                    Console.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"// Connections in the window of {parameters.BenchmarkCase.DisplayInfo}: peak {connections.Peak}, average open {connections.AverageOpen:0.0}, average idle {connections.AverageIdle:0.0}; peak per pool: {string.Join(", ", connections.PeakByPool.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key} {x.Value}"))}"));
                }

                _connectionSampler = null;
                var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_beforeTimestamp);

                if (_before is not { } before || ReadCounters(provider, countsJobs: false) is not { } after)
                {
                    _invalid[parameters.BenchmarkCase] = true;
                    break;
                }

                // The statements are read at once, the job count after the settle (the window's statements
                // must not include the idle server's while it waits). The benchmark is held meanwhile.
                if (countsJobs)
                {
                    Thread.Sleep(PostgresStatsSettle);
                    after = after with { Totals = after.Totals with { Jobs = ReadPostgresJobs() ?? -1 } };
                }

                RecordWindow(parameters.BenchmarkCase, before, after, lockWaitMs, elapsed);
                break;

            default:
                break;
        }
    }

    public IEnumerable<Metric> ProcessResults(DiagnoserResults results)
    {
        if (_invalid.ContainsKey(results.BenchmarkCase) || !_deltas.TryGetValue(results.BenchmarkCase, out var delta))
        {
            yield break;
        }

        _client.TryGetValue(results.BenchmarkCase, out var client);

        // Connections are a level, not a count of work: neither per job nor per second.
        if (_connections.TryGetValue(results.BenchmarkCase, out var connections))
        {
            yield return new Metric(PerJobDescriptor.PeakConnections, connections.Peak);
            yield return new Metric(PerJobDescriptor.IdleConnections, connections.AverageIdle);
        }

        // An idle server's cost is per second, not per job. Timed between the same two readings the counters
        // came from, so both halves cover one window.
        if (IsPerSecond(results.BenchmarkCase.Descriptor.Type))
        {
            if (_elapsed.TryGetValue(results.BenchmarkCase, out var window) && window.TotalSeconds > 0)
            {
                yield return new Metric(PerJobDescriptor.StatementsPerSecond, delta.Statements / window.TotalSeconds);
                yield return new Metric(PerJobDescriptor.BuffersPerSecond, delta.Buffers / window.TotalSeconds);
                if (delta.WalBytes > 0)
                {
                    yield return new Metric(PerJobDescriptor.WalBytesPerSecond, delta.WalBytes / window.TotalSeconds);
                }

                foreach (var metric in LoadMetrics(delta, client, window.TotalSeconds, perSecond: true))
                {
                    yield return metric;
                }
            }

            yield break;
        }

        // A draining case that wrote no job rows did not measure: its job count failed to read, or the
        // counter went backwards (SQL Server's is reset when the table's metadata is evicted). Reported as
        // no measurement, which the comparer fails, never as a per-second figure.
        if (delta.Jobs <= 0)
        {
            yield break;
        }

        yield return new Metric(PerJobDescriptor.Statements, delta.Statements / (double)delta.Jobs);
        yield return new Metric(PerJobDescriptor.Buffers, delta.Buffers / (double)delta.Jobs);
        yield return new Metric(PerJobDescriptor.JobRows, delta.Jobs);

        // SQL Server has no WAL figure in Query Store; a zero there is "not measured", not "none".
        if (delta.WalBytes > 0)
        {
            yield return new Metric(PerJobDescriptor.WalBytes, delta.WalBytes / (double)delta.Jobs);

            // The part of the WAL that is whole pages, written for the first change to each page after a
            // checkpoint. The benchmark servers run with full_page_writes off (start-bench-postgres.sh), so this
            // reads 0; anything else means a server measured with them on, whose WAL/job swings with checkpoints.
            yield return new Metric(PerJobDescriptor.WalFullPageImages, delta.WalFullPageImages / (double)delta.Jobs);
        }

        foreach (var metric in LoadMetrics(delta, client, delta.Jobs, perSecond: false))
        {
            yield return metric;
        }

        // Benchmarks that run two servers count the jobs each completed (TwoServersUnderLoadBenchmark).
        var serverJobs = client.FirstServerJobs + client.SecondServerJobs;
        if (serverJobs > 0)
        {
            yield return new Metric(PerJobDescriptor.SmallestServerShare, Math.Min(client.FirstServerJobs, client.SecondServerJobs) / (double)serverJobs);
            yield return new Metric(PerJobDescriptor.ServerJobs, serverJobs);
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

    /// <summary>The child's running totals, or null when it has not started counting (no fixture, no file).</summary>
    private static ClientCounts? ReadClientCounts()
    {
        try
        {
            using var stream = new FileStream(RoundTripFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[RoundTripCounter.FileSize];
            stream.ReadExactly(buffer);

            long At(RoundTripCounter.Slot slot) => BitConverter.ToInt64(buffer, (int)slot * sizeof(long));

            return new ClientCounts(
                At(RoundTripCounter.Slot.Commands),
                At(RoundTripCounter.Slot.HarnessCommands),
                At(RoundTripCounter.Slot.FirstServerJobs),
                At(RoundTripCounter.Slot.SecondServerJobs));
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

    private static Reading? ReadCounters(BenchmarkProvider provider, bool countsJobs)
    {
        if (provider == BenchmarkProvider.SqlServer)
        {
            return ReadSqlServerCounters() is { } sqlServer ? new Reading(sqlServer, ReadClientCounts()) : null;
        }

        if (ReadPostgresCounters() is not { } totals)
        {
            return null;
        }

        if (countsJobs)
        {
            if (ReadPostgresJobs() is not { } jobs)
            {
                return null;
            }

            totals = totals with { Jobs = jobs };
        }

        return new Reading(totals, ReadClientCounts());
    }

    /// <summary>
    /// What happened between two readings, kept for <see cref="ProcessResults"/>. Both providers' counters only
    /// grow inside a window, so their totals subtract.
    /// <para>
    /// SQL Server's used not to: it was read from <c>dm_exec_query_stats</c>, which forgets a plan's count
    /// when the plan is recompiled or evicted, and comparing plan by plan only stopped that from reaching
    /// BEFORE the window. A recompile INSIDE the window (auto-update statistics on the job table every few
    /// seconds under a drain) still lost every execution before it: the same commit read 2.16 and then 3.10
    /// statements per job on single-worker SQL Server, the job claim's count moving from 65 to 1,020 per
    /// 1,000 jobs between runs. Query Store keeps per-query totals across recompiles (see
    /// <see cref="ReadSqlServerCounters"/>), so the difference of two totals is the window.
    /// </para>
    /// <para>
    /// PostgreSQL's hold while <c>pg_stat_statements</c> keeps every entry. When it evicts some to make room
    /// (<c>pg_stat_statements_info.dealloc</c> moved), the evicted entries' counts leave the total and the
    /// difference reads low by an unknown amount, so the window is invalid.
    /// </para>
    /// </summary>
    private void RecordWindow(BenchmarkCase benchmarkCase, Reading before, Reading after, double? lockWaitMs, TimeSpan elapsed)
    {
        if (after.Totals.Deallocations != before.Totals.Deallocations)
        {
            _invalid[benchmarkCase] = true;
            Console.WriteLine($"// pg_stat_statements evicted entries inside the window of {benchmarkCase.DisplayInfo}: its database counts are not reported");

            return;
        }

        var delta = after.Totals - before.Totals;

        // PostgreSQL keeps no cumulative lock-wait time; it is sampled across the window instead.
        if (lockWaitMs is { } sampled)
        {
            delta = delta with { LockWaitMs = sampled };
        }

        if (before.Client is { } clientBefore && after.Client is { } clientAfter)
        {
            var client = clientAfter - clientBefore;

            // Each harness statement is a transaction of its own (no harness code opens one), so on PostgreSQL,
            // whose count is exact, the harness's commands are its transactions. SQL Server's Transactions/sec
            // counter did not move one for one with statements when checked, so its figure keeps them.
            if (ProviderOf(benchmarkCase) == BenchmarkProvider.PostgreSql && client.HarnessCommands > 0)
            {
                delta = delta with { Transactions = Math.Max(0, delta.Transactions - client.HarnessCommands) };
            }

            _client.AddOrUpdate(benchmarkCase, client, (_, existing) => existing + client);
        }

        _deltas.AddOrUpdate(benchmarkCase, delta, (_, existing) => existing + delta);
        _elapsed.AddOrUpdate(benchmarkCase, elapsed, (_, existing) => existing + elapsed);
    }

    /// <summary>
    /// The database-load columns: time the database spent, commands sent, transactions and lock waits, per job
    /// (or per second for an idle window). Each is left out when its source gave nothing, so it reads "—"
    /// rather than a zero that was never measured.
    /// </summary>
    private static IEnumerable<Metric> LoadMetrics(Counters delta, ClientCounts? client, double per, bool perSecond)
    {
        if (per <= 0)
        {
            yield break;
        }

        if (delta.DbTimeMs > 0)
        {
            yield return new Metric(perSecond ? PerJobDescriptor.DbTimePerSecond : PerJobDescriptor.DbTime, delta.DbTimeMs / per);
        }

        if (client is { Commands: > 0 } counted)
        {
            yield return new Metric(perSecond ? PerJobDescriptor.RoundTripsPerSecond : PerJobDescriptor.RoundTrips, counted.Commands / per);
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

    /// <summary>
    /// Numerator from <c>pg_stat_statements</c>, denominator from the job table's own insert counter
    /// (<see cref="ReadPostgresJobs"/>), both over the same window. That is what makes this independent of
    /// BenchmarkDotNet's own accounting: dividing by an invocation count was wrong twice over, the signals
    /// bracketing the whole measured phase rather than one iteration, and
    /// <c>DiagnoserResults.TotalOperations</c> counting every phase including warmup and jitting.
    /// <para>
    /// Every entry whose text carries <see cref="Harness.Tag"/> is the harness's and left out, as is this
    /// read's own (it reads pg_stat_statements).
    /// </para>
    /// </summary>
    private static Counters? ReadPostgresCounters()
    {
        try
        {
            using var connection = new NpgsqlConnection(PostgresConnectionString);
            connection.Open();

            using var command = connection.CreateCommand();

            // Buffers are the pages each statement touched, cached or read. They are the measure of work
            // a statement count cannot see: the PostgreSQL claim that took several jobs issued the SAME
            // number of statements while each one walked the whole table, 4.3 s a claim at 300k rows.
            // WAL bytes are the write side of the same question.
            command.CommandText = @"
                WITH counted AS (
                    SELECT * FROM pg_stat_statements
                    WHERE query NOT LIKE '%pg_stat_statements%' AND query NOT LIKE '%" + Harness.Tag + @"%')
                SELECT
                    (SELECT COALESCE(SUM(calls), 0) FROM counted) AS statements,
                    (SELECT COALESCE(SUM(shared_blks_hit + shared_blks_read), 0) FROM counted) AS buffers,
                    (SELECT COALESCE(SUM(wal_bytes), 0) FROM counted) AS wal,
                    (SELECT COALESCE(SUM(wal_fpi), 0) FROM counted) AS wal_fpi,
                    (SELECT COALESCE(SUM(total_exec_time + total_plan_time), 0) FROM counted) AS db_ms,
                    (SELECT COALESCE(SUM(xact_commit + xact_rollback), 0) FROM pg_stat_database WHERE datname LIKE 'warpbench\_%') AS xacts,
                    (SELECT COALESCE(SUM(deadlocks), 0) FROM pg_stat_database WHERE datname LIKE 'warpbench\_%') AS deadlocks,
                    (SELECT dealloc FROM pg_stat_statements_info) AS dealloc";

            using var reader = command.ExecuteReader();

            // Lock wait is not cumulative on PostgreSQL: -1 here means "sampled separately" (PostgresLockSampler).
            return reader.Read()
                ? new Counters(
                    Convert.ToInt64(reader.GetValue(0)),
                    0,
                    Convert.ToInt64(reader.GetValue(1)),
                    Convert.ToInt64(reader.GetValue(2)),
                    Convert.ToInt64(reader.GetValue(3)),
                    Convert.ToDouble(reader.GetValue(4)),
                    Convert.ToInt64(reader.GetValue(5)),
                    -1,
                    Convert.ToInt64(reader.GetValue(6)),
                    Convert.ToInt64(reader.GetValue(7)))
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
    /// Job rows inserted, from the job table's own counter (<c>pg_stat_user_tables.n_tup_ins</c>) in every
    /// <c>warpbench_*</c> database, like SQL Server's <c>leaf_insert_count</c>. It replaced the rows of the
    /// statements whose text began <c>INSERT INTO warp.job (</c>, which counted only the inserts written that
    /// way: a job inserted by a statement of another shape (a set-based copy, a CTE) was not a job.
    /// <para>
    /// The counter is per database and readable only from inside one, so each is read on its own connection,
    /// unpooled (a pooled one would leave a backend in the database). It is published by each backend at most
    /// once a second, and by an idle one up to ten seconds late, hence <see cref="PostgresStatsSettle"/>.
    /// </para>
    /// </summary>
    private static long? ReadPostgresJobs()
    {
        try
        {
            var databases = new List<string>();
            using (var connection = new NpgsqlConnection(PostgresConnectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"SELECT datname FROM pg_database WHERE datname LIKE 'warpbench\_%' AND datallowconn";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    databases.Add(reader.GetString(0));
                }
            }

            long jobs = 0;
            foreach (var database in databases)
            {
                var builder = new NpgsqlConnectionStringBuilder(PostgresConnectionString) { Database = database, Pooling = false };
                using var connection = new NpgsqlConnection(builder.ConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COALESCE(SUM(n_tup_ins), 0) FROM pg_stat_user_tables WHERE schemaname = 'warp' AND relname = 'job'";
                jobs += Convert.ToInt64(command.ExecuteScalar());
            }

            return jobs;
        }
        catch (NpgsqlException)
        {
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
    /// out of the count. Query Store's own internal queries (<c>is_internal_query</c>) and the harness's
    /// (compiled under <see cref="Harness.SqlServerLanguage"/>) are left out.
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
            command.Parameters.AddWithValue("@harnessLanguage", Harness.SqlServerLanguage);

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
                0,
                cpuMs,
                Convert.ToInt64(reader.GetValue(0)),
                Convert.ToDouble(reader.GetValue(1)),
                Convert.ToInt64(reader.GetValue(2)),
                0);
        }
        catch (SqlException)
        {
            return null;
        }
    }

    /// <summary>
    /// The SQL Server reading, one batch with three result sets. The first: Query Store's executions, logical
    /// reads and CPU summed over every <c>warpbench_*</c> database with Query Store on (flushed first), less
    /// internal and harness queries, the number of databases read and the number whose Query Store is not
    /// read-write. The second: job rows written across those databases (<c>leaf_insert_count</c>, heap or
    /// clustered index only, since the counter is per index), resolved by name per database because the
    /// connection is to master. The third: transactions, lock waits and deadlocks.
    /// </summary>
    internal const string SqlServerReadBatch = @"
        SET NOCOUNT ON;
        DECLARE @flush nvarchar(max) = N'';
        DECLARE @read nvarchar(max) = N'';
        DECLARE @harness smallint = (SELECT langid FROM sys.syslanguages WHERE name = @harnessLanguage);

        SELECT
            @flush = @flush + N'EXEC ' + QUOTENAME(name) + N'.sys.sp_query_store_flush_db; ',
            @read = @read + CASE WHEN @read = N'' THEN N'' ELSE N' UNION ALL ' END
                + N'SELECT SUM(rs.count_executions) AS e, SUM(rs.avg_logical_io_reads * rs.count_executions) AS r, '
                + N'(SELECT CASE WHEN o.actual_state = 2 THEN 0 ELSE 1 END FROM ' + QUOTENAME(name) + N'.sys.database_query_store_options o) AS bad, '
                + N'SUM(rs.avg_cpu_time * rs.count_executions) AS cpu '
                + N'FROM ' + QUOTENAME(name) + N'.sys.query_store_runtime_stats rs '
                + N'JOIN ' + QUOTENAME(name) + N'.sys.query_store_plan p ON p.plan_id = rs.plan_id '
                + N'JOIN ' + QUOTENAME(name) + N'.sys.query_store_query q ON q.query_id = p.query_id '
                + N'JOIN ' + QUOTENAME(name) + N'.sys.query_context_settings c ON c.context_settings_id = q.context_settings_id '
                + N'WHERE q.is_internal_query = 0 AND c.language_id <> @harness'
        FROM sys.databases
        WHERE name LIKE N'warpbench[_]%' AND state = 0 AND is_query_store_on = 1;

        IF @read = N''
            SELECT CAST(0 AS bigint), CAST(0 AS float), 0, 0, CAST(0 AS float);
        ELSE
        BEGIN
            EXEC (@flush);
            DECLARE @sum nvarchar(max) = N'SELECT COALESCE(SUM(e), 0), COALESCE(SUM(r), 0), COUNT(*), COALESCE(SUM(bad), 0), COALESCE(SUM(cpu), 0) FROM (' + @read + N') t';
            EXEC sys.sp_executesql @sum, N'@harness smallint', @harness = @harness;
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

    /// <summary>One reading: the server's totals and the benchmark process's own counts (null when not counted).</summary>
    private sealed record Reading(Counters Totals, ClientCounts? Client);

    /// <summary>The benchmark process's counts (<see cref="RoundTripCounter"/>), and the difference between two.</summary>
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct ClientCounts(long Commands, long HarnessCommands, long FirstServerJobs, long SecondServerJobs)
    {
        public static ClientCounts operator -(ClientCounts a, ClientCounts b) =>
            new(a.Commands - b.Commands, a.HarnessCommands - b.HarnessCommands, a.FirstServerJobs - b.FirstServerJobs, a.SecondServerJobs - b.SecondServerJobs);

        public static ClientCounts operator +(ClientCounts a, ClientCounts b) =>
            new(a.Commands + b.Commands, a.HarnessCommands + b.HarnessCommands, a.FirstServerJobs + b.FirstServerJobs, a.SecondServerJobs + b.SecondServerJobs);
    }

    /// <summary>A server-wide counter reading, and the difference between two.</summary>
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct Counters(
        long Statements,
        long Jobs,
        long Buffers,
        long WalBytes,
        long WalFullPageImages,
        double DbTimeMs,
        long Transactions,
        double LockWaitMs,
        long Deadlocks,
        long Deallocations)
    {
        // A negative lock wait is "not measured here" (PostgreSQL samples it), and stays negative through a
        // difference so the sampled value can replace it.
        public static Counters operator -(Counters a, Counters b) =>
            new(
                a.Statements - b.Statements,
                a.Jobs - b.Jobs,
                a.Buffers - b.Buffers,
                a.WalBytes - b.WalBytes,
                a.WalFullPageImages - b.WalFullPageImages,
                a.DbTimeMs - b.DbTimeMs,
                a.Transactions - b.Transactions,
                a.LockWaitMs < 0 ? -1 : a.LockWaitMs - b.LockWaitMs,
                a.Deadlocks - b.Deadlocks,
                a.Deallocations - b.Deallocations);

        public static Counters operator +(Counters a, Counters b) =>
            new(
                a.Statements + b.Statements,
                a.Jobs + b.Jobs,
                a.Buffers + b.Buffers,
                a.WalBytes + b.WalBytes,
                a.WalFullPageImages + b.WalFullPageImages,
                a.DbTimeMs + b.DbTimeMs,
                a.Transactions + b.Transactions,
                a.LockWaitMs < 0 || b.LockWaitMs < 0 ? -1 : a.LockWaitMs + b.LockWaitMs,
                a.Deadlocks + b.Deadlocks,
                a.Deallocations + b.Deallocations);
    }

    private sealed class PerJobDescriptor : IMetricDescriptor
    {
        public static readonly PerJobDescriptor Statements = new("StatementsPerJob", "Statements/job", "Database statements per job, measured over the run", "stmt");

        public static readonly PerJobDescriptor Buffers = new("BuffersPerJob", "Buffers/job", "Database pages touched per job, cached or read", "blk");

        public static readonly PerJobDescriptor WalBytes = new("WalBytesPerJob", "WAL/job", "Write-ahead log bytes per job (PostgreSQL)", "B");

        public static readonly PerJobDescriptor WalFullPageImages = new("WalFullPageImagesPerJob", "WAL FPI/job", "Full-page images written to the WAL per job, the first change to each page after a checkpoint (PostgreSQL)", "fpi");

        public static readonly PerJobDescriptor PeakConnections = new("PeakConnections", "Peak conns", "The most database connections Warp had open at one moment in the window (sampled every 50 ms)", "conn");

        public static readonly PerJobDescriptor IdleConnections = new("IdleConnections", "Idle conns", "Connections Warp kept open doing nothing, averaged over the window (sampled every 50 ms)", "conn");

        public static readonly PerJobDescriptor JobRows = new("JobRows", "Job rows", "Job rows written in the measured window, the per-job columns' divisor", "rows");

        public static readonly PerJobDescriptor StatementsPerSecond = new("StatementsPerSecond", "Statements/s", "Database statements per second, with no jobs running", "stmt");

        public static readonly PerJobDescriptor BuffersPerSecond = new("BuffersPerSecond", "Buffers/s", "Database pages touched per second, with no jobs running", "blk");

        public static readonly PerJobDescriptor WalBytesPerSecond = new("WalBytesPerSecond", "WAL/s", "Write-ahead log bytes per second, with no jobs running (PostgreSQL)", "B");

        public static readonly PerJobDescriptor DbTime = new("DbTimePerJob", "DB ms/job", "Database time per job: execution + planning time (PostgreSQL), CPU time (SQL Server)", "ms");

        public static readonly PerJobDescriptor DbTimePerSecond = new("DbTimePerSecond", "DB ms/s", "Database time per second (see DB ms/job)", "ms");

        // Ids keep their old names (release records and the history read them); what is counted is commands.
        public static readonly PerJobDescriptor RoundTrips = new("RoundTripsPerJob", "Commands/job", "Commands sent by the benchmark process, counted at the driver (transaction control not included)", "cmd");

        public static readonly PerJobDescriptor RoundTripsPerSecond = new("RoundTripsPerSecond", "Commands/s", "Commands sent by the benchmark process per second", "cmd");

        public static readonly PerJobDescriptor Transactions = new("TransactionsPerJob", "Xacts/job", "Transactions per job in the benchmark database", "tx");

        public static readonly PerJobDescriptor TransactionsPerSecond = new("TransactionsPerSecond", "Xacts/s", "Transactions per second in the benchmark database", "tx");

        public static readonly PerJobDescriptor LockWait = new("LockWaitMsPerJob", "Lock wait ms/job", "Time spent waiting for locks per job (sampled on PostgreSQL, LCK waits on SQL Server)", "ms");

        public static readonly PerJobDescriptor LockWaitPerSecond = new("LockWaitMsPerSecond", "Lock wait ms/s", "Time spent waiting for locks per second", "ms");

        public static readonly PerJobDescriptor Deadlocks = new("Deadlocks", "Deadlocks", "Deadlocks in the measured window", "n");

        public static readonly PerJobDescriptor SmallestServerShare = new("SmallestServerShare", "Smallest server share", "Share of the jobs completed by the server that completed fewest (two-server scenarios)", "share");

        public static readonly PerJobDescriptor ServerJobs = new("ServerJobs", "Server jobs", "Jobs completed by workers of either server (two-server scenarios)", "jobs");

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
