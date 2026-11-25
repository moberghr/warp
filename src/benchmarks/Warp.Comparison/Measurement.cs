using System.Diagnostics;
using System.Globalization;
using Npgsql;

namespace Warp.Comparison;

/// <summary>The harness's own database work, which every measurement leaves out.</summary>
internal static class Harness
{
    /// <summary>In the text of every harness query (left out of the statement count) and the harness connection's application name (left out of the connections).</summary>
    public const string Tag = "comparison-harness";

    private static int _queries;

    /// <summary>Queries the harness ran on the benchmark database inside the window: each is a transaction of its own, taken off the commits.</summary>
    public static int Queries => Volatile.Read(ref _queries);

    public static void CountQuery() => Interlocked.Increment(ref _queries);

    public static void ResetQueries() => Interlocked.Exchange(ref _queries, 0);
}

/// <summary>
/// The database's own counters for the benchmark database, read from a monitoring connection on the server's
/// default database so the reads are not counted: statements, WAL and execution time from
/// <c>pg_stat_statements</c> (harness queries left out by their tag), commits from <c>pg_stat_database</c>.
/// </summary>
internal sealed record DatabaseCounters(long Statements, long WalBytes, double DbMs, long Commits)
{
    public static async Task ResetAsync(string monitor, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(monitor);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT pg_stat_statements_reset()", connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    public static async Task<DatabaseCounters> ReadAsync(string monitor, string database, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(monitor);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT
              (SELECT coalesce(sum(calls), 0) FROM pg_stat_statements s JOIN pg_database d ON d.oid = s.dbid
                 WHERE d.datname = @db AND s.query NOT LIKE '%{Harness.Tag}%'),
              (SELECT coalesce(sum(wal_bytes), 0) FROM pg_stat_statements s JOIN pg_database d ON d.oid = s.dbid
                 WHERE d.datname = @db AND s.query NOT LIKE '%{Harness.Tag}%'),
              (SELECT coalesce(sum(total_exec_time + total_plan_time), 0) FROM pg_stat_statements s JOIN pg_database d ON d.oid = s.dbid
                 WHERE d.datname = @db AND s.query NOT LIKE '%{Harness.Tag}%'),
              (SELECT xact_commit FROM pg_stat_database WHERE datname = @db)
            """,
            connection);
        command.Parameters.AddWithValue("db", database);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        return new DatabaseCounters(
            Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
            Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
            Convert.ToDouble(reader.GetValue(2), CultureInfo.InvariantCulture),
            Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// The library's connections to the benchmark database, every 50 ms: the most open at once, and the average left
/// open doing nothing. The harness's connection is left out by its application name.
/// </summary>
internal sealed class ConnectionSampler : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task<(int Peak, double AverageIdle)> _loop;

    public ConnectionSampler(string monitor, string database)
    {
        _loop = Task.Run(() => RunAsync(monitor, database, _stop.Token));
    }

    public async Task<(int Peak, double AverageIdle)> StopAsync()
    {
        await _stop.CancelAsync();

        return await _loop;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _stop.Dispose();
    }

    private static async Task<(int Peak, double AverageIdle)> RunAsync(string monitor, string database, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(monitor);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT count(*), count(*) FILTER (WHERE state = 'idle') FROM pg_stat_activity
            WHERE datname = @db AND backend_type = 'client backend' AND application_name <> '{Harness.Tag}'
            """,
            connection);
        command.Parameters.AddWithValue("db", database);

        var peak = 0;
        double idleTime = 0;
        double elapsed = 0;
        var last = Stopwatch.GetTimestamp();
        while (!ct.IsCancellationRequested)
        {
            await using (var reader = await command.ExecuteReaderAsync(CancellationToken.None))
            {
                await reader.ReadAsync(CancellationToken.None);
                var open = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
                var idle = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
                var now = Stopwatch.GetTimestamp();
                var span = Stopwatch.GetElapsedTime(last, now).TotalMilliseconds;
                last = now;
                peak = Math.Max(peak, open);
                idleTime += idle * span;
                elapsed += span;
            }

            try
            {
                await Task.Delay(50, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return (peak, elapsed > 0 ? idleTime / elapsed : 0);
    }
}

/// <summary>One arm's run, as written to its JSON file and read back by the summary.</summary>
internal sealed record RunResult(
    string Engine,
    string Job,
    int Jobs,
    int Servers,
    int Workers,
    double DrainSeconds,
    double FirstToLastSeconds,
    double JobsPerSecondFirstToLast,
    double JobsPerSecondP10ToP90,
    double StatementsPerJob,
    double WalBytesPerJob,
    double DbMsPerJob,
    double CommitsPerJob,
    double ProcessCpuMsPerJob,
    double AllocatedKbPerJob,
    int PeakConnections,
    double AverageIdleConnections,
    string PostgresVersion,
    string Commit,
    string HangfireVersion);
