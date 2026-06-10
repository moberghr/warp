using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Worker;

namespace Warp.ServerBenchmarks.Infrastructure;

/// <summary>
/// Boots a Testcontainer PostgreSQL + full Warp server for benchmarking.
/// Shared across benchmark iterations via [GlobalSetup]/[GlobalCleanup].
/// </summary>
public class PostgresServerFixture : IAsyncDisposable
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:latest")
        .Build();

    private IHost? _host;
    private string _connectionString = null!;

    public bool IsInitialized => _host != null;

    public IHost Host => _host!;

    /// <summary>
    /// Boots the full server: container, schema, host with workers + background tasks.
    /// </summary>
    public async Task InitializeAsync(int workerCount = 5, bool useDispatcher = false, int completionBatchSize = 50, TimeSpan? completionFlushInterval = null)
    {
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();

        // Boot full Warp server
        _host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning))
            .ConfigureServices(services =>
            {
                services.AddDbContext<TestContext>(options =>
                {
                    options.UseNpgsql(_connectionString)
                        .UseSnakeCaseNamingConvention();
                });

                services.AddWarpServer<TestContext>(config =>
                {
                    config.WorkerCount = workerCount;
                    config.Queues = ["default"];
                    config.PollingInterval = TimeSpan.FromMilliseconds(100);
                    config.OrchestrationInterval = TimeSpan.FromMilliseconds(100);
                    config.MessageRoutingInterval = TimeSpan.FromMilliseconds(500);
                    config.HealthCheckInterval = TimeSpan.FromMilliseconds(200);
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
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();

        // Build a host but don't start it — only use its DI container
        _host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .ConfigureServices(services =>
            {
                services.AddDbContext<TestContext>(options =>
                {
                    options.UseNpgsql(_connectionString)
                        .UseSnakeCaseNamingConvention();
                });

                services.AddWarpServer<TestContext>(config =>
                {
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

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        await _container.DisposeAsync();
    }
}
