using Microsoft.Data.SqlClient;
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
using Warp.Provider.PostgreSql;
using Warp.Provider.SqlServer;
using Warp.Worker;

namespace Warp.ServerBenchmarks.Infrastructure;

/// <summary>
/// Boots a Testcontainer PostgreSQL + full Warp server for benchmarking.
/// Shared across benchmark iterations via [GlobalSetup]/[GlobalCleanup].
/// </summary>
public class PostgresServerFixture : IAsyncDisposable
{
    /// <summary>
    /// Points the fixture at an EXISTING PostgreSQL instead of starting its own container.
    /// <para>
    /// This is what lets <see cref="PgStatStatementsDiagnoser"/> work at all. BenchmarkDotNet runs the
    /// benchmark in a child process while diagnosers run in the host, so a container created in the
    /// child is invisible to the diagnoser — it has no way to learn a randomly-assigned port. Naming
    /// one database in the environment gives both processes the same target. It is also how the lab
    /// already works (`--connection`), and how CI wants it: one server, started once, with
    /// `shared_preload_libraries=pg_stat_statements` set.
    /// </para>
    /// </summary>
    public const string ConnectionStringVariable = "WARP_BENCH_POSTGRES";

    /// <summary>The SQL Server equivalent, so a run can cover both providers on one machine.</summary>
    public const string SqlServerConnectionStringVariable = "WARP_BENCH_SQLSERVER";

    /// <summary>
    /// Records, inside PostgreSQL, every statement that moves job rows into Processing: how many rows,
    /// which ones, from which backend, and the statement's own text. Diagnostic only — it adds a
    /// trigger to the job table — for tracing jobs that were claimed and never run.
    /// </summary>
    public const string ClaimAuditVariable = "WARP_BENCH_AUDIT_CLAIMS";

    private readonly PostgreSqlContainer? _container = Environment.GetEnvironmentVariable(ConnectionStringVariable) is null
        ? new PostgreSqlBuilder()
            .WithImage("postgres:latest")

            // Loaded at startup so statement counts can be read. shared_preload_libraries cannot be
            // switched on later; it needs a restart.
            .WithCommand(
                "-c",
                "shared_preload_libraries=pg_stat_statements",
                "-c",
                "pg_stat_statements.track=top")
            .Build()
        : null;

    private MsSqlContainer? _sqlContainer;
    private IHost? _host;
    private string _connectionString = null!;
    private BenchmarkProvider _provider = BenchmarkProvider.PostgreSql;

    public bool IsInitialized => _host != null;

    public IHost Host => _host!;

    /// <summary>
    /// Boots the full server: container, schema, host with workers + background tasks.
    /// </summary>
    public async Task InitializeAsync(
        int workerCount = 5,
        bool useDispatcher = false,
        int completionBatchSize = 50,
        TimeSpan? completionFlushInterval = null,
        bool addConcurrency = false,
        BenchmarkProvider provider = BenchmarkProvider.PostgreSql,
        Action<WarpServerBuilder<TestContext>>? configure = null)
    {
        _provider = provider;
        ExceptionTally.StartIfRequested();
        _connectionString = await ResolveConnectionStringAsync();

        // Boot full Warp server
        _host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging
                .SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning)

                // The worker loop swallows a DbUpdateConcurrencyException at Debug and claims again.
                // Raised here so that path is visible if it is what orphans claimed jobs.
                .AddFilter("Warp.Worker.WarpWorker", Microsoft.Extensions.Logging.LogLevel.Debug)

                // Filters match by prefix, so the rule above also caught WarpWorkerService, which logs
                // every job it runs. The longer prefix wins.
                .AddFilter("Warp.Worker.WarpWorkerService", Microsoft.Extensions.Logging.LogLevel.Warning))
            .ConfigureServices(services =>
            {
                services.AddDbContext<TestContext>(options => ConfigureProvider(options));

                services.AddWarpServer<TestContext>(config =>
                {
                    // Registers IWarpLockProvider and IWarpSqlQueries. Without it ServerTaskHost
                    // cannot be activated, every benchmark using this fixture reports NA, and the
                    // failure is quiet - BenchmarkDotNet prints a table of NA rather than failing.
                    if (_provider == BenchmarkProvider.SqlServer)
                    {
                        config.UseSqlServer();
                    }
                    else
                    {
                        config.UsePostgreSql();
                    }

                    // Opt-in addon (rule 8.6): without this, WithMutex stamps metadata that no
                    // behaviour reads, so a concurrency benchmark measures the plain baseline and
                    // reports it as the addon's cost. That is exactly what it did before this
                    // parameter existed - 14.05 statements per job against the lab's 37.0.
                    if (addConcurrency)
                    {
                        config.AddConcurrency();
                    }

                    config.WorkerCount = workerCount;
                    config.Queues = ["default"];

                    // Intervals are left at their PRODUCTION defaults on purpose. They used to be
                    // driven 20-100x faster here (polling 100ms against 10s, orchestration 100ms
                    // against 10s, heartbeat 200ms against 5s) for quick turnaround, and the cost was
                    // that every background tick landed in the statement count: this fixture reported
                    // 42 statements per job where the lab, on defaults, measured 13.6 for the same
                    // shape of work. A/B comparisons survived that, but the absolute number was an
                    // artefact of the harness and not comparable to anything published.
                    //
                    // Defaults are affordable because a same-process enqueue signals the workers
                    // directly (SignalJobEnqueued, rule 6.3), so a drain does not wait out
                    // PollingInterval.
                    config.UseDispatcher = useDispatcher;
                    config.CompletionBatchSize = completionBatchSize;
                    config.CompletionFlushInterval = completionFlushInterval ?? TimeSpan.FromMilliseconds(100);

                    // Last, so a scenario can add an addon or override any default above.
                    configure?.Invoke(config);
                });
            })
            .Build();

        // Use DI-resolved context for schema creation (includes Warp entity configurations)
        await using var scope = _host.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
        await ctx.Database.EnsureCreatedAsync();

        if (_provider == BenchmarkProvider.PostgreSql && Environment.GetEnvironmentVariable(ClaimAuditVariable) is not null)
        {
            await InstallClaimAuditAsync();
        }

        await _host.StartAsync();
    }

    /// <summary>
    /// Boots DI container only (no hosted services). For component isolation benchmarks.
    /// </summary>
    public async Task InitializeWithoutHostedServicesAsync()
    {
        _connectionString = await ResolveConnectionStringAsync();

        // Build a host but don't start it — only use its DI container
        _host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .ConfigureServices(services =>
            {
                services.AddDbContext<TestContext>(options => ConfigureProvider(options));

                services.AddWarpServer<TestContext>(config =>
                {
                    // Registered here too. Nothing resolves IWarpLockProvider without hosted services,
                    // so its absence was latent rather than fatal on this path - but latent is how the
                    // same omission sat unnoticed in InitializeAsync while every server benchmark
                    // reported NA.
                    if (_provider == BenchmarkProvider.SqlServer)
                    {
                        config.UseSqlServer();
                    }
                    else
                    {
                        config.UsePostgreSql();
                    }

                    config.WorkerCount = 1;
                    config.Queues = ["default"];
                    config.PollingInterval = TimeSpan.FromMilliseconds(100);
                    config.UseDispatcher = false;
                });
            })
            .Build();

        // Use DI-resolved context for schema creation (includes Warp entity configurations)
        await using var scope = _host.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
        await ctx.Database.EnsureCreatedAsync();

        // Do NOT call _host.StartAsync() — we only want the DI container
    }

    public IPublisher CreatePublisher()
    {
        var scope = Host.Services.CreateScope();

        return scope.ServiceProvider.GetRequiredService<IPublisher>();
    }

    public IBatchPublisher CreateBatchPublisher()
    {
        var scope = Host.Services.CreateScope();

        return scope.ServiceProvider.GetRequiredService<IBatchPublisher>();
    }

    /// <summary>
    /// Polls until all jobs reach a terminal state.
    /// </summary>
    public async Task WaitForCompletion(TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(5));
        while (DateTime.UtcNow < deadline)
        {
            await using var scope = Host.Services.CreateAsyncScope();
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            var activeJobs = await ctx.Set<Job>()
                .CountAsync(x =>
                    x.CurrentState == State.Enqueued ||
                    x.CurrentState == State.Processing ||
                    x.CurrentState == State.Awaiting);

            if (activeJobs == 0)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Not all jobs completed within timeout{Environment.NewLine}{await DescribeStuckJobsAsync()}");
    }

    /// <summary>
    /// What the database looked like when a drain gave up, so an intermittent hang in CI leaves
    /// evidence rather than only a stack trace: jobs per state, a sample of the ones still live with
    /// their latest log lines, and — on PostgreSQL — the advisory locks held and by whom.
    /// </summary>
    private async Task<string> DescribeStuckJobsAsync()
    {
        var report = new System.Text.StringBuilder();

        try
        {
            await using var scope = Host.Services.CreateAsyncScope();
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            var now = DateTime.UtcNow;

            var byState = await ctx.Set<Job>()
                .AsNoTracking()
                .GroupBy(x => x.CurrentState)
                .Select(x => new { State = x.Key, Count = x.Count() })
                .ToListAsync();

            report.AppendLine($"now={now:O} states: {string.Join(", ", byState.Select(x => $"{x.State}={x.Count}"))}");

            // One claim statement per row, or several rows per statement? Rows a single claim took share
            // its worker and its `now` parameter (LastKeepAlive), so this separates the two readings.
            var claims = await ctx.Set<Job>()
                .AsNoTracking()
                .Where(x => x.CurrentState == State.Processing)
                .GroupBy(x => new { x.CurrentWorkerId, x.LastKeepAlive })
                .Select(x => new { x.Key.CurrentWorkerId, x.Key.LastKeepAlive, Count = x.Count() })
                .OrderByDescending(x => x.Count)
                .Take(10)
                .ToListAsync();

            report.AppendLine($"  processing grouped by (worker, claim time), largest first:");
            var workerIds = await ctx.Set<Warp.Core.Data.Entities.Worker>().AsNoTracking().Select(x => x.Id).ToListAsync();
            var groupIds = await ctx.Set<WorkerGroup>().AsNoTracking().Select(x => x.Id).ToListAsync();
            var servers = await ctx.Set<Server>().AsNoTracking().CountAsync();
            report.AppendLine($"  servers={servers} workers={workerIds.Count} groups={groupIds.Count}");

            foreach (var claim in claims)
            {
                // A worker claims one row stamped with its own id; the dispatcher claims a batch stamped
                // with its GROUP id. Which one owns a stuck batch says which path claimed it.
                var owner = OwnerOf(claim.CurrentWorkerId, workerIds, groupIds);
                report.AppendLine($"    {claim.Count,5} rows  {owner} {claim.CurrentWorkerId} keepalive={claim.LastKeepAlive:O}");
            }

            var stuck = await ctx.Set<Job>()
                .AsNoTracking()
                .Where(x => x.CurrentState == State.Enqueued || x.CurrentState == State.Processing || x.CurrentState == State.Awaiting)
                .OrderBy(x => x.ScheduleTime)
                .Take(5)
                .Select(x => new { x.Id, x.CurrentState, x.ScheduleTime, x.Queue, x.Metadata })
                .ToListAsync();

            foreach (var job in stuck)
            {
                report.AppendLine($"  job {job.Id} {job.CurrentState} queue={job.Queue} scheduled={job.ScheduleTime:O} meta={job.Metadata}");

                var logs = await ctx.Set<JobLog>()
                    .AsNoTracking()
                    .Where(x => x.JobId == job.Id)
                    .OrderByDescending(x => x.Timestamp)
                    .Take(4)
                    .Select(x => new { x.Timestamp, x.EventType, x.WorkerId, x.Message })
                    .ToListAsync();

                foreach (var log in logs)
                {
                    report.AppendLine($"    {log.Timestamp:O} {log.EventType} worker={log.WorkerId} {log.Message}");
                }
            }

            if (_provider == BenchmarkProvider.PostgreSql)
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT l.classid, l.objid, l.granted, a.pid, a.application_name, a.state,
                           now() - a.state_change AS idle_for, left(a.query, 120)
                    FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid
                    WHERE l.locktype = 'advisory' AND l.database = (SELECT oid FROM pg_database WHERE datname = current_database())
                    ORDER BY a.pid";
                await using (var reader = await command.ExecuteReaderAsync())
                {
                    report.AppendLine("  advisory locks:");
                    while (await reader.ReadAsync())
                    {
                        report.AppendLine(
                            $"    {reader.GetValue(0)}/{reader.GetValue(1)} granted={reader.GetValue(2)} pid={reader.GetValue(3)} "
                            + $"app={reader.GetValue(4)} state={reader.GetValue(5)} for={reader.GetValue(6)} last={reader.GetValue(7)}");
                    }
                }

                await DescribeClaimAuditAsync(connection, report);

                // The plan the claim gets NOW, with this database's current statistics. The claim is
                // `UPDATE ... FROM (SELECT ... LIMIT n FOR UPDATE SKIP LOCKED)`, and whether the
                // subquery can be re-executed depends on which side of the join the planner puts it.
                await using (var explain = connection.CreateCommand())
                {
                    explain.CommandText = @"
                        EXPLAIN UPDATE warp.job AS t SET current_state = 2
                        FROM (SELECT id FROM warp.job WHERE kind = 1 AND current_state = 1 AND queue = 'default'
                              ORDER BY schedule_time LIMIT 1 FOR UPDATE SKIP LOCKED) AS c
                        WHERE t.id = c.id RETURNING t.id";
                    await using var reader = await explain.ExecuteReaderAsync();
                    report.AppendLine("  claim plan now:");
                    while (await reader.ReadAsync())
                    {
                        report.AppendLine($"    {reader.GetString(0)}");
                    }
                }

                // The claim asks for LIMIT 1, so rows > calls means a statement returned more than it was
                // asked for — and the worker runs only the first, orphaning the rest in Processing. Read
                // through the ADMIN database: the extension is created there, not in this fresh one.
                var admin = Environment.GetEnvironmentVariable(ConnectionStringVariable);
                if (admin is not null)
                {
                    try
                    {
                        await using var adminConnection = new NpgsqlConnection(admin);
                        await adminConnection.OpenAsync();
                        await using var claimStats = adminConnection.CreateCommand();
                        claimStats.CommandText = @"
                            SELECT calls, rows, left(regexp_replace(query, '\s+', ' ', 'g'), 110)
                            FROM pg_stat_statements
                            WHERE dbid = (SELECT oid FROM pg_database WHERE datname = @db)
                              AND query ILIKE 'UPDATE%' AND query ILIKE '%current_worker_id%'
                            ORDER BY calls DESC LIMIT 5";
                        claimStats.Parameters.AddWithValue("db", connection.Database);
                        await using var reader = await claimStats.ExecuteReaderAsync();
                        report.AppendLine("  claim statements in this database (pg_stat_statements):");
                        while (await reader.ReadAsync())
                        {
                            report.AppendLine($"    calls={reader.GetValue(0)} rows={reader.GetValue(1)} {reader.GetValue(2)}");
                        }
                    }
                    catch (PostgresException e)
                    {
                        report.AppendLine($"  (pg_stat_statements unavailable: {e.MessageText})");
                    }
                }
            }
        }
        catch (Exception e)
        {
            report.AppendLine($"(could not describe stuck jobs: {e.GetType().Name}: {e.Message})");
        }

        return report.ToString();
    }

    // From the enum, not a literal: Processing is 3 (Awaiting is 2), and a hand-typed value made the
    // first version of this audit record nothing at all.
    private static readonly string Processing = ((int)State.Processing).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private async Task InstallClaimAuditAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();

        // Statement-level with transition tables, so one row is written per claiming STATEMENT, and
        // it runs inside the server rather than in the process whose timing is being investigated.
        command.CommandText = @"
            CREATE TABLE warp.claim_audit (
                at timestamptz NOT NULL DEFAULT clock_timestamp(),
                pid int NOT NULL,
                app text,
                xid bigint,
                row_count int NOT NULL,
                ids uuid[] NOT NULL,
                worker_ids uuid[] NOT NULL,
                query text,

                -- What the planner believed about the table when this statement ran, and what was true.
                -- Recorded on every claim so a bad one can be set against the good ones either side.
                reltuples real,
                relpages int,
                relallvisible int,
                actual_pages bigint);

            -- Column statistics at the moment of an over-claim, in the shape pg_restore_attribute_stats
            -- takes, so the planner's exact view can be replayed into a local database.
            CREATE TABLE warp.claim_stats (
                at timestamptz NOT NULL DEFAULT clock_timestamp(),
                reltuples real,
                relpages int,
                relallvisible int,
                attname name,
                inherited bool,
                null_frac real,
                avg_width int,
                n_distinct real,
                most_common_vals text,
                most_common_freqs real[],
                histogram_bounds text,
                correlation real);

            CREATE FUNCTION warp.audit_claims() RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE
                moved int;
                taken timestamptz := clock_timestamp();
            BEGIN
                INSERT INTO warp.claim_audit
                    (pid, app, xid, row_count, ids, worker_ids, query, reltuples, relpages, relallvisible, actual_pages)
                SELECT pg_backend_pid(), current_setting('application_name'), txid_current(),
                       count(*), array_agg(n.id), array_agg(DISTINCT n.current_worker_id), left(current_query(), 400),
                       c.reltuples, c.relpages, c.relallvisible, pg_relation_size('warp.job') / current_setting('block_size')::int
                FROM new_rows n JOIN old_rows o ON o.id = n.id
                CROSS JOIN (SELECT reltuples, relpages, relallvisible FROM pg_class WHERE oid = 'warp.job'::regclass) c
                WHERE n.current_state = " + Processing + @" AND o.current_state <> " + Processing + @"
                GROUP BY c.reltuples, c.relpages, c.relallvisible
                HAVING count(*) > 0
                RETURNING row_count INTO moved;

                IF moved > 1 AND NOT EXISTS (
                    SELECT 1 FROM warp.claim_stats WHERE at > taken - interval '2 seconds') THEN
                    INSERT INTO warp.claim_stats
                        (at, reltuples, relpages, relallvisible, attname, inherited, null_frac, avg_width, n_distinct,
                         most_common_vals, most_common_freqs, histogram_bounds, correlation)
                    SELECT taken, c.reltuples, c.relpages, c.relallvisible, s.attname, s.inherited, s.null_frac, s.avg_width,
                           s.n_distinct, s.most_common_vals::text, s.most_common_freqs, s.histogram_bounds::text, s.correlation
                    FROM pg_class c
                    LEFT JOIN pg_stats s ON s.schemaname = 'warp' AND s.tablename = 'job'
                    WHERE c.oid = 'warp.job'::regclass;
                END IF;

                RETURN NULL;
            END $$;

            CREATE TRIGGER audit_claims AFTER UPDATE ON warp.job
                REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows
                FOR EACH STATEMENT EXECUTE FUNCTION warp.audit_claims();";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DescribeClaimAuditAsync(NpgsqlConnection connection, System.Text.StringBuilder report)
    {
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT to_regclass('warp.claim_audit') IS NOT NULL";
        if (await exists.ExecuteScalarAsync() is not true)
        {
            return;
        }

        await using var summary = connection.CreateCommand();
        summary.CommandText = @"
            SELECT row_count, count(*), min(at), max(at) FROM warp.claim_audit GROUP BY row_count ORDER BY row_count";
        await using (var reader = await summary.ExecuteReaderAsync())
        {
            report.AppendLine("  claim audit — statements by rows moved to Processing:");
            while (await reader.ReadAsync())
            {
                report.AppendLine($"    {reader.GetValue(0)} rows x {reader.GetValue(1)} statements ({reader.GetValue(2):O} .. {reader.GetValue(3):O})");
            }
        }

        // The planner's view of the table against the truth, claim by claim. If over-claims cluster at one
        // statistics state, that state is what a local test has to reproduce.
        await using var byStats = connection.CreateCommand();
        byStats.CommandText = @"
            SELECT reltuples, relpages, relallvisible, min(actual_pages), max(actual_pages), count(*),
                   count(*) FILTER (WHERE row_count > 1), min(at), max(at)
            FROM warp.claim_audit
            GROUP BY reltuples, relpages, relallvisible
            ORDER BY min(at)";
        await using (var reader = await byStats.ExecuteReaderAsync())
        {
            report.AppendLine("  claims by planner statistics (reltuples/relpages/allvisible, actual pages, claims, multi-row):");
            while (await reader.ReadAsync())
            {
                report.AppendLine(
                    $"    {reader.GetValue(0)}/{reader.GetValue(1)}/{reader.GetValue(2)} actual={reader.GetValue(3)}..{reader.GetValue(4)} "
                    + $"claims={reader.GetValue(5)} multi-row={reader.GetValue(6)} ({reader.GetValue(7):O} .. {reader.GetValue(8):O})");
            }
        }

        // Which statement claimed each job that is still stuck — the question every earlier dump left open.
        await using var owners = connection.CreateCommand();
        owners.CommandText = @"
            SELECT a.at, a.pid, a.app, a.xid, a.row_count, a.worker_ids, regexp_replace(a.query, '\s+', ' ', 'g'), count(*) AS stuck_here
            FROM warp.job j
            JOIN LATERAL (
                SELECT * FROM warp.claim_audit a WHERE j.id = ANY(a.ids) ORDER BY a.at DESC LIMIT 1) a ON true
            WHERE j.current_state = " + Processing + @"
            GROUP BY a.at, a.pid, a.app, a.xid, a.row_count, a.worker_ids, a.query
            ORDER BY stuck_here DESC, a.at
            LIMIT 12";
        await using (var reader = await owners.ExecuteReaderAsync())
        {
            report.AppendLine("  claim audit — the statement that last claimed each stuck job:");
            while (await reader.ReadAsync())
            {
                report.AppendLine(
                    $"    {reader.GetValue(7)} stuck | {reader.GetValue(0):O} pid={reader.GetValue(1)} app={reader.GetValue(2)} "
                    + $"xid={reader.GetValue(3)} rows={reader.GetValue(4)} workers={string.Join(",", (Guid[])reader.GetValue(5))}");
                report.AppendLine($"      {reader.GetString(6)}");
            }
        }

        await using var unclaimed = connection.CreateCommand();
        unclaimed.CommandText = @"
            SELECT count(*) FROM warp.job j
            WHERE j.current_state = " + Processing + @" AND NOT EXISTS (SELECT 1 FROM warp.claim_audit a WHERE j.id = ANY(a.ids))";
        report.AppendLine($"  stuck jobs with NO claiming statement recorded: {await unclaimed.ExecuteScalarAsync()}");

        await using var backends = connection.CreateCommand();
        backends.CommandText = @"
            SELECT application_name, state, count(*) FROM pg_stat_activity
            WHERE datname = current_database() GROUP BY 1, 2 ORDER BY 3 DESC";
        await using (var reader = await backends.ExecuteReaderAsync())
        {
            report.AppendLine("  backends on this database:");
            while (await reader.ReadAsync())
            {
                report.AppendLine($"    {reader.GetValue(2)} x app={reader.GetValue(0)} state={reader.GetValue(1)}");
            }
        }
    }

    private static string OwnerOf(Guid? id, List<Guid> workerIds, List<Guid> groupIds)
    {
        if (id is not { } value)
        {
            return "none";
        }

        if (workerIds.Contains(value))
        {
            return "worker";
        }

        return groupIds.Contains(value) ? "GROUP" : "unknown";
    }

    /// <summary>
    /// Deletes all job-related rows between benchmark iterations.
    /// </summary>
    public Task CleanJobTables() => CleanJobTables(createdAfter: null);

    /// <summary>
    /// Deletes the jobs created after <paramref name="createdAfter"/>, or every job when it is null, so a
    /// scenario that seeds a standing history can clear each iteration's jobs without deleting it.
    /// </summary>
    public async Task CleanJobTables(DateTime? createdAfter)
    {
        await using var scope = Host.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();

        // Through EF rather than raw SQL, because the two providers do not name these tables the same
        // way: PostgreSQL runs under the snake_case convention and SQL Server keeps Warp's default. The
        // raw `DELETE FROM warp.job_log` this replaces worked on one and failed on the other, and it
        // failed in IterationCleanup - where BenchmarkDotNet reports the fallout as NA rather than as
        // an error, so every SQL Server arm looked like a benchmark that would not run.
        //
        // Order matters: job_log and counter reference nothing, but job is the parent of job_log.
        await ctx.Set<JobLog>().ExecuteDeleteAsync();
        await ctx.Set<Counter>().ExecuteDeleteAsync();
        if (createdAfter is { } since)
        {
            await ctx.Set<Job>().Where(x => x.CreateTime > since).ExecuteDeleteAsync();

            return;
        }

        await ctx.Set<Job>().ExecuteDeleteAsync();
    }

    /// <summary>
    /// Uses the externally-provided database when there is one, otherwise starts a container.
    /// Each run gets a fresh database on the shared server, so arms cannot contaminate each other
    /// through leftover rows or a warmed cache.
    /// </summary>
    private void ConfigureProvider(DbContextOptionsBuilder options)
    {
        if (_provider == BenchmarkProvider.SqlServer)
        {
            // No snake_case here: SQL Server keeps Warp's default naming, and forcing the Postgres
            // convention would rename every table underneath the provider's own SQL.
            options.UseSqlServer(_connectionString);

            return;
        }

        options.UseNpgsql(_connectionString).UseSnakeCaseNamingConvention();
    }

    private async Task<string> ResolveConnectionStringAsync()
    {
        if (_provider == BenchmarkProvider.SqlServer)
        {
            return await ResolveSqlServerConnectionStringAsync();
        }

        var external = Environment.GetEnvironmentVariable(ConnectionStringVariable);

        if (external is null)
        {
            await _container!.StartAsync();

            return _container.GetConnectionString();
        }

        var builder = new NpgsqlConnectionStringBuilder(external);
        var database = $"warpbench_{Guid.NewGuid():N}";

        var adminConnectionString = new NpgsqlConnectionStringBuilder(external) { Database = "postgres" }.ConnectionString;
        await using (var admin = new NpgsqlConnection(adminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{database}\"";
            await create.ExecuteNonQueryAsync();
        }

        builder.Database = database;

        return builder.ConnectionString;
    }

    private async Task<string> ResolveSqlServerConnectionStringAsync()
    {
        var external = Environment.GetEnvironmentVariable(SqlServerConnectionStringVariable);

        if (external is null)
        {
            _sqlContainer = new MsSqlBuilder().Build();
            await _sqlContainer.StartAsync();

            return _sqlContainer.GetConnectionString();
        }

        var builder = new SqlConnectionStringBuilder(external);
        var database = $"warpbench_{Guid.NewGuid():N}";

        var adminConnectionString = new SqlConnectionStringBuilder(external) { InitialCatalog = "master" }.ConnectionString;
        await using (var admin = new SqlConnection(adminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{database}]";
            await create.ExecuteNonQueryAsync();
        }

        builder.InitialCatalog = database;

        return builder.ConnectionString;
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        ExceptionTally.Report();
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        if (_sqlContainer is not null)
        {
            await _sqlContainer.DisposeAsync();
        }
    }
}
