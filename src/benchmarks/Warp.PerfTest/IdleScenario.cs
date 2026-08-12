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
    IReadOnlyDictionary<string, long>? CapturedByText = null,
    IReadOnlyList<string>? DisabledLoops = null,
    IReadOnlyDictionary<string, long>? ServerLogByTask = null)
{
    public double QueriesPerSecond => Total / WallClock.TotalSeconds;

    public string Label => DisabledLoops is null || DisabledLoops.Count == 0
        ? Name
        : $"{Name} -{string.Join(",", DisabledLoops)}";
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
        bool captureSql = false,
        IReadOnlyList<string>? disabledLoops = null)
    {
        await using var scenario = new IdleScenario();

        return await scenario.ExecuteAsync(name, idleSeconds, useDispatcher, enableDatabasePush, captureSql, disabledLoops ?? []);
    }

    private async Task<IdleResult> ExecuteAsync(
        string name,
        int idleSeconds,
        bool useDispatcher,
        bool enableDatabasePush,
        bool captureSql,
        IReadOnlyList<string> disabledLoops)
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

                    // Harness-only per-loop attribution switch. No-op unless --disable was
                    // passed, so the default matrix still measures out-of-the-box defaults.
                    // Applied last so it also overrides the interval bumps UseDatabasePush
                    // makes to MessageRouting/Orchestration.
                    IdleLoopSwitches.Apply(config, disabledLoops);
                });

                // Server tasks run on the internal WarpServerContext (§2.14), so an interceptor on
                // TestContext alone misses their queries. Decorate the provider's configurator so the
                // same counter observes both contexts — otherwise idle q/s counts only the traffic
                // that leaks onto the user's context.
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

        await _host.StartAsync();

        // Let background tasks register and complete their first iteration. Server task
        // EnsureRegisteredAsync + first-tick noise would inflate the per-second rate if
        // we counted it.
        await Task.Delay(TimeSpan.FromSeconds(3));
        _interceptor.Reset();
        _interceptor.CaptureSql = captureSql;
        _activityCounter.Reset();
        _activityCounter.CaptureSql = captureSql;

        var windowStart = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(idleSeconds));
        sw.Stop();

        // Snapshot the counters BEFORE the diagnostic query below, so the diagnostic's own
        // commands are never part of the measurement.
        var (select, update, insert, delete, other, total) = (
            _activityCounter.Select,
            _activityCounter.Update,
            _activityCounter.Insert,
            _activityCounter.Delete,
            _activityCounter.Other,
            _activityCounter.Total);
        var captured = captureSql ? new Dictionary<string, long>(_activityCounter.CapturedByText) : null;

        // Which task wrote how many ServerLog rows during the run. Explains the bookkeeping
        // half of the command total: a ServerLog INSERT only happens on a tick that reported
        // work (message != null) or failed, whereas the ServerTask UPDATE happens every tick.
        var serverLogByTask = captureSql ? await ReadServerLogByTaskAsync(windowStart) : null;

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
            select,
            update,
            insert,
            delete,
            other,
            total,
            captured,
            disabledLoops,
            serverLogByTask);
    }

    private async Task<IReadOnlyDictionary<string, long>> ReadServerLogByTaskAsync(DateTime windowStart)
    {
        await using var scope = _host!.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();

        var rows = await ctx.Set<Warp.Core.Data.Entities.ServerLog>()
            .Where(x => x.Timestamp >= windowStart)
            .GroupBy(x => x.ServerTaskId)
            .Select(x =>
                new
                {
                    TaskId = x.Key,
                    Count = (long)x.Count(),
                })
            .ToListAsync();

        var names = await ctx.Set<Warp.Core.Data.Entities.ServerTask>()
            .Select(x =>
                new
                {
                    x.Id,
                    x.TaskName,
                })
            .ToDictionaryAsync(x => x.Id, x => x.TaskName);

        return rows.ToDictionary(
            x => x.TaskId != null && names.TryGetValue(x.TaskId.Value, out var n) ? n : "(none)",
            x => x.Count,
            StringComparer.Ordinal);
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
        _activityCounter?.Dispose();
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        await _container.DisposeAsync();
    }
}
