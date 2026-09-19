using System.Collections.Concurrent;
using Npgsql;

namespace Warp.ServerBenchmarks.Lab;

/// <summary>
/// Samples what the database's backends are doing while load runs.
/// <para>
/// Throughput stops improving long before Postgres runs out of CPU, and the serialized fraction that
/// caps it survives being split across processes — so it is shared, but statement counts and exec
/// time cannot say what it is. <c>pg_stat_activity</c> can: a backend parked in <c>ClientRead</c> is
/// idle waiting for the client (the bottleneck is round trips or client CPU), one in <c>Lock:*</c> is
/// contending on rows, and one in <c>LWLock:*</c> is contending inside Postgres itself. These are
/// mutually exclusive diagnoses, which is the point.
/// </para>
/// </summary>
public sealed class WaitSampler : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, int> _samples = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private int _sweeps;
    private int _failures;

    public WaitSampler(string connectionString, TimeSpan interval)
    {
        _loop = Task.Run(async () => await SampleAsync(connectionString, interval));
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();

        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
            // Expected on stop.
        }
        finally
        {
            // A sampler is instrumentation; it must never be the reason a measured run fails. Without
            // the finally a faulted loop rethrows here and, since ClusterLab samples inside an
            // `await using` in its repeat loop, takes the run's own reporting down with it.
            _stop.Dispose();
        }
    }

    public void Report()
    {
        var total = _samples.Values.Sum();

        if (total == 0)
        {
            Console.WriteLine($"   no backend samples collected ({_failures} failed sweeps) — shares unavailable.");

            return;
        }

        // Coverage is part of the result: shares of a handful of sweeps look identical to shares of
        // thousands, and a sampler that died early would otherwise report with full confidence.
        Console.WriteLine(
            $"   backend state while load ran ({_sweeps:N0} sweeps, {_failures:N0} failed):");

        foreach (var entry in _samples.OrderByDescending(x => x.Value).Take(10))
        {
            Console.WriteLine($"   {entry.Value * 100.0 / total,6:N1}%  {entry.Key}");
        }

        Console.WriteLine();
    }

    private async Task SampleAsync(string connectionString, TimeSpan interval)
    {
        await using var connection = new NpgsqlConnection(connectionString);

        const string Sql = """
            SELECT state, wait_event_type, wait_event
            FROM pg_stat_activity
            WHERE datname = current_database()
              AND pid <> pg_backend_pid()
            """;

        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (connection.State != System.Data.ConnectionState.Open)
                {
                    await connection.OpenAsync(_stop.Token);
                }

                await using var command = new NpgsqlCommand(Sql, connection);
                await using var reader = await command.ExecuteReaderAsync(_stop.Token);

                while (await reader.ReadAsync(_stop.Token))
                {
                    var state = await reader.IsDBNullAsync(0, _stop.Token) ? "?" : reader.GetString(0);
                    var type = await reader.IsDBNullAsync(1, _stop.Token) ? "Running" : reader.GetString(1);
                    var name = await reader.IsDBNullAsync(2, _stop.Token) ? string.Empty : reader.GetString(2);

                    _samples.AddOrUpdate($"{state,-20} {type}{(name.Length > 0 ? ":" + name : string.Empty)}", 1, (_, n) => n + 1);
                }

                _sweeps++;
            }
            catch (OperationCanceledException)
            {
                return;
            }
#pragma warning disable CA1031 // Instrumentation must not fail the run it is observing.
            catch (Exception)
#pragma warning restore CA1031
            {
                _failures++;
            }

            try
            {
                await Task.Delay(interval, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
