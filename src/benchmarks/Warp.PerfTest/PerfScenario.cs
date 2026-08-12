using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Provider.PostgreSql;
using Warp.Provider.SqlServer;
using Warp.Worker;

namespace Warp.PerfTest;

public sealed record PerfResult(
    string Name,
    int Jobs,
    TimeSpan Duration,
    TimeSpan Burst1,
    TimeSpan Burst2,
    TimeSpan Enqueue,
    TimeSpan Drain,
    long Select,
    long Update,
    long Insert,
    long Delete,
    long Other,
    long Total,
    long ServerBatchRequests = 0,
    IReadOnlyDictionary<string, long>? CapturedByText = null);

public sealed class PerfScenario : IAsyncDisposable
{
    private readonly DotNet.Testcontainers.Containers.IDatabaseContainer _container;
    private readonly bool _sqlServer;

    private PerfScenario(bool sqlServer)
    {
        _sqlServer = sqlServer;
        _container = sqlServer
            ? new MsSqlBuilder().WithImage("mcr.microsoft.com/mssql/server:2022-latest").Build()
            : new PostgreSqlBuilder().WithImage("postgres:latest").Build();
    }

    private IHost? _host;
    private CommandCountingInterceptor _interceptor = null!;

    public static async Task<PerfResult> RunAsync(
        string name,
        int jobCount,
        bool useDispatcher,
        bool enableDatabasePush,
        int? completionFlushMs = null,
        bool captureSql = false,
        bool sqlServer = false,
        bool singleBurst = false,
        int? prefetchCount = null)
    {
        await using var scenario = new PerfScenario(sqlServer);
        return await scenario.ExecuteAsync(name, jobCount, useDispatcher, enableDatabasePush, completionFlushMs, captureSql, singleBurst, prefetchCount);
    }

    private async Task<PerfResult> ExecuteAsync(
        string name,
        int jobCount,
        bool useDispatcher,
        bool enableDatabasePush,
        int? completionFlushMs,
        bool captureSql,
        bool singleBurst,
        int? prefetchCount)
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
                    if (_sqlServer)
                    {
                        options.UseSqlServer(connectionString);
                    }
                    else
                    {
                        options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
                    }

                    options.AddInterceptors(_interceptor);
                });

                services.AddWarpServer<TestContext>(config =>
                {
                    if (_sqlServer)
                    {
                        config.UseSqlServer();
                    }
                    else
                    {
                        config.UsePostgreSql();
                    }

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
                    if (prefetchCount.HasValue)
                    {
                        config.PrefetchCount = prefetchCount.Value;
                    }

                    if (completionFlushMs.HasValue)
                    {
                        config.CompletionFlushInterval = TimeSpan.FromMilliseconds(completionFlushMs.Value);
                    }

                    if (enableDatabasePush)
                    {
                        var channel = "warp_perf_" + name.Replace('-', '_');
                        config.UseDatabasePush(o => o.ChannelName = channel);
                    }
                });

                // The worker fetch/complete path and the server tasks run on the internal
                // WarpServerContext (§2.14), NOT on TestContext — so an interceptor registered on
                // TestContext alone sees almost none of the server's DB traffic. Decorate the
                // provider's IWarpServerContextConfigurator so the same counter also observes the
                // server context; otherwise these numbers measure only what leaks onto the user's
                // context, which is exactly the bug this run is verifying.
                var configurator = services.Last(d => d.ServiceType == typeof(IWarpServerContextConfigurator));
                services.Remove(configurator);
                var inner = (IWarpServerContextConfigurator)configurator.ImplementationInstance!;
                services.AddSingleton<IWarpServerContextConfigurator>(
                    new InterceptingServerContextConfigurator(inner, _interceptor));
            })
            .Build();

        await using (var scope = _host.Services.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            await ctx.Database.EnsureCreatedAsync();
        }

        // Reset the counter AFTER schema creation so the CREATE TABLE commands don't count.
        _interceptor.Reset();
        _interceptor.CaptureSql = captureSql;

        // On SQL Server, also read the engine's own cumulative batch-request counter. It is
        // client-agnostic, so the same measurement can be taken against Hangfire and compared
        // without trusting two different client-side counting mechanisms.
        var batchBaseline = _sqlServer ? await ReadBatchRequestsAsync(connectionString) : 0;

        await _host.StartAsync();

        // Two-burst workload with idle between: lets the poll-only scenarios' exponential
        // backoff grow during the gap, so the second burst pays the full MaxPollingInterval
        // wake-up cost. Push wakes immediately on the second-burst enqueue. This is the
        // pattern that makes push's benefit visible.
        var halfCount = singleBurst ? jobCount : jobCount / 2;
        var secondHalf = jobCount - halfCount;

        // Each burst is timed from the start of its enqueue to full drain. The whole-run duration
        // also contains the deliberate 15s idle gap AND the polling wake-up that gap is designed to
        // provoke, so only the per-burst figures measure throughput.
        var sw = Stopwatch.StartNew();
        var enqueue = new Stopwatch();
        var drain = new Stopwatch();

        var b1 = Stopwatch.StartNew();
        enqueue.Start();
        await EnqueueBatchAsync(halfCount);
        enqueue.Stop();
        drain.Start();
        await WaitForCompletionAsync(TimeSpan.FromMinutes(5));
        drain.Stop();
        b1.Stop();

        // Idle period — enough for polling backoff to ramp up near MaxPollingInterval.
        var b2 = new Stopwatch();
        if (!singleBurst)
        {
            await Task.Delay(TimeSpan.FromSeconds(15));

            b2.Start();
            enqueue.Start();
            await EnqueueBatchAsync(secondHalf);
            enqueue.Stop();
            drain.Start();
            await WaitForCompletionAsync(TimeSpan.FromMinutes(5));
            drain.Stop();
            b2.Stop();
        }

        sw.Stop();

        return new PerfResult(
            name,
            jobCount,
            sw.Elapsed,
            b1.Elapsed,
            b2.Elapsed,
            enqueue.Elapsed,
            drain.Elapsed,
            _interceptor.Select,
            _interceptor.Update,
            _interceptor.Insert,
            _interceptor.Delete,
            _interceptor.Other,
            _interceptor.Total,
            _sqlServer ? await ReadBatchRequestsAsync(connectionString) - batchBaseline : 0,
            captureSql ? _interceptor.CapturedByText.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal) : null);
    }

    private static async Task<long> ReadBatchRequestsAsync(string connectionString)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "select cntr_value from sys.dm_os_performance_counters " +
            "where counter_name = 'Batch Requests/sec' and object_name like '%SQL Statistics%'";

        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
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

    private sealed class InterceptingServerContextConfigurator(
        IWarpServerContextConfigurator inner,
        CommandCountingInterceptor interceptor) : IWarpServerContextConfigurator
    {
        public void Configure(DbContextOptionsBuilder optionsBuilder, IServiceProvider applicationServices)
        {
            inner.Configure(optionsBuilder, applicationServices);
            optionsBuilder.AddInterceptors(interceptor);
        }
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
