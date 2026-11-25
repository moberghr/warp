using System.Diagnostics;
using Npgsql;

namespace Warp.ServerBenchmarks.Infrastructure;

/// <summary>
/// Lock-wait time on PostgreSQL, which keeps no cumulative counter for it (<c>log_lock_waits</c> only logs
/// waits past <c>deadlock_timeout</c>, a second by default, and <c>pg_stat_database</c> counts deadlocks, not
/// waits). Every <see cref="Interval"/> it counts the lock requests not yet granted in <c>pg_locks</c> and adds
/// that many intervals of waiting, so the total is backend-milliseconds spent waiting, at the sampling
/// interval's resolution: a wait shorter than the interval is seen in proportion to how often it lands on a
/// sample. Advisory TRY-locks never wait and are never seen; a blocking advisory lock is.
/// <para>
/// The query carries the text <c>pg_stat_statements</c> in a comment, which is what the statement counter
/// filters its own reads out by, so sampling adds nothing to the statements, buffers or DB time it reports.
/// </para>
/// </summary>
internal sealed class PostgresLockSampler
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(50);

    private readonly CancellationTokenSource _stop = new();
    private readonly Task<double?> _loop;

    private PostgresLockSampler(string connectionString)
    {
        _loop = Task.Run(() => RunAsync(connectionString, _stop.Token));
    }

    public static PostgresLockSampler Start(string connectionString) => new(connectionString);

    /// <summary>Milliseconds of lock waiting observed, or null when the sampling failed.</summary>
    public double? Stop()
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

    private static async Task<double?> RunAsync(string connectionString, CancellationToken ct)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM pg_locks WHERE NOT granted /* pg_stat_statements: lock sampler */";

            double waitingMs = 0;
            var last = Stopwatch.GetTimestamp();
            while (!ct.IsCancellationRequested)
            {
                var waiting = Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None));
                var now = Stopwatch.GetTimestamp();
                waitingMs += waiting * Stopwatch.GetElapsedTime(last, now).TotalMilliseconds;
                last = now;

                try
                {
                    await Task.Delay(Interval, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            return waitingMs;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (NpgsqlException)
        {
            return null;
        }
    }
}
