using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;
using Warp.Core;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Provider.PostgreSql;
using Warp.Worker;

namespace Warp.ServerBenchmarks.Lab;

/// <summary>
/// Multi-server load measurement with each Warp server in its own OS process.
/// <para>
/// The in-process version of this (several <c>IHost</c> instances in one process) cannot separate
/// Warp's distribution cost from the cost of co-hosting: one runtime ends up running every worker
/// and every server-task loop of every "server" against a single thread pool and GC, so a throughput
/// difference says as much about .NET scheduling as about Warp. Real servers do not share a heap.
/// </para>
/// <para>
/// The coordinator owns the container, creates the schema, generates load and measures; it runs no
/// worker of its own. Each server is a child process running <c>node</c> against the same database.
/// </para>
/// </summary>
public static class ClusterLab
{
    public static async Task RunAsync(
        int jobs,
        int workersPerServer,
        int servers,
        int arrivalPerSecond,
        int repeats,
        bool useDispatcher,
        int idleSeconds = 0,
        string? existingConnection = null,
        int handlerMs = 0)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        // An externally supplied database is how this runs without the host's port-forward proxy in
        // the path: the coordinator, its child servers and Postgres sit on one container network.
        PostgreSqlContainer? container = null;
        var connectionString = existingConnection;

        if (connectionString is null)
        {
            container = new PostgreSqlBuilder()
                .WithImage("postgres:latest")
                .WithCommand("-c", "shared_preload_libraries=pg_stat_statements", "-c", "pg_stat_statements.track=top")
                .Build();

            Console.WriteLine("Starting Postgres...");
            await container.StartAsync();

            // The child processes connect over TCP from outside this process, so the mapped host port
            // in the container's own connection string is what they need — no translation required.
            connectionString = container.GetConnectionString();
        }

        await PgStats.TryCreateExtensionAsync(connectionString);
        var withStatements = await PgStats.HasStatStatementsAsync(connectionString);

        using var coordinator = BuildCoordinator(connectionString);

        await using (var scope = coordinator.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<TestContext>().Database.EnsureCreatedAsync();
        }

        Console.WriteLine(
            $"servers={servers} (separate processes)  workers={workersPerServer}/server  jobs={jobs:N0}  "
            + $"arrival={(arrivalPerSecond > 0 ? arrivalPerSecond + "/s" : "burst")}  "
            + $"dispatcher={useDispatcher}  handler={handlerMs}ms");

        var children = StartServers(connectionString, workersPerServer, servers, useDispatcher);

        try
        {
            // Let every server register and settle before any load is offered.
            var startedUtc = DateTime.UtcNow;
            await Task.Delay(TimeSpan.FromSeconds(12));
            await VerifyServersRegisteredAsync(coordinator, servers, startedUtc);

            if (idleSeconds > 0)
            {
                await MeasureIdleAsync(connectionString, withStatements, servers, idleSeconds);

                return;
            }

            var samples = new List<(double Seconds, double DbMs, int Processed)>();
            PgStatsDelta? lastDelta = null;

            for (var run = 1; run <= repeats; run++)
            {
                Console.WriteLine($"-- run {run} of {repeats} " + new string('-', 40));
                await ClearJobsAsync(coordinator);

                var before = await PgStats.CaptureAsync(connectionString, withStatements);
                await using var sampler = new WaitSampler(connectionString, TimeSpan.FromMilliseconds(50));
                var sw = Stopwatch.StartNew();

                var publishSeconds = await PublishAsync(coordinator, jobs, arrivalPerSecond, handlerMs);
                await WaitForDrainAsync(coordinator, jobs, TimeSpan.FromMinutes(30));

                sw.Stop();
                var delta = (await PgStats.CaptureAsync(connectionString, withStatements)).Since(before);

                Console.WriteLine(
                    $"   {sw.Elapsed.TotalSeconds,6:N1}s  {jobs / sw.Elapsed.TotalSeconds,7:N0} jobs/sec  "
                    + $"{delta.TotalExecMs / jobs,6:N3} DB ms/job  {delta.TotalCalls / (double)jobs,6:N1} stmt/job"
                    + $"   [publish {publishSeconds,5:N1}s = {jobs / publishSeconds,6:N0}/s offered]");

                samples.Add((sw.Elapsed.TotalSeconds, delta.TotalExecMs, jobs));
                lastDelta = delta;
                sampler.Report();
            }

            ReportMedians(samples, servers, workersPerServer);

            if (lastDelta is not null)
            {
                ReportTopStatements(lastDelta, jobs);
            }
        }
        finally
        {
            foreach (var child in children)
            {
                TryKill(child);
            }

            if (container is not null)
            {
                await container.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Statement cost of an idle cluster, with each server in its own process. This is where the
    /// lease claim lives — a per-iteration lock is paid by every server every interval whether or not
    /// it has work — and the earlier in-process measurement of it shared one thread pool across all
    /// the "servers", which is not the shape the claim is about.
    /// </summary>
    private static async Task MeasureIdleAsync(
        string connectionString, bool withStatements, int servers, int seconds)
    {
        Console.WriteLine($"   settling, then measuring {seconds}s of idle cluster...");
        await Task.Delay(TimeSpan.FromSeconds(35));

        var before = await PgStats.CaptureAsync(connectionString, withStatements);
        var sw = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        sw.Stop();

        var delta = (await PgStats.CaptureAsync(connectionString, withStatements)).Since(before);
        var perMinute = delta.TotalCalls / sw.Elapsed.TotalSeconds * 60;

        Console.WriteLine();
        Console.WriteLine($"== {servers} idle server process(es) ==");
        Console.WriteLine($"   {perMinute,8:N0} statements/minute total");
        Console.WriteLine($"   {perMinute / servers,8:N0} per server");
        Console.WriteLine();

        foreach (var statement in delta.Statements.OrderByDescending(x => x.Calls).Take(8))
        {
            Console.WriteLine($"   {statement.Calls,7:N0}  {Shorten(statement.Query)}");
        }

        Console.WriteLine();
    }

    private static string Shorten(string query)
    {
        var text = string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return text.Length <= 62 ? text : text[..61] + "…";
    }

    private static List<Process> StartServers(
        string connectionString, int workers, int servers, bool useDispatcher)
    {
        var started = new List<Process>();

        for (var i = 0; i < servers; i++)
        {
            var info = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            // Launched as `dotnet app.dll`, ProcessPath is the dotnet host rather than this program,
            // so the child needs the assembly path back or it starts a `dotnet node` that does not
            // exist. A published apphost points at itself and needs nothing.
            if (IsDotnetHost(Environment.ProcessPath!))
            {
                info.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
            }

            info.ArgumentList.Add("node");
            info.ArgumentList.Add($"--connection={connectionString}");
            info.ArgumentList.Add($"--workers={workers}");

            if (useDispatcher)
            {
                info.ArgumentList.Add("--dispatcher");
            }

            var child = Process.Start(info)!;

            // Drain the child's pipes; a full buffer would block it.
            _ = Task.Run(async () => await child.StandardOutput.ReadToEndAsync());
            _ = Task.Run(async () => await child.StandardError.ReadToEndAsync());

            started.Add(child);
            Console.WriteLine($"   server {i + 1} started (pid {child.Id})");
        }

        return started;
    }

    private static bool IsDotnetHost(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);

        return string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Fails loudly when fewer servers registered than were launched. Without this a child that
    /// crashed on startup would simply look like a slower cluster.
    /// </summary>
    private static async Task VerifyServersRegisteredAsync(IHost coordinator, int expected, DateTime startedUtc)
    {
        await using var scope = coordinator.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TestContext>();

        // Only servers that have beaten since this run began. Children are killed with the process
        // tree and never deregister, and against an external database their rows outlive the run — so
        // counting every row lets a run in which EVERY child failed to start still see N stale rows,
        // pass, and then report "cluster throughput" for a cluster with nothing in it.
        var registered = await context.Set<Warp.Core.Data.Entities.Server>()
            .Where(x => x.LastHeartbeatTime >= startedUtc)
            .CountAsync();

        Console.WriteLine($"   {registered} server row(s) registered and beating");

        if (registered < expected)
        {
            throw new InvalidOperationException(
                $"Only {registered} of {expected} servers are live — a child process failed to start.");
        }
    }

    private static IHost BuildCoordinator(string connectionString)
    {
        // Publisher and measurer only: no AddWarpServer, so the coordinator never competes for jobs
        // and the measured throughput belongs entirely to the child servers.
        return Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Error))
            .ConfigureServices(services =>
            {
                services.AddDbContext<TestContext>(options => options.UseNpgsql(connectionString)
                    .UseSnakeCaseNamingConvention());

                services.AddWarp<TestContext>(config => config.UsePostgreSql());
            })
            .Build();
    }

    /// <summary>
    /// Publishes the load and returns how long that took. The offered rate is a hard ceiling on
    /// measured throughput — a cluster cannot drain faster than the coordinator can enqueue — so a
    /// scaling test has to report it, or a publisher bottleneck reads as server saturation.
    /// </summary>
    private static async Task<double> PublishAsync(IHost coordinator, int count, int arrivalPerSecond, int handlerMs)
    {
        var publishClock = Stopwatch.StartNew();
        var remaining = count;
        var published = 0;
        var clock = Stopwatch.StartNew();
        var batchSize = arrivalPerSecond > 0 ? Math.Max(1, arrivalPerSecond / 10) : 1_000;

        while (remaining > 0)
        {
            var batch = Math.Min(batchSize, remaining);

            await using var scope = coordinator.Services.CreateAsyncScope();
            var publisher = scope.ServiceProvider.GetRequiredService<IPublisher>();
            for (var i = 0; i < batch; i++)
            {
                if (handlerMs > 0)
                {
                    await publisher.Enqueue(new DelayRequest { DelayMs = handlerMs });
                }
                else
                {
                    await publisher.Enqueue(new EmptyRequest());
                }
            }

            await publisher.SaveChangesAsync();
            published += batch;
            remaining -= batch;

            if (arrivalPerSecond > 0)
            {
                var behind = TimeSpan.FromSeconds(published / (double)arrivalPerSecond) - clock.Elapsed;
                if (behind > TimeSpan.Zero)
                {
                    await Task.Delay(behind);
                }
            }
        }

        return publishClock.Elapsed.TotalSeconds;
    }

    private static async Task WaitForDrainAsync(IHost coordinator, int expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var started = DateTime.UtcNow;
        var nextReport = started.AddSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            await using (var scope = coordinator.Services.CreateAsyncScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<TestContext>();
                HarnessQueries.Count();
                var active = await context.Set<Job>()
                    .AnyAsync(x => x.CurrentState == State.Enqueued
                        || x.CurrentState == State.Processing
                        || x.CurrentState == State.Awaiting
                        || x.CurrentState == State.Scheduled);

                if (!active)
                {
                    return;
                }

                if (DateTime.UtcNow >= nextReport)
                {
                    nextReport = DateTime.UtcNow.AddSeconds(20);
                    HarnessQueries.Count();
                    var done = await context.Set<Job>()
                        .CountAsync(x => x.CurrentState == State.Completed);
                    Console.WriteLine($"     {done,8:N0}/{expected:N0}");
                }
            }

            await Task.Delay(1_000);
        }

        throw new TimeoutException("Cluster did not drain within the timeout.");
    }

    private static async Task ClearJobsAsync(IHost coordinator)
    {
        await using var scope = coordinator.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TestContext>();

        await context.Set<Warp.Core.Data.Entities.JobLog>().ExecuteDeleteAsync();
        await context.Set<Warp.Core.Data.Entities.Counter>().ExecuteDeleteAsync();
        await context.Set<Job>().ExecuteDeleteAsync();
    }

    private static void ReportMedians(
        List<(double Seconds, double DbMs, int Processed)> samples, int servers, int workersPerServer)
    {
        var rates = samples.Select(x => x.Processed / x.Seconds).Order().ToArray();
        var costs = samples.Select(x => x.DbMs / x.Processed).Order().ToArray();

        Console.WriteLine();
        Console.WriteLine($"== {servers} server(s) x {workersPerServer} workers, separate processes ==");
        Console.WriteLine($"   median {Median(rates),7:N0} jobs/sec   {Median(costs),6:N3} DB ms/job");
        Console.WriteLine($"   range  {rates[0],7:N0} .. {rates[^1]:N0}");
        Console.WriteLine();
    }

    /// <summary>
    /// Which statements the load actually issued. Throughput alone cannot say whether extra servers
    /// cost anything; a per-job statement count that climbs with server count has to be attributable
    /// to named statements before it means anything.
    /// </summary>
    private static void ReportTopStatements(PgStatsDelta delta, int jobs)
    {
        if (delta.Statements.Count == 0)
        {
            return;
        }

        Console.WriteLine("   top statements of the final run (calls, calls/job):");

        foreach (var statement in delta.Statements.OrderByDescending(x => x.Calls).Take(12))
        {
            Console.WriteLine(
                $"   {statement.Calls,8:N0} {statement.Calls / (double)jobs,6:N2}  {Shorten(statement.Query)}");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// True median. Indexing the midpoint of an even-length sample silently reports the HIGHER of two
    /// runs as the "median", which flatters every two-repeat arm.
    /// </summary>
    private static double Median(double[] ordered) =>
        ordered.Length % 2 == 1
            ? ordered[ordered.Length / 2]
            : (ordered[(ordered.Length / 2) - 1] + ordered[ordered.Length / 2]) / 2;

    /// <summary>
    /// Kills one child server, and never throws.
    /// <para>
    /// This runs in a loop over every child, so an exception here strands the servers after it —
    /// still running, still claiming jobs against the same database. The next run then measures its
    /// own load plus the previous run's, which reads as unexplained contention rather than as
    /// leftover processes. InvalidOperationException (already exited) is only one of the ways Kill
    /// fails: Win32Exception covers a refusal from the OS, which is what a process caught mid-exit
    /// or a permissions problem actually raises, and either one used to escape.
    /// </para>
    /// <para>
    /// A failure is reported rather than swallowed. A leaked server invalidates whatever is measured
    /// next, so the operator has to know to go and look.
    /// </para>
    /// </summary>
    private static void TryKill(Process child)
    {
        try
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"  WARNING: could not kill child server pid {SafePid(child)}: {ex.Message}. "
                + "It may still be claiming jobs — check before trusting the next run.");
        }
        finally
        {
            child.Dispose();
        }
    }

    /// <summary>The pid is unreadable once the Process object is disposed, so reading it can throw too.</summary>
    private static string SafePid(Process child)
    {
        try
        {
            return child.Id.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    /// <summary>
    /// One Warp server, run as a child process. Stays up until the coordinator kills it.
    /// </summary>
    public static async Task RunNodeAsync(string connectionString, int workers, bool useDispatcher)
    {
        using var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Error))
            .ConfigureServices(services =>
            {
                services.AddDbContext<TestContext>(options => options.UseNpgsql(connectionString)
                    .UseSnakeCaseNamingConvention());

                services.AddWarpServer<TestContext>(config =>
                {
                    config.UsePostgreSql();
                    config.WorkerCount = workers;
                    config.UseDispatcher = useDispatcher;
                });
            })
            .Build();

        await host.RunAsync();
    }
}
