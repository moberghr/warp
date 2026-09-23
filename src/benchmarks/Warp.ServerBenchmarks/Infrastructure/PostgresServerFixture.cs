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
        BenchmarkProvider provider = BenchmarkProvider.PostgreSql)
    {
        _provider = provider;
        _connectionString = await ResolveConnectionStringAsync();

        // Boot full Warp server
        _host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning))
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
                });
            })
            .Build();

        // Use DI-resolved context for schema creation (includes Warp entity configurations)
        await using var scope = _host.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
        await ctx.Database.EnsureCreatedAsync();

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

        throw new TimeoutException("Not all jobs completed within timeout");
    }

    /// <summary>
    /// Deletes all job-related rows between benchmark iterations.
    /// </summary>
    public async Task CleanJobTables()
    {
        await using var scope = Host.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
        await ctx.Database.ExecuteSqlRawAsync(
            """
            DELETE FROM warp.job_log;
            DELETE FROM warp.counter;
            DELETE FROM warp.job;
            """);
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
