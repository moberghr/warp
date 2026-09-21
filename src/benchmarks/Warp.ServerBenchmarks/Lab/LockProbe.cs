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

    public static async Task RunAsync(
        int iterations, int maxCount, bool separatePool, bool dataSource, bool rawLock, bool held, int concurrency, int keys, int holdMs, string connectionString)
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

        if (held)
        {
            await RunHeldAsync(efRoot, semaphores, rawLock, connectionString, iterations, maxCount, concurrency, keys, holdMs);

            clock.Stop();

            // ATTEMPTS, not acquisitions: a refusal under contention still consumes one, and
            // ReportDelta's per-iteration column divides by this. separatePool/dataSource are reported
            // for the same reason the single-cycle arm reports them — two transcripts taken with
            // different pool shapes must not be indistinguishable.
            var pool = dataSource || rawLock ? "n/a" : separatePool.ToString();
            var label = $"held: {iterations:N0} attempts, concurrency={concurrency}, keys={keys}, holdMs={holdMs}, "
                + $"rawLock={rawLock}, separatePool={pool}, dataSource={dataSource}";
            var heldDelta = (await PgStats.CaptureAsync(connectionString, withStatements: true)).Since(before);

            ReportDelta(heldDelta, iterations, label, clock.Elapsed);

            return;
        }

        for (var i = 0; i < iterations; i++)
        {
            await ReadOnceAsync(efRoot);

            if (rawLock)
            {
                await RawLockCycleAsync(connectionString, i);

                continue;
            }

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
            + $"maxCount={maxCount}, separatePool={(dataSource || rawLock ? "n/a" : separatePool.ToString())}, "
            + $"dataSource={dataSource}, rawLock={rawLock}, {clock.Elapsed.TotalSeconds:N1}s");
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

    /// <summary>
    /// The shape the single-cycle arms cannot see: locks HELD across a unit of work, taken
    /// concurrently by many workers over a small key set — what <c>ConcurrencyPipelineBehavior</c>
    /// actually does.
    /// <para>
    /// This is the arm that decides whether a hand-rolled unprepared lock is viable. Medallion
    /// MULTIPLEXES held locks onto shared connections; a naive raw implementation holds one connection
    /// per held lock. The single-cycle probe acquires and releases immediately, so it is blind to that
    /// difference — and reasoning about pool behaviour instead of measuring it is exactly what produced
    /// a 50% error earlier in this investigation.
    /// </para>
    /// </summary>
    private static async Task RunHeldAsync(
        ServiceProvider efRoot,
        IWarpSemaphoreProvider semaphores,
        bool rawLock,
        string connectionString,
        int iterations,
        int maxCount,
        int concurrency,
        int keys,
        int holdMs)
    {
        var next = -1;

        async Task WorkerAsync()
        {
            while (true)
            {
                var i = Interlocked.Increment(ref next);
                if (i >= iterations)
                {
                    return;
                }

                // Scattered, not index % keys — round-robin hands consecutive workers distinct keys and
                // the arm measures no contention at all (the same trap the load lab hit).
                var key = (int)(Mix((ulong)i) % (ulong)Math.Max(keys, 1));

                await ReadOnceAsync(efRoot);

                if (rawLock)
                {
                    await RawHeldCycleAsync(connectionString, key, holdMs);

                    continue;
                }

                var handle = await semaphores.TryAcquireAsync($"warp:probe:held:{key}", maxCount, TimeSpan.Zero, CancellationToken.None);
                if (handle == null)
                {
                    // A refusal, exactly as the pipeline sees it. Not retried here — the point is the
                    // connection and statement profile, not draining a queue.
                    continue;
                }

                await Task.Delay(holdMs);
                await handle.DisposeAsync();
            }
        }

        await Task.WhenAll(Enumerable.Range(0, Math.Max(concurrency, 1)).Select(_ => WorkerAsync()));
    }

    /// <summary>Raw unprepared acquire, hold, release — one connection held for the duration, no multiplexing.</summary>
    private static async Task RawHeldCycleAsync(string connectionString, int key, int holdMs)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using (var acquire = connection.CreateCommand())
        {
            acquire.CommandText = "SELECT pg_try_advisory_lock(@k)";
            acquire.Parameters.AddWithValue("k", (long)key);

            if (await acquire.ExecuteScalarAsync() is not true)
            {
                return;
            }
        }

        await Task.Delay(holdMs);

        await using var release = connection.CreateCommand();
        release.CommandText = "SELECT pg_advisory_unlock(@k)";
        release.Parameters.AddWithValue("k", (long)key);
        await release.ExecuteScalarAsync();
    }

    private static ulong Mix(ulong value)
    {
        value = (value + 1) * 0x9E3779B97F4A7C15UL;
        value ^= value >> 30;
        value *= 0xBF58476D1CE4E5B9UL;

        return value ^ (value >> 27);
    }

    private static void ReportDelta(PgStatsDelta delta, int iterations, string label, TimeSpan elapsed)
    {
        Console.WriteLine();
        Console.WriteLine($"== lock probe : {label}, {elapsed.TotalSeconds:N1}s");
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
    /// One session-scoped advisory lock taken by hand on EF's OWN connection string, with no
    /// <c>Prepare()</c> anywhere.
    /// <para>
    /// This is the option-3 hypothesis under test. The seven-statement reset is not caused by locking
    /// as such — it is caused by a connector carrying PREPARED statements, which Npgsql cannot clear
    /// with <c>DISCARD ALL</c> without deallocating them. Medallion prepares its lock commands, which
    /// is why sharing a pool with EF contaminated every connector and forced Warp to split the pool
    /// (at a measured cost of ~WorkerCount extra peak connections). If an unprepared lock leaves
    /// <c>DISCARD ALL</c> intact, then one shared pool can have BOTH the cheap reset and no extra
    /// connections — and the split becomes unnecessary rather than a trade.
    /// </para>
    /// <para>
    /// <b>This arm alone does not isolate preparedness.</b> Since the pool split shipped,
    /// <c>UsePostgreSql</c> rewrites <c>Application Name</c> on the bare-connection-string path, so the
    /// default arm is prepared-locks-on-their-OWN-pool and this one is unprepared-locks-on-EF's-pool —
    /// two variables at once. The baseline that holds the pool constant is <c>--data-source</c>, which
    /// is the one path Core does not split: measured 17.00 statements per iteration there, against 5.00
    /// here, both on a shared pool. That is the preparedness effect. The default arm's 11.00 is the
    /// pool split's effect, and comparing 5.00 against it conflates the two.
    /// </para>
    /// <para>
    /// Independently of any comparison, the absence of granular reset statements in this arm's own
    /// trace — <c>DISCARD ALL</c> present, <c>RESET ALL</c> and <c>pg_advisory_unlock_all()</c> absent,
    /// while sharing EF's pool — is the contamination-free signature the hypothesis predicts.
    /// </para>
    /// </summary>
    private static async Task RawLockCycleAsync(string connectionString, int key)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using (var acquire = connection.CreateCommand())
        {
            acquire.CommandText = "SELECT pg_try_advisory_lock(@k)";
            acquire.Parameters.AddWithValue("k", (long)key);

            if (await acquire.ExecuteScalarAsync() is not true)
            {
                return;
            }
        }

        await using var release = connection.CreateCommand();
        release.CommandText = "SELECT pg_advisory_unlock(@k)";
        release.Parameters.AddWithValue("k", (long)key);
        await release.ExecuteScalarAsync();
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
