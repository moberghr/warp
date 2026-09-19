using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Provider.PostgreSql;

namespace Warp.ServerBenchmarks.Lab;

/// <summary>
/// Isolates what the concurrency semaphore costs the database, with the worker and the job path taken
/// out of the picture — one EF read plus one acquire/release per iteration, nothing else.
/// <para>
/// The load lab measures the concurrency addon at ~38 statements per job spent on connection hygiene.
/// Npgsql resets a pooled connection with a single <c>DISCARD ALL</c>, but falls back to a seven
/// statement sequence (<c>CLOSE ALL</c>, <c>UNLISTEN *</c>, <c>SELECT pg_advisory_unlock_all()</c>,
/// <c>RESET ALL</c>, <c>DISCARD TEMP</c>, <c>DISCARD SEQUENCES</c>,
/// <c>SET SESSION AUTHORIZATION DEFAULT</c>) on any connector carrying prepared statements, because
/// <c>DISCARD ALL</c> would deallocate them. The lock provider is handed the SAME connection string as
/// the user's DbContext, so both share one Npgsql pool — and the hypothesis this probe tests is that
/// the lock's prepared statements flip the shared connectors, making every EF connection in the
/// process pay the seven-statement reset as well.
/// </para>
/// <para>
/// <c>--separate-pool</c> hands the semaphore provider a connection string differing only in
/// <c>Application Name</c>. That value is part of Npgsql's pool key, so the lock gets its own pool and
/// its own connectors. If the hypothesis holds, <c>DISCARD ALL</c> reappears for the EF side and the
/// per-iteration statement count falls; if it does not, the two arms read the same.
/// </para>
/// </summary>
public static class LockProbe
{
    private const string LockApplicationName = "warp-locks";

    public static async Task RunAsync(int iterations, int maxCount, bool separatePool, bool dataSource, string connectionString)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Unlike the load lab, this probe does not start a container of its own: it exists to be run
            // beside Postgres on the same network (Lab/README.md), where the proxy cost that would
            // dominate an 11-statement measurement is absent. Say so rather than failing inside Npgsql
            // with "connection string missing".
            Console.WriteLine("lockprobe requires --connection=<npgsql connection string>; it does not start a container of its own.");

            return;
        }

        await PgStats.TryCreateExtensionAsync(connectionString);
        if (!await PgStats.HasStatStatementsAsync(connectionString))
        {
            Console.WriteLine("pg_stat_statements unavailable — this probe reports nothing without it.");

            return;
        }

        // The data-source arm models the shape Warp deliberately does NOT split: the host registered an
        // NpgsqlDataSource (Aspire, Managed Identity, client certificates), UsePostgreSql hands that
        // same data source to the lock providers, and EF and the locks therefore share one pool with no
        // connection string in between to suffix. This arm measures whether that shape actually pays the
        // seven-statement reset, rather than assuming it does.
        await using var sharedDataSource = dataSource ? NpgsqlDataSource.Create(connectionString) : null;

        if (dataSource && separatePool)
        {
            // --separate-pool has no effect here: UsePostgreSql resolves the shared data source for the
            // lock providers and never looks at a connection string, so the two would be byte-for-byte
            // the same arm. Reported as n/a rather than True, so a transcript cannot be read as evidence
            // that the split does nothing on this path.
            Console.WriteLine("note: --separate-pool is inert under --data-source; the lock providers take the shared data source.");
        }

        // The EF side always runs on the connection string as given. Only the lock side moves.
        await using var efRoot = BuildProvider(connectionString, sharedDataSource);
        await using var lockRoot = BuildProvider(separatePool ? WithApplicationName(connectionString) : connectionString, sharedDataSource);

        var semaphores = lockRoot.GetRequiredService<IWarpSemaphoreProvider>();

        await using (var scope = efRoot.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<TestContext>().Database.EnsureCreatedAsync();
        }

        // Warm both pools and Medallion's one-time setup, which would otherwise land in the window.
        await ReadOnceAsync(efRoot);
        await using (var warm = await semaphores.TryAcquireAsync("warp:probe:warm", maxCount, TimeSpan.Zero, CancellationToken.None))
        {
        }

        var before = await PgStats.CaptureAsync(connectionString, withStatements: true);
        var clock = Stopwatch.StartNew();

        for (var i = 0; i < iterations; i++)
        {
            await ReadOnceAsync(efRoot);

            // A distinct key per iteration, so every pass is an uncontended first acquire — the shape
            // the no-contention control arm measures. A shared key would serialise the probe against
            // itself and measure waiting instead of acquiring.
            var handle = await semaphores.TryAcquireAsync($"warp:probe:{i}", maxCount, TimeSpan.Zero, CancellationToken.None);
            if (handle == null)
            {
                Console.WriteLine($"  iteration {i}: acquire returned null — keys are colliding, results are not comparable.");

                continue;
            }

            await handle.DisposeAsync();
        }

        clock.Stop();
        var delta = (await PgStats.CaptureAsync(connectionString, withStatements: true)).Since(before);

        Console.WriteLine();
        Console.WriteLine($"== lock probe : {iterations:N0} iterations (1 EF read + 1 acquire/release each), "
            + $"maxCount={maxCount}, separatePool={(dataSource ? "n/a" : separatePool.ToString())}, "
            + $"dataSource={dataSource}, {clock.Elapsed.TotalSeconds:N1}s");
        Console.WriteLine();
        Console.WriteLine($"{"statement",-52}{"calls",10}{"per iter",11}{"exec ms",11}");

        foreach (var statement in delta.Statements.Take(16))
        {
            Console.WriteLine(
                $"{Shorten(statement.Query),-52}{statement.Calls,10:N0}{statement.Calls / (double)iterations,11:N2}{statement.TotalExecMs,11:N1}");
        }

        Console.WriteLine();
        Console.WriteLine($"total statements {delta.TotalCalls:N0} = {delta.TotalCalls / (double)iterations:N2} per iteration");
        Console.WriteLine();
    }

    /// <summary>
    /// Appends an <c>Application Name</c> the user's own connection string will not carry. Npgsql
    /// includes that value in the pool key, so this is the whole of the pool split — no second
    /// connection string to configure, and <c>pg_stat_activity</c> gains a label naming the sessions.
    /// </summary>
    private static string WithApplicationName(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { ApplicationName = LockApplicationName }.ConnectionString;

    private static ServiceProvider BuildProvider(string connectionString, NpgsqlDataSource? dataSource)
    {
        // Resolved through DI rather than constructed directly: the provider implementations are
        // internal to the package (§0.5), and IWarpSemaphoreProvider is the seam Warp itself uses.
        var services = new ServiceCollection();

        if (dataSource is not null)
        {
            services.AddDbContext<TestContext>(options => options.UseNpgsql(dataSource).UseSnakeCaseNamingConvention());
        }
        else
        {
            services.AddDbContext<TestContext>(options => options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());
        }

        services.AddWarp<TestContext>(config => config.UsePostgreSql());

        return services.BuildServiceProvider();
    }

    /// <summary>One trivial EF read per iteration — enough to rent and return a pooled connection, which is what the reset cost attaches to.</summary>
    private static async Task ReadOnceAsync(IServiceProvider root)
    {
        await using var scope = root.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TestContext>();

        await context.Set<ConcurrencyLimit>()
            .AsNoTracking()
            .Where(x => x.Name == "warp:probe:absent")
            .FirstOrDefaultAsync();
    }

    private static string Shorten(string query)
    {
        var text = string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return text.Length <= 50 ? text : text[..49] + "…";
    }
}
