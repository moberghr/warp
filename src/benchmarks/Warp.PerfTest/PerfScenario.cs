using System.Diagnostics;
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
using Warp.Provider.PostgreSql;
using Warp.Worker;

namespace Warp.PerfTest;

public sealed record PerfResult(
    string Name,
    int Jobs,
    TimeSpan Duration,
    long Select,
    long Update,
    long Insert,
    long Delete,
    long Other,
    long Total);

public sealed class PerfScenario : IAsyncDisposable
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:latest")
        .Build();

    private IHost? _host;
    private CommandCountingInterceptor _interceptor = null!;

    public static async Task<PerfResult> RunAsync(
        string name,
        int jobCount,
        bool useDispatcher,
        bool enableDatabasePush)
    {
        await using var scenario = new PerfScenario();
        return await scenario.ExecuteAsync(name, jobCount, useDispatcher, enableDatabasePush);
    }

    private async Task<PerfResult> ExecuteAsync(
        string name,
        int jobCount,
        bool useDispatcher,
        bool enableDatabasePush)
    {
        await _container.StartAsync();
        var connectionString = _container.GetConnectionString();
        _interceptor = new CommandCountingInterceptor();

        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .ConfigureServices(services =>
            {
                services.AddDbContext<TestContext>(options =>
                {
                    options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
                    options.AddInterceptors(_interceptor);
                });

                services.AddWarpServer<TestContext>(config =>
                {
                    config.UsePostgreSql();
                    config.WorkerCount = 5;
                    config.Queues = ["default"];

                    // Production-realistic polling: 1s floor with backoff up to 30s on idle.
                    // This is the configuration push actually helps — tight test-style polling
                    // (100ms) masks push's benefit because polls already fire often enough.
                    config.PollingInterval = TimeSpan.FromSeconds(1);
                    config.MaxPollingInterval = TimeSpan.FromSeconds(30);
                    config.PollingIntervalFactor = 2.0;

                    // MessageRoutingInterval also long so push's wake-up is visible there too.
                    config.MessageRoutingInterval = TimeSpan.FromSeconds(1);
                    config.OrchestrationInterval = TimeSpan.FromSeconds(1);
                    config.HealthCheckInterval = TimeSpan.FromSeconds(3);
                    config.CounterAggregationInterval = TimeSpan.FromSeconds(5);
                    config.StaleJobRecoveryInterval = TimeSpan.FromSeconds(30);
                    config.ExpirationCleanupInterval = TimeSpan.FromSeconds(60);
                    config.UseDispatcher = useDispatcher;

                    if (enableDatabasePush)
                    {
                        var channel = "warp_perf_" + name.Replace('-', '_');
                        config.UseDatabasePush(o => o.ChannelName = channel);
                    }
                });
            })
            .Build();

        await using (var scope = _host.Services.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            await ctx.Database.EnsureCreatedAsync();
        }

        // Reset the counter AFTER schema creation so the CREATE TABLE commands don't count.
        _interceptor.Reset();

        await _host.StartAsync();

        // Two-burst workload with idle between: lets the poll-only scenarios' exponential
        // backoff grow during the gap, so the second burst pays the full MaxPollingInterval
        // wake-up cost. Push wakes immediately on the second-burst enqueue. This is the
        // pattern that makes push's benefit visible.
        var halfCount = jobCount / 2;
        var secondHalf = jobCount - halfCount;

        await EnqueueBatchAsync(halfCount);

        var sw = Stopwatch.StartNew();
        await WaitForCompletionAsync(TimeSpan.FromMinutes(5));

        // Idle period — enough for polling backoff to ramp up near MaxPollingInterval.
        await Task.Delay(TimeSpan.FromSeconds(15));

        await EnqueueBatchAsync(secondHalf);
        await WaitForCompletionAsync(TimeSpan.FromMinutes(5));
        sw.Stop();

        return new PerfResult(
            name,
            jobCount,
            sw.Elapsed,
            _interceptor.Select,
            _interceptor.Update,
            _interceptor.Insert,
            _interceptor.Delete,
            _interceptor.Other,
            _interceptor.Total);
    }

    private async Task EnqueueBatchAsync(int count)
    {
        await using var scope = _host!.Services.CreateAsyncScope();
        var publisher = scope.ServiceProvider.GetRequiredService<IPublisher>();
        for (var i = 0; i < count; i++)
        {
            await publisher.Enqueue(new EmptyRequest());
        }

        await publisher.SaveChangesAsync();
    }

    private async Task WaitForCompletionAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await using var scope = _host!.Services.CreateAsyncScope();
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            var active = await ctx.Set<Job>().CountAsync(x =>
                x.CurrentState == State.Enqueued
                || x.CurrentState == State.Processing
                || x.CurrentState == State.Awaiting
                || x.CurrentState == State.Scheduled);
            if (active == 0)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("Not all jobs completed within timeout");
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
