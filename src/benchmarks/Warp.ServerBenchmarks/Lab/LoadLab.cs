using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Warp.Core;
using Warp.Core.Concurrency;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Services;
using Warp.Provider.PostgreSql;
using Warp.Provider.SqlServer;
using Warp.Worker;

namespace Warp.ServerBenchmarks.Lab;

public enum LoadScenario
{
    /// <summary>Server running, nothing published, no dashboard. The floor.</summary>
    Idle = 1,

    /// <summary>N empty jobs published and drained to completion.</summary>
    Jobs = 2,

    /// <summary>N empty jobs drained while dashboard tabs poll, i.e. someone is watching it work.</summary>
    JobsWithDashboard = 3,
}

public static class LoadLab
{
    public static async Task RunAsync(
        LoadScenario scenario,
        int jobs,
        int workers,
        int tabs,
        TimeSpan idleWindow,
        string? externalConnectionString,
        bool useDispatcher = false,
        int? prefetchCount = null,
        int? completionBatchSize = null,
        int payloadBytes = 0,
        string tune = "none",
        int repeats = 1,
        int types = 1,
        int arrivalPerSecond = 0,
        bool sqlServer = false,
        int servers = 1)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        PostgreSqlContainer? container = null;
        MsSqlContainer? sqlContainer = null;
        string connectionString;

        if (sqlServer)
        {
            // SQL Server arm. The committed changes touch both providers, and pre-aggregation in
            // particular changes write patterns, so measuring only Postgres leaves half the surface
            // unverified. Server-side statistics differ entirely, so only wall-clock and throughput
            // are compared across providers — the pg_stat_* columns are not available here.
            sqlContainer = new MsSqlBuilder().Build();
            await sqlContainer.StartAsync();
            connectionString = sqlContainer.GetConnectionString();
            Console.WriteLine("Started a throwaway SQL Server container (throughput only, no DB-side stats).");
        }
        else if (externalConnectionString is not null)
        {
            connectionString = externalConnectionString;
            Console.WriteLine("Using the supplied PostgreSQL instance.");
        }
        else
        {
            container = new PostgreSqlBuilder()
                .WithImage("postgres:latest")
                // auto_explain captures the plan of any statement slower than the threshold, so a
                // pathological plan can be read rather than guessed at.
                .WithCommand(BuildPostgresArgs(explainPlans: false))
                .Build();

            await container.StartAsync();
            connectionString = container.GetConnectionString();
            Console.WriteLine("Started a throwaway PostgreSQL container.");
        }

        if (!sqlServer)
        {
            await PgStats.TryCreateExtensionAsync(connectionString);
        }

        var withStatements = !sqlServer && await PgStats.HasStatStatementsAsync(connectionString);
        if (!withStatements && !sqlServer)
        {
            Console.WriteLine(
                "WARNING: pg_stat_statements unavailable — statement counts and DB exec time will be blank. " +
                "Tuple and transaction figures below are still exact.");
        }

        // Several servers against one database is the shape Warp is built for, and the one every
        // measurement so far has skipped. Each host registers its own server row and its workers
        // compete for the same queue, so this exercises cross-server claim contention — which a
        // single-process run cannot show at all.
        var hosts = new List<IHost>();
        for (var i = 0; i < servers; i++)
        {
            hosts.Add(BuildHost(connectionString, workers, useDispatcher, prefetchCount, completionBatchSize, sqlServer));
        }

        var host = hosts[0];

        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<TestContext>().Database.EnsureCreatedAsync();
        }

        if (!sqlServer)
        {
            await ApplyTuningAsync(connectionString, tune);
        }

        foreach (var started in hosts)
        {
            await started.StartAsync();
        }

        Console.WriteLine($"scenario={scenario}  servers={servers}  workers={workers}/server"
            + (scenario == LoadScenario.Idle
                ? string.Empty
                : $"  jobs={jobs:N0}  dispatcher={useDispatcher}  prefetch={FormatKnob(prefetchCount)}  completionBatch={FormatKnob(completionBatchSize)}  payload={payloadBytes}B  tune={tune}  types={types}  arrival={FormatArrival(arrivalPerSecond)}")
            + (scenario == LoadScenario.JobsWithDashboard ? $"  tabs={tabs}" : string.Empty));

        // Warm up: JIT, connection pool, EF query compilation, server registration. Measuring these
        // would attribute one-time startup cost to steady-state load.
        await WarmUpAsync(host, scenario);
        Console.WriteLine("Warmed up. Settling...");
        await Task.Delay(TimeSpan.FromSeconds(scenario == LoadScenario.Idle ? 45 : 5));

        var samples = new List<(double Seconds, double DbMs, long Statements, int Processed)>();

        for (var run = 1; run <= repeats; run++)
        {
            if (repeats > 1)
            {
                Console.WriteLine($"-- run {run} of {repeats} " + new string('-', 40));
                await ResetJobTablesAsync(host);
            }

            var before = sqlServer ? PgStats.Empty : await PgStats.CaptureAsync(connectionString, withStatements);
            var sw = Stopwatch.StartNew();

            var processed = await RunScenarioAsync(host, scenario, jobs, tabs, idleWindow, payloadBytes, types, arrivalPerSecond);

            sw.Stop();
            var after = sqlServer ? PgStats.Empty : await PgStats.CaptureAsync(connectionString, withStatements);
            var delta = after.Since(before);

            Report(scenario, delta, sw.Elapsed, processed, withStatements);
            samples.Add((sw.Elapsed.TotalSeconds, delta.TotalExecMs, delta.TotalCalls, processed));
        }

        if (repeats > 1)
        {
            ReportSpread(samples);
        }

        if (!sqlServer)
        {
            await ReportIndexUsageAsync(connectionString);
        }

        if (!sqlServer && string.Equals(tune, "explainclaim", StringComparison.OrdinalIgnoreCase))
        {
            await ExplainClaimShapesAsync(connectionString);
        }

        if (container is not null)
        {
            var (stdout, stderr) = await container.GetLogsAsync();
            var planLines = (stdout + stderr)
                .Split((char)10)
                .Where(x => x.Contains("Index Scan", StringComparison.Ordinal)
                    || x.Contains("Seq Scan", StringComparison.Ordinal)
                    || x.Contains("duration:", StringComparison.Ordinal)
                    || x.Contains("Sort", StringComparison.Ordinal)
                    || x.Contains("LockRows", StringComparison.Ordinal))
                .Take(40)
                .ToList();

            if (planLines.Count > 0)
            {
                Console.WriteLine("-- slow-statement plans (auto_explain) " + new string('-', 20));
                foreach (var line in planLines)
                {
                    Console.WriteLine(line.TrimEnd());
                }

                Console.WriteLine();
            }
        }

        foreach (var running in hosts)
        {
            await running.StopAsync();
            running.Dispose();
        }

        if (container is not null)
        {
            await container.DisposeAsync();
        }

        if (sqlContainer is not null)
        {
            await sqlContainer.DisposeAsync();
        }
    }

    /// <summary>
    /// Incompressible payload. A repeated character compresses to almost nothing, so TOAST and WAL
    /// costs vanish and a payload-width benchmark measures nothing — which is exactly the trap the
    /// first version of this fell into.
    /// </summary>
    private static string RandomPayload(int bytes)
    {
        const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var chars = new char[bytes];
        for (var i = 0; i < bytes; i++)
        {
            chars[i] = Alphabet[Random.Shared.Next(Alphabet.Length)];
        }

        return new string(chars);
    }

    /// <summary>
    /// Applies candidate claim-path tuning directly in SQL, so each lever can be measured before
    /// anyone implements it in Core.
    /// <para>
    /// <c>vacuum</c> tightens autovacuum on the job table. The stock 20% scale factor means vacuum
    /// waits for ~100k dead tuples on a 500k-row table, and every job state transition leaves dead
    /// index entries in the range the claim scans (CurrentState is part of the claim index, so no
    /// update is ever HOT).
    /// </para>
    /// <para>
    /// <c>index</c> adds a partial index matching the claim predicate exactly. The provider
    /// interpolates Kind and CurrentState as literals rather than parameters, so the planner can
    /// prove the partial predicate holds and use it — with parameters it could not.
    /// </para>
    /// </summary>
    private static async Task ApplyTuningAsync(string connectionString, string tune)
    {
        if (string.Equals(tune, "none", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var statements = new List<string>();

        if (tune is "explainclaim")
        {
            // Compare the planner's estimate for the claim predicate written two ways. The only
            // difference is queue = ANY(array) versus queue = scalar; if the array form is what
            // produces the rows=1 estimate, it shows up here as a row-count difference.
            statements.Add("ANALYZE warp.job;");
        }

        if (tune is "extstats" or "both")
        {
            // Attack the misestimate itself rather than the index it escapes to. kind, current_state
            // and queue are heavily correlated (every claimable row is kind=Job, state=Enqueued), and
            // per-column statistics multiplied together are what produce the rows=1 estimate.
            statements.Add(
                "CREATE STATISTICS job_claim_stats (ndistinct, dependencies, mcv) "
                + "ON kind, current_state, queue FROM warp.job;");
            statements.Add("ANALYZE warp.job;");
        }

        if (tune is "dropidx" or "both")
        {
            // The pathological plan scans this index and then sorts. Removing it leaves the planner
            // no alternative to the index that already supplies ORDER BY queue, schedule_time, which
            // lets LIMIT stop at the first unlocked row instead of sorting the whole backlog.
            statements.Add("DROP INDEX warp.ix_job_kind_current_state_create_time;");
        }

        if (tune is "customplan" or "both")
        {
            // Postgres plans a prepared statement with the real parameter values for its first five
            // executions, then switches to a generic plan if that looks no worse. A generic plan
            // cannot use the selectivity of queue = ANY($1), which is the suspected reason a fraction
            // of claims choose an index satisfying neither the filter nor the ORDER BY.
            // current_database(), not a hard-coded name: against --connection the target is usually not
            // called "postgres", and ALTER DATABASE postgres would succeed against a database nothing
            // in the run is connected to — reporting a lever that was never pulled.
            statements.Add(
                "DO $$ BEGIN EXECUTE format('ALTER DATABASE %I SET plan_cache_mode = ''force_custom_plan''', current_database()); END $$;");
        }

        if (tune is "analyze" or "both")
        {
            // The claim's plan, not its vacuum pressure, is the suspect. The job table's row
            // distribution swings violently (a full backlog drains to zero), so the planner's
            // statistics go stale and it mis-estimates the claim predicate — which is how an index
            // that satisfies neither the filter nor the ORDER BY gets chosen over the exact match.
            statements.Add(
                "ALTER TABLE warp.job SET (autovacuum_analyze_scale_factor = 0.02, "
                + "autovacuum_analyze_threshold = 500);");
        }

        if (tune is "vacuum" or "both")
        {
            statements.Add(
                "ALTER TABLE warp.job SET (autovacuum_vacuum_scale_factor = 0.02, "
                + "autovacuum_vacuum_threshold = 500, autovacuum_vacuum_cost_limit = 2000, "
                + "autovacuum_vacuum_cost_delay = 0);");
        }

        if (tune is "index" or "both")
        {
            statements.Add(
                "CREATE INDEX ix_job_claim_partial ON warp.job (queue, schedule_time) "
                + "WHERE kind = 1 AND current_state = 1;");
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var sql in statements)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
            Console.WriteLine($"  tuning applied: {sql.Split('(')[0].Trim()}");
        }
    }

    /// <summary>
    /// Per-index scan counts for the job table. Without this, a tuning experiment that adds an index
    /// cannot distinguish "the index did not help" from "the planner never used it".
    /// </summary>
    private static async Task ReportIndexUsageAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT indexrelname, idx_scan, idx_tup_read
            FROM pg_stat_user_indexes
            WHERE relname = 'job'
            ORDER BY idx_scan DESC;
            """,
            connection);

        Console.WriteLine($"{"index on job",-44}{"scans",12}{"tuples read",14}");
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            Console.WriteLine($"{reader.GetString(0),-44}{reader.GetInt64(1),12:N0}{reader.GetInt64(2),14:N0}");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Prints the planner's estimate for the claim predicate in both spellings, against whatever
    /// backlog is in the table. The claim is the one place the estimate matters, and the estimate is
    /// what selects the Sort plan.
    /// </summary>
    private static async Task ExplainClaimShapesAsync(string connectionString)
    {
        var shapes = new (string Label, string Sql)[]
        {
            ("queue = ANY(array)",
                """
                EXPLAIN (FORMAT TEXT)
                SELECT id FROM warp.job
                WHERE kind = 1 AND current_state = 1 AND queue = ANY(ARRAY['default'])
                ORDER BY queue, schedule_time LIMIT 1 FOR UPDATE SKIP LOCKED
                """),
            ("queue = scalar",
                """
                EXPLAIN (FORMAT TEXT)
                SELECT id FROM warp.job
                WHERE kind = 1 AND current_state = 1 AND queue = 'default'
                ORDER BY queue, schedule_time LIMIT 1 FOR UPDATE SKIP LOCKED
                """),
        };

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        foreach (var (label, sql) in shapes)
        {
            Console.WriteLine($"-- claim plan: {label} " + new string('-', 24));
            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                Console.WriteLine("   " + reader.GetString(0));
            }

            Console.WriteLine();
        }
    }

    /// <summary>
    /// Reports median and spread across runs instead of a single number.
    /// <para>
    /// Measured run-to-run variance on this harness is around ±5%, which is wide enough to invent
    /// results: a single run of one configuration in this session showed a 21% "win" that repeats
    /// revealed as an outlier. A claim whose spread overlaps the arm it is compared against is not a
    /// result, and printing the spread beside the median is what makes that visible.
    /// </para>
    /// </summary>
    private static void ReportSpread(List<(double Seconds, double DbMs, long Statements, int Processed)> samples)
    {
        Console.WriteLine("== across runs " + new string('=', 46));
        Console.WriteLine($"{"metric",-22}{"median",14}{"min",14}{"max",14}{"spread",10}");

        WriteSpread("wall seconds", [.. samples.Select(x => x.Seconds)]);
        WriteSpread("jobs/sec", [.. samples.Select(x => x.Processed > 0 ? x.Processed / x.Seconds : 0)]);
        WriteSpread("DB ms/job", [.. samples.Select(x => x.Processed > 0 ? x.DbMs / x.Processed : x.DbMs)]);
        WriteSpread("statements/job", [.. samples.Select(x => x.Processed > 0 ? x.Statements / (double)x.Processed : x.Statements)]);

        Console.WriteLine();
        Console.WriteLine($"n = {samples.Count}. Compare MEDIANS, and treat any difference smaller than");
        Console.WriteLine("the spread of either arm as noise rather than a result.");
        Console.WriteLine();
    }

    private static void WriteSpread(string label, double[] values)
    {
        Array.Sort(values);

        // True median. Indexing the midpoint of an even-length sample reports the HIGHER of two runs,
        // which flatters jobs/sec and penalises DB ms/job — opposite directions in the same table,
        // under a footer telling the reader to compare medians.
        var median = values.Length % 2 == 1
            ? values[values.Length / 2]
            : (values[(values.Length / 2) - 1] + values[values.Length / 2]) / 2;
        var min = values[0];
        var max = values[^1];
        var spread = Math.Abs(median) < double.Epsilon ? 0 : (max - min) / median;

        Console.WriteLine($"{label,-22}{median,14:N3}{min,14:N3}{max,14:N3}{spread,10:P1}");
    }

    /// <summary>Clears job tables between repeats so each run starts from the same state.</summary>
    private static async Task ResetJobTablesAsync(IHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await ClearJobTablesAsync(scope.ServiceProvider.GetRequiredService<TestContext>());
    }

    /// <summary>
    /// auto_explain is DELIBERATELY off. <c>log_analyze=on</c> cannot be limited by
    /// <c>log_min_duration</c> — Postgres does not know a statement's duration until it has run, so it
    /// instruments every statement whether or not that one is ever logged. The instrumentation lands in
    /// <c>total_exec_time</c>, which is the quantity this lab reports as DB ms/job, and it lands
    /// unevenly (per plan node), so a plan-heavy statement is inflated more than a simple one and the
    /// share breakdown shifts too. Turn it on to read plans, never to take a timing.
    /// </summary>
    private static string[] BuildPostgresArgs(bool explainPlans) =>
        explainPlans
            ?
            [
                "-c", "shared_preload_libraries=pg_stat_statements,auto_explain",
                "-c", "pg_stat_statements.track=top",
                "-c", "auto_explain.log_min_duration=10",
                "-c", "auto_explain.log_analyze=on",
                "-c", "auto_explain.log_buffers=on",
                "-c", "auto_explain.log_nested_statements=on",
            ]
            :
            [
                "-c", "shared_preload_libraries=pg_stat_statements",
                "-c", "pg_stat_statements.track=top",
            ];

    private static string FormatArrival(int perSecond) =>
        perSecond > 0 ? $"{perSecond}/s" : "burst";

    private static string FormatKnob(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "default";

    private static IHost BuildHost(
        string connectionString, int workers, bool useDispatcher, int? prefetchCount, int? completionBatchSize, bool sqlServer)
    {
        return Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Error))
            .ConfigureServices(services =>
            {
                services.AddDbContext<TestContext>(options =>
                {
                    if (sqlServer)
                    {
                        options.UseSqlServer(connectionString);
                    }
                    else
                    {
                        options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
                    }
                });

                services.AddWarpServer<TestContext>(config =>
                {
                    if (sqlServer)
                    {
                        config.UseSqlServer();
                    }
                    else
                    {
                        config.UsePostgreSql();
                    }

                    config.AddConcurrency();
                    config.WorkerCount = workers;
                    config.UseDispatcher = useDispatcher;

                    if (prefetchCount is { } prefetch)
                    {
                        config.PrefetchCount = prefetch;
                    }

                    if (completionBatchSize is { } batch)
                    {
                        config.CompletionBatchSize = batch;
                    }
                });
            })
            .Build();
    }

    private static async Task WarmUpAsync(IHost host, LoadScenario scenario)
    {
        if (scenario == LoadScenario.Idle)
        {
            return;
        }

        await PublishAsync(host, 200, 0, 1, 0);
        await WaitForDrainAsync(host, TimeSpan.FromMinutes(2));

        await using var scope = host.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
        await ClearJobTablesAsync(ctx);
    }

    /// <summary>
    /// Clears job tables through EF rather than raw SQL. The physical names differ by provider — the
    /// Postgres arm applies a snake_case convention and the SQL Server arm does not — so hand-written
    /// table names work on one and fail on the other.
    /// </summary>
    private static async Task ClearJobTablesAsync(TestContext context)
    {
        await context.Set<JobLog>().ExecuteDeleteAsync();
        await context.Set<Counter>().ExecuteDeleteAsync();
        await context.Set<Job>().ExecuteDeleteAsync();
    }

    private static async Task<int> RunScenarioAsync(
        IHost host, LoadScenario scenario, int jobs, int tabs, TimeSpan idleWindow, int payloadBytes, int types, int arrivalPerSecond)
    {
        if (scenario == LoadScenario.Idle)
        {
            await Task.Delay(idleWindow);

            return 0;
        }

        using var stopPolling = new CancellationTokenSource();
        var pollers = scenario == LoadScenario.JobsWithDashboard
            ? StartDashboardPollers(host, tabs, stopPolling.Token)
            : Task.CompletedTask;

        // Progress is reported from the START of the measured window, spanning publish AND drain.
        // Reporting only from drain-start silently folded every job the workers finished while
        // publishing into the first window, which made the opening rate look far higher than the
        // system ever actually sustained.
        using var stopProgress = new CancellationTokenSource();
        var progress = ReportProgressAsync(host, jobs, stopProgress.Token);

        await PublishAsync(host, jobs, payloadBytes, types, arrivalPerSecond);
        Console.WriteLine($"  publish complete");
        await WaitForDrainAsync(host, TimeSpan.FromMinutes(90));

        await stopProgress.CancelAsync();
        await progress;

        await stopPolling.CancelAsync();
        await pollers;

        return jobs;
    }

    /// <summary>
    /// Emulates tabs on the dashboard polling status.
    /// </summary>
    private static Task StartDashboardPollers(IHost host, int tabs, CancellationToken ct)
    {
        var pollers = Enumerable.Range(0, tabs).Select(async _ =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await using var scope = host.Services.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<IDashboardStatsService>().GetWarpStatus();
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });

        return Task.WhenAll(pollers);
    }

    private static async Task PublishAsync(IHost host, int count, int payloadBytes, int types, int arrivalPerSecond)
    {
        var remaining = count;
        var published = 0;
        var clock = Stopwatch.StartNew();

        // Smaller batches when pacing, so arrival is smooth rather than a burst per second.
        var batchSize = arrivalPerSecond > 0 ? Math.Max(1, arrivalPerSecond / 10) : 1_000;

        while (remaining > 0)
        {
            var batch = Math.Min(batchSize, remaining);

            await using var scope = host.Services.CreateAsyncScope();
            var publisher = scope.ServiceProvider.GetRequiredService<IPublisher>();
            for (var i = 0; i < batch; i++)
            {
                await EnqueueOneAsync(publisher, payloadBytes, types, published + i);
            }

            await publisher.SaveChangesAsync();
            published += batch;
            remaining -= batch;

            // Steady arrival instead of dump-then-drain. The dump shape is the worst case for
            // planner statistics (the table goes from empty to a full backlog in seconds) and for
            // counter-key locality, so it is not the shape to draw steady-state conclusions from.
            if (arrivalPerSecond > 0)
            {
                var due = TimeSpan.FromSeconds(published / (double)arrivalPerSecond);
                var behind = due - clock.Elapsed;
                if (behind > TimeSpan.Zero)
                {
                    await Task.Delay(behind);
                }
            }
        }
    }

    /// <summary>
    /// Samples completed-job count on a fixed cadence for the whole measured window, so the
    /// throughput curve covers publish and drain on one timeline.
    /// </summary>
    private static async Task ReportProgressAsync(IHost host, int expected, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var previous = 0;
        var previousAt = started;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await using var scope = host.Services.CreateAsyncScope();
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            var done = await ctx.Set<Job>().CountAsync(x => x.CurrentState == State.Completed, CancellationToken.None);

            var now = DateTime.UtcNow;
            var windowRate = (done - previous) / Math.Max((now - previousAt).TotalSeconds, 0.001);
            Console.WriteLine(
                $"  [{(now - started).TotalSeconds,6:N0}s] {done,9:N0}/{expected:N0}  window {windowRate,8:N0} jobs/sec");

            previous = done;
            previousAt = now;
        }
    }

    /// <summary>
    /// Waits for every job to reach a terminal state.
    /// <para>
    /// Uses EXISTS on a 1s cadence, not COUNT(*) every 250ms. The original poller was itself one of
    /// the most expensive statements in the trace - 712ms per call against a large job table, about
    /// 3% of all database time - which is measurement perturbing the thing being measured. EXISTS
    /// short-circuits on the first matching row instead of scanning every one.
    /// </para>
    /// </summary>
    /// <summary>
    /// Publishes one job, spreading across <paramref name="types"/> distinct request types so the
    /// counter-key set reflects a realistic workload rather than collapsing to a single type's keys.
    /// </summary>
    private static async Task EnqueueOneAsync(IPublisher publisher, int payloadBytes, int types, int index)
    {
        if (types <= 1 && payloadBytes == 0)
        {
            await publisher.Enqueue(new EmptyRequest());

            return;
        }

        var data = payloadBytes > 0 ? RandomPayload(payloadBytes) : string.Empty;

        switch (index % Math.Clamp(types, 1, 8))
        {
            case 0: await publisher.Enqueue(new PayloadRequest1 { Data = data }); return;
            case 1: await publisher.Enqueue(new PayloadRequest2 { Data = data }); return;
            case 2: await publisher.Enqueue(new PayloadRequest3 { Data = data }); return;
            case 3: await publisher.Enqueue(new PayloadRequest4 { Data = data }); return;
            case 4: await publisher.Enqueue(new PayloadRequest5 { Data = data }); return;
            case 5: await publisher.Enqueue(new PayloadRequest6 { Data = data }); return;
            case 6: await publisher.Enqueue(new PayloadRequest7 { Data = data }); return;
            default: await publisher.Enqueue(new PayloadRequest8 { Data = data }); return;
        }
    }

    private static async Task WaitForDrainAsync(IHost host, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
                var active = await ctx.Set<Job>()
                    .AnyAsync(x => x.CurrentState == State.Enqueued
                        || x.CurrentState == State.Processing
                        || x.CurrentState == State.Awaiting
                        || x.CurrentState == State.Scheduled);

                if (!active)
                {
                    return;
                }
            }

            await Task.Delay(1_000);
        }

        throw new TimeoutException("Jobs did not drain within the timeout.");
    }

    private static void Report(
        LoadScenario scenario, PgStatsDelta delta, TimeSpan elapsed, int processed, bool withStatements)
    {
        var seconds = elapsed.TotalSeconds;
        var db = delta.Database;

        Console.WriteLine();
        Console.WriteLine($"== {scenario} : {elapsed.TotalSeconds:N1}s"
            + (processed > 0 ? $", {processed:N0} jobs, {processed / seconds:N0} jobs/sec" : string.Empty));
        Console.WriteLine();

        Console.WriteLine($"{"metric",-28}{"total",16}{"per sec",14}{(processed > 0 ? "per job" : string.Empty),12}");
        WriteRow("statements", delta.TotalCalls, seconds, processed);
        WriteRow("transactions", delta.TotalTransactions, seconds, processed);
        WriteRow("rows inserted", db.TupInserted, seconds, processed);
        WriteRow("rows updated", db.TupUpdated, seconds, processed);
        WriteRow("rows deleted", db.TupDeleted, seconds, processed);
        WriteRow("rows fetched", db.TupFetched, seconds, processed);
        WriteRow("buffer hits", db.BlksHit, seconds, processed);
        WriteRow("buffer reads (disk)", db.BlksRead, seconds, processed);

        if (withStatements)
        {
            Console.WriteLine();
            Console.WriteLine($"DB exec time: {delta.TotalExecMs:N0} ms over {seconds:N1}s "
                + $"= {delta.TotalExecMs / seconds / 10:N2}% of one core"
                + (processed > 0 ? $", {delta.TotalExecMs / processed:N3} ms/job" : string.Empty));

            Console.WriteLine();
            Console.WriteLine($"{"top statements by DB time",-46}{"calls",10}{"exec ms",11}{"share",7}{"blocks",14}");
            foreach (var statement in delta.Statements.Take(12))
            {
                Console.WriteLine(
                    $"{Shorten(statement.Query),-46}{statement.Calls,10:N0}{statement.TotalExecMs,11:N1}"
                    + $"{statement.TotalExecMs / Math.Max(delta.TotalExecMs, 0.001),7:P1}{statement.Blocks,14:N0}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{"table",-28}{"ins",12}{"upd",12}{"del",12}{"seq",10}{"idx",12}");
        foreach (var (name, stat) in delta.Tables.OrderByDescending(x => x.Value.Inserted + x.Value.Updated + x.Value.Deleted).Take(10))
        {
            Console.WriteLine(
                $"{name,-28}{stat.Inserted,12:N0}{stat.Updated,12:N0}{stat.Deleted,12:N0}{stat.SeqScan,10:N0}{stat.IdxScan,12:N0}");
        }

        Console.WriteLine();
    }

    private static void WriteRow(string label, long total, double seconds, int processed)
    {
        var perJob = processed > 0 ? (total / (double)processed).ToString("N2", CultureInfo.InvariantCulture) : string.Empty;
        Console.WriteLine($"{label,-28}{total,16:N0}{total / seconds,14:N1}{perJob,12}");
    }

    private static string Shorten(string query)
    {
        var text = string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return text.Length <= 44 ? text : text[..43] + "…";
    }
}
