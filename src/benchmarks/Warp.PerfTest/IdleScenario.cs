using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;
using Warp.Core;
using Warp.Provider.PostgreSql;
using Warp.Worker;

namespace Warp.PerfTest;

public sealed record IdleResult(
    string Name,
    bool UseDispatcher,
    bool EnableDatabasePush,
    TimeSpan WallClock,
    long Select,
    long Update,
    long Insert,
    long Delete,
    long Other,
    long Total,
    IReadOnlyDictionary<string, long>? CapturedByText = null)
{
    public double QueriesPerSecond => Total / WallClock.TotalSeconds;
}

/// <summary>
/// Boots a Warp server with the DEFAULT configuration (only UseDispatcher and UseDatabasePush
/// vary), lets it sit idle for <paramref name="idleSeconds"/>, and tallies the SQL commands
/// emitted during that window. Quantifies the steady-state DB chatter from background tasks
/// (heartbeat, scheduler, counter-aggregator, message-router, orchestrator, scheduled-job-
/// activation) for the four (UseDispatcher x UseDatabasePush) combinations.
/// </summary>
public sealed class IdleScenario : IAsyncDisposable
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:latest")
        .Build();

    private IHost? _host;
    private CommandCountingInterceptor _interceptor = null!;
    private DbCommandActivityCounter _activityCounter = null!;

    public static async Task<IdleResult> RunAsync(
        string name,
        int idleSeconds,
        bool useDispatcher,
        bool enableDatabasePush,
        bool captureSql = false)
    {
        await using var scenario = new IdleScenario();

        return await scenario.ExecuteAsync(name, idleSeconds, useDispatcher, enableDatabasePush, captureSql);
    }

    private async Task<IdleResult> ExecuteAsync(
        string name,
        int idleSeconds,
        bool useDispatcher,
        bool enableDatabasePush,
        bool captureSql)
    {
        await _container.StartAsync();
        var connectionString = _container.GetConnectionString();
        _interceptor = new CommandCountingInterceptor();

        // Activity-based counter catches commands issued via raw DbConnection.CreateCommand()
        // — those bypass EF Core's interceptor. Wrapping creation BEFORE the host so we
        // capture even the EnsureCreated DDL if needed; we Reset() before measurement.
        _activityCounter = new DbCommandActivityCounter();

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
                    // Defaults only: the point is to measure what an out-of-the-box server
                    // costs while idle. UseDispatcher and UseDatabasePush are the only knobs
                    // varied across the matrix. UseDatabasePush also auto-bumps
                    // MessageRoutingInterval and OrchestrationInterval — that bump is part of
                    // what we want to measure here.
                    config.UsePostgreSql();
                    config.UseDispatcher = useDispatcher;
                    if (enableDatabasePush)
                    {
                        var channel = "warp_idle_" + name.Replace('-', '_');
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

        await _host.StartAsync();

        // Let background tasks register and complete their first iteration. Server task
        // EnsureRegisteredAsync + first-tick noise would inflate the per-second rate if
        // we counted it.
        await Task.Delay(TimeSpan.FromSeconds(3));
        _interceptor.Reset();
        _interceptor.CaptureSql = captureSql;
        _activityCounter.Reset();
        _activityCounter.CaptureSql = captureSql;

        var sw = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(idleSeconds));
        sw.Stop();

        // The activity counter is the source of truth: it sees ALL Npgsql commands, including
        // those issued via raw DbConnection.CreateCommand() (Warp's HeartbeatAsync,
        // ActivateScheduledJobsAsync, notification transport). The EF interceptor only sees
        // commands EF Core created. We keep the interceptor for now as a cross-check during
        // bring-up; if they disagree, the activity counter is right.
        return new IdleResult(
            name,
            useDispatcher,
            enableDatabasePush,
            sw.Elapsed,
            _activityCounter.Select,
            _activityCounter.Update,
            _activityCounter.Insert,
            _activityCounter.Delete,
            _activityCounter.Other,
            _activityCounter.Total,
            captureSql ? new Dictionary<string, long>(_activityCounter.CapturedByText) : null);
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        _activityCounter?.Dispose();
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        await _container.DisposeAsync();
    }
}
