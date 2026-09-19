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
using Warp.Core.Helper;
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

    /// <summary>
    /// N jobs spread over <c>--keys</c> mutex groups, so surplus claims are rejected by
    /// <c>ConcurrencyPipelineBehavior</c> and (in Wait mode) requeued. The arm that measures what
    /// enforcing group serialization AFTER the claim costs — the claim UPDATE, the limit lookup, the
    /// advisory-lock round trip and the requeue write are all spent on a job that did not run.
    /// </summary>
    Mutex = 4,

    /// <summary>
    /// As <see cref="Mutex"/> but with <c>--limit</c> slots per key, so the rejection rate is a
    /// fraction of the mutex arm's rather than all-but-one.
    /// </summary>
    Semaphore = 5,
}

/// <summary>
/// Requeue churn read from the durable <c>stats:</c> family (§8.33) after a run. The ratio of
/// <see cref="RequeuedConcurrency"/> to <see cref="Succeeded"/> is the number PA-02 turns on: how many
/// full claim/reject/requeue cycles the system pays per job it actually completes.
/// </summary>
public sealed record ChurnCounts(long Succeeded, long Deleted, long Requeued, long RequeuedConcurrency, long DeletedConcurrency);

/// <summary>The concurrency arm's shape: how many groups, how wide each is, what happens to the surplus, and how long a group stays busy.</summary>
public sealed record ConcurrencyShape(int Keys, int Limit, ConcurrencyMode Mode, int HandlerMs);

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
        int servers = 1,
        int concurrencyKeys = 1,
        int concurrencyLimit = 1,
        ConcurrencyMode concurrencyMode = ConcurrencyMode.Wait,
        int handlerMs = 0)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        PostgreSqlContainer? container = null;
        MsSqlContainer? sqlContainer = null;
        var hosts = new List<IHost>();

        // Teardown belongs in a finally, not on the happy path. A throw anywhere in a scenario used
        // to leak the container AND the running hosts, whose workers keep polling the database after
        // the run that started them has gone — so the NEXT measurement silently includes the previous
        // one's load. A measuring instrument that contaminates its own next reading is worse than one
        // that simply fails.
        try
        {
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
                    : $"  jobs={jobs:N0}  dispatcher={useDispatcher}  prefetch={FormatKnob(prefetchCount)}  completionBatch={FormatKnob(completionBatchSize)}  tune={tune}  arrival={FormatArrival(arrivalPerSecond)}"
                        + DescribePayload(scenario, payloadBytes, types))
                + (scenario == LoadScenario.JobsWithDashboard ? $"  tabs={tabs}" : string.Empty)
                + DescribeConcurrency(scenario, concurrencyKeys, concurrencyLimit, concurrencyMode, handlerMs));

            // Warm up: JIT, connection pool, EF query compilation, server registration. Measuring these
            // would attribute one-time startup cost to steady-state load.
            await WarmUpAsync(host, scenario, new ConcurrencyShape(concurrencyKeys, concurrencyLimit, concurrencyMode, handlerMs));
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
                var harnessBefore = HarnessQueries.Total;
                var sw = Stopwatch.StartNew();

                var processed = await RunScenarioAsync(
                    host, scenario, jobs, tabs, idleWindow, payloadBytes, types, arrivalPerSecond, new ConcurrencyShape(concurrencyKeys, concurrencyLimit, concurrencyMode, handlerMs));

                sw.Stop();
                var after = sqlServer ? PgStats.Empty : await PgStats.CaptureAsync(connectionString, withStatements);
                var delta = after.Since(before);

                // Read AFTER the stats capture so the churn queries stay out of the delta. The cost is
                // that counters still sitting in WarpCounterBuffer when the last job drained flush
                // outside the measured window (§6.2, CounterBufferFlushInterval = 2s) — a couple of
                // seconds of writes on a multi-minute run. The churn figures themselves are complete,
                // because ReadChurnAsync waits for that flush before it reads.
                // Read the harness counter BEFORE the churn queries, which call HarnessQueries.Count()
                // themselves — they are outside the measured window and must not inflate its figure.
                var harnessQueries = HarnessQueries.Total - harnessBefore;
                var churn = IsConcurrency(scenario) ? await ReadChurnAsync(host) : null;

                Report(scenario, delta, sw.Elapsed, processed, withStatements, harnessQueries, churn);
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
        }
        finally
        {
            // Each teardown step is independent: a host that refuses to stop must not strand the
            // container, which is the resource that actually survives the process and costs the
            // next run.
            foreach (var running in hosts)
            {
                try
                {
                    await running.StopAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  teardown: host stop failed: {ex.Message}");
                }

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
        // Match lower-cased against a known set, and refuse anything else. Every lever below is a
        // case-SENSITIVE `is` pattern, so --tune=DropIdx used to apply nothing, print no "tuning
        // applied" line and no error, and report the untuned baseline under the tuned run's heading —
        // the operator reads that as "the lever does not help". A silently-ignored typo is the same
        // failure the customplan lever is commented against: reporting a lever that was never pulled.
        tune = tune.ToLowerInvariant();

        var known = new[]
        {
            "none", "explainclaim", "extstats", "dropidx", "customplan", "analyze", "vacuum", "index", "both",
        };

        if (!known.Contains(tune, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"Unknown --tune value '{tune}'. Known levers: {string.Join(", ", known)}.",
                nameof(tune));
        }

        if (tune is "none")
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

    private static async Task WarmUpAsync(IHost host, LoadScenario scenario, ConcurrencyShape concurrency)
    {
        if (scenario == LoadScenario.Idle)
        {
            return;
        }

        // Warmed in the arm's own shape, not the plain-jobs shape: a concurrency arm's first jobs
        // otherwise pay to JIT the policy pipeline, the semaphore provider and the requeue path inside
        // the measured window. The handler delay is dropped so the warmup still drains quickly.
        await PublishAsync(host, 200, 0, 1, 0, scenario, concurrency with { HandlerMs = 0 });
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
        // Settle past CounterBufferFlushInterval (2s) BEFORE clearing. The worker stages counters in
        // WarpCounterBuffer (§6.2) and the flusher writes them up to two seconds after the job that
        // produced them finished — so clearing the instant a drain completes leaves the tail to land
        // afterwards, and the next run's churn read opens with the previous run's requeues already in it.
        await Task.Delay(TimeSpan.FromSeconds(4));

        await context.Set<JobLog>().ExecuteDeleteAsync();
        await context.Set<Counter>().ExecuteDeleteAsync();

        // Statistic too, or a repeat's churn read would include every previous run's requeues:
        // CounterAggregator folds Counter rows into Statistic, so clearing only the former leaves the
        // folded history behind and the ratio climbs run over run.
        await context.Set<Statistic>().ExecuteDeleteAsync();
        await context.Set<Job>().ExecuteDeleteAsync();
    }

    private static async Task<int> RunScenarioAsync(
        IHost host, LoadScenario scenario, int jobs, int tabs, TimeSpan idleWindow, int payloadBytes, int types, int arrivalPerSecond, ConcurrencyShape concurrency)
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

        await PublishAsync(host, jobs, payloadBytes, types, arrivalPerSecond, scenario, concurrency);
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
    /// <para>
    /// A poller failure must never take the run's RESULT with it. RunScenarioAsync awaits these before
    /// it returns, so an escaping exception propagates out of the measured window and neither Report
    /// nor ReportSpread runs — a 90-minute run that completed successfully would print nothing at all
    /// because one status query hit a transient connection reset under the load the lab itself is
    /// generating. The poller is emulated background noise, not a measurement: its failures are
    /// counted and reported, never thrown.
    /// </para>
    /// </summary>
    private static Task StartDashboardPollers(IHost host, int tabs, CancellationToken ct)
    {
        var failures = 0;

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
                catch (Exception)
                {
                    // Counted, not swallowed silently: a run whose dashboard load mostly failed did not
                    // measure the dashboard load it claims to have applied.
                    Interlocked.Increment(ref failures);
                }
            }
        });

        return Task.WhenAll(pollers)
            .ContinueWith(
                _ =>
                {
                    if (failures > 0)
                    {
                        Console.WriteLine($"  dashboard pollers: {failures} failed status queries");
                    }
                },
                TaskScheduler.Default);
    }

    private static async Task PublishAsync(
        IHost host, int count, int payloadBytes, int types, int arrivalPerSecond, LoadScenario scenario, ConcurrencyShape concurrency)
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
                if (IsConcurrency(scenario))
                {
                    await EnqueueConcurrencyOneAsync(publisher, scenario, concurrency, published + i);
                }
                else
                {
                    await EnqueueOneAsync(publisher, payloadBytes, types, published + i);
                }
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
            HarnessQueries.Count();
            var done = await ctx.Set<Job>()
                .CountAsync(x => x.CurrentState == State.Completed, CancellationToken.None);

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

    /// <summary>
    /// Publishes one concurrency-gated job. The key is applied at PUBLISH (<c>WithMutex</c> /
    /// <c>WithSemaphore</c>) rather than as an attribute so key cardinality is a run parameter instead of
    /// a compile-time set of types — sweeping contention is the whole point of the arm. It is also the
    /// top precedence rung (§8.8), so the shape asked for is the shape that executes.
    /// </summary>
    private static async Task EnqueueConcurrencyOneAsync(IPublisher publisher, LoadScenario scenario, ConcurrencyShape concurrency, int index)
    {
        var key = $"g{KeyFor(index, concurrency.Keys)}";
        var parameters = new JobParameters();

        if (scenario == LoadScenario.Mutex)
        {
            parameters.WithMutex(key, concurrency.Mode);
        }
        else
        {
            parameters.WithSemaphore(key, Math.Max(concurrency.Limit, 1), concurrency.Mode);
        }

        // DelayRequest, not EmptyRequest: a handler that returns immediately holds the group for
        // microseconds, so the rejection rate collapses and the arm measures almost no contention. The
        // delay is what makes a group genuinely busy while its siblings are claimed and turned away.
        await publisher.Enqueue(new DelayRequest { DelayMs = concurrency.HandlerMs }, parameters);
    }

    /// <summary>
    /// Sums the <c>stats:</c> family across BOTH tables: the worker writes into
    /// <c>WarpCounterBuffer</c> (§6.2), <c>CounterBufferFlusher</c> lands that as <c>Counter</c> rows,
    /// and <c>CounterAggregator</c> later folds those into <c>Statistic</c> and deletes them — so at any
    /// instant a key's total is split across the two.
    /// </summary>
    private static async Task<ChurnCounts> ReadChurnAsync(IHost host)
    {
        // Past CounterBufferFlushInterval (2s), so the last jobs to finalize are counted rather than
        // still sitting in the process-local buffer.
        await Task.Delay(TimeSpan.FromSeconds(4));

        await using var scope = host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TestContext>();

        HarnessQueries.Count();
        var counters = await context.Set<Counter>()
            .AsNoTracking()
            .Where(x => x.Key.StartsWith("stats:"))
            .GroupBy(x => x.Key)
            .Select(x =>
                new
                {
                    Key = x.Key,
                    Value = (long)x.Sum(y => y.Value),
                })
            .ToListAsync();

        HarnessQueries.Count();
        var statistics = await context.Set<Statistic>()
            .AsNoTracking()
            .Where(x => x.Key.StartsWith("stats:"))
            .Select(x =>
                new
                {
                    x.Key,
                    x.Value,
                })
            .ToListAsync();

        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in counters.Concat(statistics))
        {
            // Lifetime keys only. The hourly siblings (stats:requeued:2026-09-19-14) carry the same
            // events and would double every figure here.
            if (row.Key.AsSpan(6).IndexOf(':') >= 0)
            {
                continue;
            }

            totals[row.Key] = totals.GetValueOrDefault(row.Key) + row.Value;
        }

        return new ChurnCounts(
            totals.GetValueOrDefault("stats:succeeded"),
            totals.GetValueOrDefault("stats:deleted"),
            totals.GetValueOrDefault("stats:requeued"),
            totals.GetValueOrDefault("stats:requeued-concurrency"),
            totals.GetValueOrDefault("stats:deleted-concurrency"));
    }

    /// <summary>
    /// Scatters a job index across the key set with a splitmix64 finalizer.
    /// <para>
    /// NOT <c>index % keys</c>. Round-robin assigns consecutive keys to consecutive rows, and the claim
    /// hands rows out in <c>ScheduleTime</c> order, so N workers each receive a DIFFERENT key and the
    /// arm measures zero contention however many workers run — an artifact of the publish order, not a
    /// property of Warp. A scattered assignment produces collisions at the rate the key count implies,
    /// and being a pure function of the index it stays reproducible across runs (<c>Random.Shared</c>
    /// would not).
    /// </para>
    /// </summary>
    private static ulong KeyFor(int index, int keys)
    {
        var mixed = ((ulong)index + 1) * 0x9E3779B97F4A7C15UL;
        mixed ^= mixed >> 30;
        mixed *= 0xBF58476D1CE4E5B9UL;
        mixed ^= mixed >> 27;

        return mixed % (ulong)Math.Max(keys, 1);
    }

    private static bool IsConcurrency(LoadScenario scenario) =>
        scenario is LoadScenario.Mutex or LoadScenario.Semaphore;

    /// <summary>Only the arms that HONOUR --payload/--types report them: the concurrency arms always publish a single-type, zero-payload DelayRequest, and echoing values they ignore makes a saved transcript unreproducible from its own header.</summary>
    private static string DescribePayload(LoadScenario scenario, int payloadBytes, int types) =>
        IsConcurrency(scenario) ? string.Empty : $"  payload={payloadBytes}B  types={types}";

    private static string DescribeConcurrency(LoadScenario scenario, int keys, int limit, ConcurrencyMode mode, int handlerMs)
    {
        if (!IsConcurrency(scenario))
        {
            return string.Empty;
        }

        // A Mutex is a Semaphore of one by definition (§8.6) — reporting the --limit the operator
        // happened to pass would print a width that arm does not have.
        var effectiveLimit = scenario == LoadScenario.Mutex ? 1 : limit;

        return $"  keys={keys}  limit={effectiveLimit}  mode={mode}  handlerMs={handlerMs}";
    }

    private static async Task WaitForDrainAsync(IHost host, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
                HarnessQueries.Count();
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
        LoadScenario scenario,
        PgStatsDelta delta,
        TimeSpan elapsed,
        int processed,
        bool withStatements,
        long harnessQueries,
        ChurnCounts? churn)
    {
        var seconds = elapsed.TotalSeconds;
        var db = delta.Database;

        Console.WriteLine();
        Console.WriteLine($"== {scenario} : {elapsed.TotalSeconds:N1}s"
            + (processed > 0 ? $", {processed:N0} jobs, {processed / seconds:N0} jobs/sec" : string.Empty));
        Console.WriteLine();

        Console.WriteLine($"{"metric",-28}{"total",16}{"per sec",14}{(processed > 0 ? "per job" : string.Empty),12}");
        WriteRow("statements", delta.TotalCalls, seconds, processed);

        // Included in the row above, not subtracted from it: the execution time these cost sits inside
        // total_exec_time with no way to attribute it back out, so correcting the count while leaving
        // the time figure alone would be the misleading half-measure. See HarnessQueries.
        if (harnessQueries > 0 && delta.TotalCalls > 0)
        {
            Console.WriteLine(
                $"{"  of which harness",-28}{harnessQueries,16:N0}{string.Empty,14}"
                + $"{harnessQueries / (double)delta.TotalCalls,12:P2}");
        }

        WriteRow("transactions", delta.TotalTransactions, seconds, processed);
        WriteRow("rows inserted", db.TupInserted, seconds, processed);
        WriteRow("rows updated", db.TupUpdated, seconds, processed);
        WriteRow("rows deleted", db.TupDeleted, seconds, processed);
        WriteRow("rows fetched", db.TupFetched, seconds, processed);
        WriteRow("buffer hits", db.BlksHit, seconds, processed);
        WriteRow("buffer reads (disk)", db.BlksRead, seconds, processed);

        if (churn is { } counts)
        {
            WriteChurn(counts);
        }

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

    /// <summary>
    /// The PA-02 headline. <c>rejected cycles / settled job</c> is what a claim-side group filter would
    /// remove: each one is a claim UPDATE, a limit lookup, an advisory-lock round trip on a session
    /// connection and a requeue write, all spent on a job that did not run.
    /// </summary>
    private static void WriteChurn(ChurnCounts churn)
    {
        var settled = churn.Succeeded + churn.Deleted;

        Console.WriteLine();
        Console.WriteLine($"{"concurrency churn",-28}{"total",16}{string.Empty,14}{"per settled",12}");
        WriteChurnRow("settled (succ + del)", settled, settled);
        WriteChurnRow("  succeeded", churn.Succeeded, settled);
        WriteChurnRow("  deleted", churn.Deleted, settled);
        WriteChurnRow("requeues (all reasons)", churn.Requeued, settled);
        WriteChurnRow("  reason=concurrency", churn.RequeuedConcurrency, settled);
        WriteChurnRow("skips (deleted-concurrency)", churn.DeletedConcurrency, settled);

        // Per job that actually RAN, not per settled job. In Skip mode a rejected job IS the Deleted
        // row, so dividing by settled puts the same job in numerator and denominator and pins the
        // ratio near 1.00 however much contention the arm really produced.
        var rejected = churn.RequeuedConcurrency + churn.DeletedConcurrency;
        Console.WriteLine(
            $"{"rejected cycles / succeeded",-28}{rejected,16:N0}{string.Empty,14}"
            + $"{(churn.Succeeded > 0 ? (rejected / (double)churn.Succeeded).ToString("N2", CultureInfo.InvariantCulture) : "n/a"),12}");
    }

    private static void WriteChurnRow(string label, long total, long settled)
    {
        var per = settled > 0 ? (total / (double)settled).ToString("N2", CultureInfo.InvariantCulture) : string.Empty;
        Console.WriteLine($"{label,-28}{total,16:N0}{string.Empty,14}{per,12}");
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
