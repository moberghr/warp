using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;
using Warp.Core;
using Warp.Provider.PostgreSql;
using Warp.Worker;

namespace Warp.ServerBenchmarks.Lab;

/// <summary>
/// Counts what a completely idle Warp server costs its database: no jobs published, no dashboard
/// open, shipped default intervals.
/// <para>
/// Measured with <c>pg_stat_statements</c> rather than an EF interceptor. The worker and the server
/// tasks run on <c>WarpServerContext</c>, not on the user's <c>TContext</c> (§2.14), so a
/// TContext-scoped interceptor sees none of their traffic - it reports a flat zero. Counting inside
/// the database captures every statement whichever context issued it, and is ground truth besides.
/// </para>
/// </summary>
public static class IdleServerProbe
{
    public static async Task RunAsync(int workerCount, TimeSpan settle, TimeSpan window, string track, int servers = 1)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        await using var container = new PostgreSqlBuilder()
            .WithImage("postgres:latest")
            .WithCommand("-c", "shared_preload_libraries=pg_stat_statements", "-c", $"pg_stat_statements.track={track}")
            .Build();

        Console.WriteLine($"Booting Postgres (pg_stat_statements.track={track}) + full AddWarpServer on default intervals...");
        await container.StartAsync();
        var connectionString = container.GetConnectionString();

        await ExecuteAsync(connectionString, "CREATE EXTENSION IF NOT EXISTS pg_stat_statements;");

        var hosts = new List<IHost>();
        for (var i = 0; i < servers; i++)
        {
            hosts.Add(BuildHost(connectionString, workerCount));
        }

        var host = hosts[0];

        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<TestContext>().Database.EnsureCreatedAsync();
        }

        foreach (var h in hosts)
        {
            await h.StartAsync();
        }

        // The container is handled by `await using`, but the hosts were not: a throw mid-measurement
        // left every server running, still polling, until the process happened to exit.
        try
        {
            Console.WriteLine($"Settling {settle.TotalSeconds:N0}s so worker backoff reaches MaxPollingInterval...");
            await Task.Delay(settle);

            await ExecuteAsync(connectionString, "SELECT pg_stat_statements_reset();");

            Console.WriteLine($"Measuring {window.TotalSeconds:N0}s of a completely idle server, workers={workerCount}.");
            Console.WriteLine();

            var sw = Stopwatch.StartNew();
            await Task.Delay(window);
            sw.Stop();

            var rows = await ReadStatementsAsync(connectionString);

            var total = rows.Sum(x => x.Calls);
            Console.WriteLine($"{"statement",-52}{"calls",9}{"per sec",10}{"share",8}");
            foreach (var row in rows.Take(18))
            {
                Console.WriteLine(
                    $"{Shorten(row.Query),-52}{row.Calls,9:N0}{row.Calls / sw.Elapsed.TotalSeconds,10:N2}" +
                    $"{row.Calls / (double)total,8:P1}");
            }

            Console.WriteLine();
            Console.WriteLine($"{"TOTAL",-52}{total,9:N0}{total / sw.Elapsed.TotalSeconds,10:N2}");
            Console.WriteLine();
            Console.WriteLine(
                $"{servers} idle server(s), {workerCount} workers each: "
                + $"{total / sw.Elapsed.TotalSeconds * 60:N0} statements/minute total, "
                + $"{total / sw.Elapsed.TotalSeconds * 60 / servers:N0} per server (track={track}).");
            Console.WriteLine();

            var reset = rows.Where(x => IsConnectionReset(x.Query)).Sum(x => x.Calls);
            var txn = rows.Where(x => IsTransactionControl(x.Query)).Sum(x => x.Calls);
            var locks = rows.Where(x => x.Query.Contains("advisory", StringComparison.OrdinalIgnoreCase)
                                        && !IsConnectionReset(x.Query)).Sum(x => x.Calls);
            var work = total - reset - txn - locks;

            Console.WriteLine($"{"bucket",-34}{"calls",9}{"per min",10}{"share",8}");
            foreach (var (name, calls) in new[]
                     {
                         ("connection reset (pool churn)", reset),
                         ("transaction control", txn),
                         ("advisory locks", locks),
                         ("actual Warp table traffic", work),
                     })
            {
                Console.WriteLine(
                    $"{name,-34}{calls,9:N0}{calls / sw.Elapsed.TotalSeconds * 60,10:N0}{calls / (double)total,8:P1}");
            }
        }
        finally
        {
            foreach (var h in hosts)
            {
                try
                {
                    await h.StopAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  teardown: host stop failed: {ex.Message}");
                }

                h.Dispose();
            }
        }
    }

    private static bool IsConnectionReset(string query) =>
        query.StartsWith("DISCARD", StringComparison.OrdinalIgnoreCase)
        || query.StartsWith("RESET", StringComparison.OrdinalIgnoreCase)
        || query.StartsWith("CLOSE", StringComparison.OrdinalIgnoreCase)
        || query.StartsWith("UNLISTEN", StringComparison.OrdinalIgnoreCase)
        || query.StartsWith("DEALLOCATE", StringComparison.OrdinalIgnoreCase)
        || query.StartsWith("SET SESSION AUTHORIZATION", StringComparison.OrdinalIgnoreCase)
        || query.Contains("pg_advisory_unlock_all", StringComparison.OrdinalIgnoreCase);

    private static bool IsTransactionControl(string query) =>
        query.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase)
        || query.StartsWith("COMMIT", StringComparison.OrdinalIgnoreCase)
        || query.StartsWith("ROLLBACK", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One server process. Several are booted against the same database so per-tick coordination
    /// cost can be seen: a per-iteration lock is paid by EVERY server every interval, which a
    /// single-server probe cannot show.
    /// </summary>
    private static IHost BuildHost(string connectionString, int workerCount)
    {
        return Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Error))
            .ConfigureServices(services =>
            {
                services.AddDbContext<TestContext>(options => options.UseNpgsql(connectionString)
                    .UseSnakeCaseNamingConvention());

                // Deliberately NOT overriding intervals: this must measure shipped defaults, not a
                // benchmark-tuned configuration.
                services.AddWarpServer<TestContext>(config =>
                {
                    config.UsePostgreSql();
                    config.WorkerCount = workerCount;
                });
            })
            .Build();
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Reads EVERY statement, not a top-N. The rows feed the headline statements/minute, the per-server
    /// figure, every share percentage and the "work" bucket computed as total minus the classified
    /// ones — so truncating here silently undercounts all of them by the tail, and an idle server emits
    /// well over forty distinct normalized statements. Only the printed list is capped.
    /// </summary>
    private static async Task<List<(long Calls, string Query)>> ReadStatementsAsync(string connectionString)
    {
        var results = new List<(long Calls, string Query)>();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT calls, query
            FROM pg_stat_statements
            WHERE query NOT LIKE '%pg_stat_statements%'
            ORDER BY calls DESC;
            """,
            connection);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        return results;
    }

    /// <summary>Collapses a normalized statement to something that fits a terminal column.</summary>
    private static string Shorten(string query)
    {
        var text = string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return text.Length <= 50 ? text : text[..49] + "…";
    }
}
