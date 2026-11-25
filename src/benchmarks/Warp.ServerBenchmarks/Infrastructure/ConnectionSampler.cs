using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Warp.ServerBenchmarks.Infrastructure;

/// <summary>
/// How many database connections Warp holds, sampled every <see cref="Interval"/> across the measured window:
/// the most it had open at one moment, and how many it kept open on average doing nothing (idle in the pool).
/// No database keeps a running count of either, so they are sampled like PostgreSQL's lock waits
/// (<see cref="PostgresLockSampler"/>).
/// <para>
/// Counted: sessions on the benchmark databases (<c>warpbench_*</c>), every pool of the process (the application's,
/// and on PostgreSQL Warp's own <c>:warp-server</c> and <c>:warp-locks</c>). Not counted: the harness's own
/// connection (application name <see cref="Harness.Tag"/> on PostgreSQL, its language on SQL Server) and this
/// sampler, which connects to the server's default database. PostgreSQL also gets the peak of each pool, printed
/// with the window, since 8.0 opens a third pool there.
/// </para>
/// <para>
/// Each sample's query is tagged like the lock sampler's, so the statement counter leaves it out.
/// </para>
/// </summary>
internal sealed class ConnectionSampler
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(50);

    private readonly CancellationTokenSource _stop = new();
    private readonly Task<Result?> _loop;

    private ConnectionSampler(BenchmarkProvider provider, string connectionString)
    {
        _loop = Task.Run(() => provider == BenchmarkProvider.SqlServer
            ? RunAsync(() => new SqlConnection(connectionString), SqlServerQuery, _stop.Token)
            : RunAsync(() => new NpgsqlConnection(connectionString), PostgresQuery, _stop.Token));
    }

    /// <summary>The window's connections: the most open at once, the average open and the average idle.</summary>
    public sealed record Result(int Peak, double AverageOpen, double AverageIdle, IReadOnlyDictionary<string, int> PeakByPool);

    // One row per pool: open sessions and how many of them are idle.
    private static string PostgresQuery =>
        $"""
        SELECT COALESCE(NULLIF(application_name, ''), '(default)'), count(*), count(*) FILTER (WHERE state = 'idle')
        FROM pg_stat_activity /* pg_stat_statements: connection sampler */
        WHERE datname LIKE 'warpbench\_%' AND backend_type = 'client backend'
          AND application_name <> '{Harness.Tag}' AND pid <> pg_backend_pid()
        GROUP BY 1
        """;

    // SQL Server sessions have no pool name to read; status 'sleeping' is a session in the pool doing nothing.
    private static string SqlServerQuery =>
        $"""
        SELECT N'(all)', count(*), sum(CASE WHEN s.status = 'sleeping' THEN 1 ELSE 0 END)
        FROM sys.dm_exec_sessions s
        WHERE s.is_user_process = 1 AND s.session_id <> @@SPID
          AND s.language <> N'{Harness.SqlServerLanguage}'
          AND DB_NAME(s.database_id) LIKE N'warpbench[_]%'
        """;

    public static ConnectionSampler Start(BenchmarkProvider provider, string connectionString) => new(provider, connectionString);

    /// <summary>The window's figures, or null when the sampling failed or took no sample.</summary>
    public Result? Stop()
    {
        _stop.Cancel();

        try
        {
            return _loop.GetAwaiter().GetResult();
        }
        finally
        {
            _stop.Dispose();
        }
    }

    private static async Task<Result?> RunAsync(Func<System.Data.Common.DbConnection> open, string query, CancellationToken ct)
    {
        try
        {
            await using var connection = open();
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = query;

            var peak = 0;
            var peakByPool = new Dictionary<string, int>(StringComparer.Ordinal);
            double openTime = 0;
            double idleTime = 0;
            double elapsed = 0;
            var last = Stopwatch.GetTimestamp();
            while (!ct.IsCancellationRequested)
            {
                var total = 0;
                var idle = 0;
                await using (var reader = await command.ExecuteReaderAsync(CancellationToken.None))
                {
                    while (await reader.ReadAsync(CancellationToken.None))
                    {
                        var pool = reader.GetString(0);
                        var count = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
                        total += count;
                        idle += await reader.IsDBNullAsync(2, CancellationToken.None) ? 0 : Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture);
                        peakByPool[pool] = Math.Max(peakByPool.GetValueOrDefault(pool), count);
                    }
                }

                // Weighted by the time each sample stands for, so a slow sample does not count as one tick.
                var now = Stopwatch.GetTimestamp();
                var span = Stopwatch.GetElapsedTime(last, now).TotalMilliseconds;
                last = now;
                peak = Math.Max(peak, total);
                openTime += total * span;
                idleTime += idle * span;
                elapsed += span;

                try
                {
                    await Task.Delay(Interval, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            return elapsed > 0 ? new Result(peak, openTime / elapsed, idleTime / elapsed, peakByPool) : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (System.Data.Common.DbException)
        {
            return null;
        }
    }
}
