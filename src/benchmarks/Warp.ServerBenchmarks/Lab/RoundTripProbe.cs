using System.Diagnostics;
using System.Globalization;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Warp.ServerBenchmarks.Lab;

/// <summary>
/// How many statements this machine can push at a given concurrency, with no Warp code involved.
/// <para>
/// Warp's throughput stops improving well before the database runs out of CPU, and the per-job
/// statement count does not rise with worker count — so the workers are waiting on something that is
/// neither lock contention nor query cost. A statement is a round trip, and a round trip has a floor.
/// If a bare <c>SELECT 1</c> hits the same wall at the same concurrency, the ceiling belongs to the
/// client-to-database channel and the only lever that moves it is fewer round trips per job.
/// </para>
/// <para>
/// Both shapes are measured because Warp uses both: a held connection is the floor, and taking one
/// from the pool per operation is what a scope-per-job actually does — the gap between them is the
/// price of the pool reset.
/// </para>
/// </summary>
public static class RoundTripProbe
{
    private static readonly int[] Levels = [1, 2, 5, 10, 20, 40];

    public static async Task RunAsync(int seconds, string? existingConnection)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        // An externally supplied database is how this runs without the host's port-forward proxy in
        // the path: the probe and Postgres sit on one container network and talk directly.
        PostgreSqlContainer? container = null;
        var connectionString = existingConnection;

        if (connectionString is null)
        {
            container = new PostgreSqlBuilder()
                .WithImage("postgres:latest")
                .Build();

            Console.WriteLine("Starting Postgres...");
            await container.StartAsync();
            connectionString = container.GetConnectionString();
        }

        // Warm the pool and the server so the first level does not pay for both.
        await MeasureAsync(connectionString, 1, 2, persistent: true, report: false);

        Console.WriteLine();
        Console.WriteLine("-- one connection held open per task (pure round trip) --");

        foreach (var level in Levels)
        {
            await MeasureAsync(connectionString, level, seconds, persistent: true, report: true);
        }

        Console.WriteLine();
        Console.WriteLine("-- a connection taken from the pool per operation (adds the reset) --");

        foreach (var level in Levels)
        {
            await MeasureAsync(connectionString, level, seconds, persistent: false, report: true);
        }

        Console.WriteLine();

        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }

    private static async Task MeasureAsync(
        string connectionString, int concurrency, int seconds, bool persistent, bool report)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var counts = new long[concurrency];
        var clock = Stopwatch.StartNew();

        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(slot => Task.Run(async () =>
        {
            if (persistent)
            {
                await using var held = new NpgsqlConnection(connectionString);
                await held.OpenAsync(CancellationToken.None);

                while (!stop.IsCancellationRequested)
                {
                    await ExecuteAsync(held);
                    counts[slot]++;
                }

                return;
            }

            while (!stop.IsCancellationRequested)
            {
                await using var pooled = new NpgsqlConnection(connectionString);
                await pooled.OpenAsync(CancellationToken.None);
                await ExecuteAsync(pooled);
                counts[slot]++;
            }
        })));

        clock.Stop();

        if (!report)
        {
            return;
        }

        var perSecond = counts.Sum() / clock.Elapsed.TotalSeconds;

        Console.WriteLine(
            $"   {concurrency,3} concurrent  {perSecond,9:N0} stmt/sec  "
            + $"{concurrency * 1000 / perSecond,6:N2} ms per statement");
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("SELECT 1", connection);
        await command.ExecuteScalarAsync(CancellationToken.None);
    }
}
