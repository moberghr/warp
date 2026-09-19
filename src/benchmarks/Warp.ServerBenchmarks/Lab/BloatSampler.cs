using System.Collections.Concurrent;
using Npgsql;

namespace Warp.ServerBenchmarks.Lab;

/// <summary>
/// Tracks dead-tuple accumulation per table while load runs.
/// <para>
/// Dead tuples are a LEVEL, not a counter: they rise as updates orphan row versions and fall when
/// autovacuum reclaims them. A before/after snapshot therefore says almost nothing — a run that ends
/// with a drained table reports a clean zero whether it peaked at 3% or 43%, and the peak is the whole
/// question. This samples through the run and keeps the high-water mark.
/// </para>
/// <para>
/// The quantity matters because no update to Warp's <c>job</c> table can be HOT: <c>CurrentState</c> is
/// part of four of its six indexes, so every state transition writes new index entries and orphans the
/// old ones. The recorded collapse — a claim scan going from 3.3 ms to 253 ms, throughput from 567 to
/// 27 jobs/sec — was measured at 43% dead tuples. Nothing in the harness observed that number until
/// now; it was taken by hand in psql.
/// </para>
/// </summary>
public sealed class BloatSampler : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Peak> _peaks = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private int _sweeps;
    private int _failures;

    public BloatSampler(string connectionString, TimeSpan interval)
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
            // Instrumentation must never be the reason a measured run fails — same contract as
            // WaitSampler, which is disposed inside an `await using` in the repeat loop.
            _stop.Dispose();
        }
    }

    public void Report()
    {
        if (_sweeps == 0 || _peaks.IsEmpty)
        {
            Console.WriteLine($"   no bloat samples collected ({_failures} failed sweeps) — peaks unavailable.");
            Console.WriteLine();

            return;
        }

        // Coverage is part of the result: a sampler that died after two sweeps would otherwise report
        // a reassuringly low peak with full confidence.
        //
        // Peak is taken on the ABSOLUTE dead count, not the share, and the share is reported beside it
        // rather than ranked on. n_dead_tup is maintained incrementally and is trustworthy; n_live_tup
        // is an ESTIMATE that drifts between ANALYZEs, and after tens of thousands of updates it can
        // read a few thousand for a table holding twenty thousand rows — which sends the share to 98%
        // and makes it useless for ranking. Compare arms on the absolute count at equal job counts.
        Console.WriteLine($"   peak dead tuples while load ran ({_sweeps:N0} sweeps, {_failures:N0} failed):");
        Console.WriteLine($"   {"table",-28}{"peak dead",12}{"live (est)",12}{"dead% (est)",13}");

        foreach (var entry in _peaks.OrderByDescending(x => x.Value.Dead).Take(10))
        {
            Console.WriteLine(
                $"   {entry.Key,-28}{entry.Value.Dead,12:N0}{entry.Value.Live,12:N0}{entry.Value.DeadShare,13:P1}");
        }

        Console.WriteLine();
    }

    private async Task SampleAsync(string connectionString, TimeSpan interval)
    {
        await using var connection = new NpgsqlConnection(connectionString);

        // Schema-qualified for the same reason PgStats.ReadTablesAsync is: two same-named tables in
        // different schemas must not merge into one row.
        // n_live_tup > 0 is not a tidiness filter, it is what makes the share mean anything. The
        // measured window is bracketed by a bulk seed and a bulk delete, and at both edges the table
        // holds dead tuples against zero live rows — which scores a perfect 100% and reports the
        // teardown as if it were the drain. Early in a run the opposite happens: a few hundred rows
        // give a share that swings on noise. Samples with no live rows are not the phenomenon.
        const string Sql = """
            SELECT schemaname || '.' || relname,
                   COALESCE(n_dead_tup, 0),
                   COALESCE(n_live_tup, 0)
            FROM pg_stat_user_tables
            WHERE COALESCE(n_dead_tup, 0) > 0
              AND COALESCE(n_live_tup, 0) > 0
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
                    var name = reader.GetString(0);
                    var dead = reader.GetInt64(1);
                    var live = reader.GetInt64(2);
                    var sample = new Peak(dead, live);

                    // Keep the sweep with the most dead tuples, and keep its dead/live together — a
                    // peak from one sweep paired with another's counts describes no real moment.
                    _peaks.AddOrUpdate(
                        name,
                        sample,
                        (_, existing) => sample.Dead > existing.Dead ? sample : existing);
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

    private sealed record Peak(long Dead, long Live)
    {
        public double DeadShare => Dead + Live > 0 ? Dead / (double)(Dead + Live) : 0;
    }
}
