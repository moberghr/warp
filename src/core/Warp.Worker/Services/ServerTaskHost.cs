using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Events;

namespace Warp.Worker.Services;

/// <summary>
/// The one <see cref="BackgroundService"/> that drives every registered <see cref="IServerTask"/> (rule
/// 6.23). It splits them once, at construction:
/// <list type="bullet">
/// <item>A task with a lock key is cluster-wide: it goes to the <see cref="ClusterTaskScheduler{TContext}"/>,
/// which claims it by lease so the cluster runs it once per tick.</item>
/// <item>A task without one (the heartbeat) is this server's own: it gets a
/// <see cref="PerServerTaskLoop{TContext}"/>, which simply runs it on its interval.</item>
/// </list>
/// A task whose <see cref="IServerTask.DefaultInterval"/> is null is not run; it stays in DI so other code
/// can still resolve it.
/// </summary>
public sealed class ServerTaskHost<TContext> : BackgroundService
    where TContext : DbContext
{
    private readonly Dictionary<Type, ServerTaskInfo> _tasks = [];
    private readonly ServerTaskRunner<TContext> _runner;
    private readonly ClusterTaskScheduler<TContext>? _scheduler;
    private readonly List<PerServerTaskLoop<TContext>> _perServer = [];
    private readonly List<IDisposable> _signalSubscriptions = [];

    public ServerTaskHost(
        IServiceScopeFactory scopes,
        IWarpLockProvider lockProvider,
        [FromKeyedServices(DatabaseClock.Key)] TimeProvider time,
        ILoggerFactory loggerFactory,
        IOptions<WarpServerConfiguration> configuration,
        ServerTaskSignals<TContext> signals)
    {
        var logger = loggerFactory.CreateLogger<ServerTaskHost<TContext>>();
        var signalInterval = configuration.Value.ServerTaskSignalInterval ?? TimeSpan.Zero;
        _runner = new ServerTaskRunner<TContext>(scopes, lockProvider);

        using (var metadataScope = scopes.CreateScope())
        {
            foreach (var task in metadataScope.ServiceProvider.GetServices<IServerTask>())
            {
                if (_tasks.ContainsKey(task.GetType()))
                {
                    continue;
                }

                if (task.DefaultInterval == null)
                {
                    logger.LogInformation("Server task {Name} has a null DefaultInterval and is not run.", task.Name);
                    continue;
                }

                _tasks[task.GetType()] = ServerTaskInfo.From(task);
            }
        }

        var clusterWide = _tasks.Values.Where(x => !x.IsLocal).ToList();
        if (clusterWide.Count > 0)
        {
            _scheduler = new ClusterTaskScheduler<TContext>(
                clusterWide,
                _runner,
                scopes,
                time,
                configuration.Value.ServerId,
                loggerFactory.CreateLogger<ClusterTaskScheduler<TContext>>(),
                signalInterval,
                configuration.Value.HealthCheckTimeout);

            foreach (var (name, channels) in _scheduler.Subscriptions)
            {
                foreach (var channel in channels)
                {
                    _signalSubscriptions.Add(signals.Subscribe(channel, () => _scheduler.Signal(name)));
                }
            }
        }

        foreach (var info in _tasks.Values.Where(x => x.IsLocal))
        {
            var loop = new PerServerTaskLoop<TContext>(
                info,
                _runner,
                scopes,
                time,
                configuration.Value.ServerId,
                loggerFactory.CreateLogger($"Warp.Worker.Services.ServerTask[{info.Name}]"),
                signalInterval);
            _perServer.Add(loop);
            foreach (var channel in info.Signals)
            {
                _signalSubscriptions.Add(signals.Subscribe(channel, loop.Signal));
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loops = _perServer.ConvertAll(x => x.RunAsync(stoppingToken));
        if (_scheduler != null)
        {
            loops.Add(_scheduler.RunAsync(stoppingToken));
        }

        try
        {
            await Task.WhenAll(loops);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Test-only. Runs <typeparamref name="TTask"/> once, in its scope and with its commit boundary, outside
    /// the schedule: no lease, no completion, no log rows, and exceptions propagate.
    /// </summary>
    internal async Task<string?> RunOnceAsync<TTask>(CancellationToken ct)
        where TTask : IServerTask
    {
        if (!_tasks.TryGetValue(typeof(TTask), out var info))
        {
            throw new InvalidOperationException(
                $"Server task {typeof(TTask).Name} is not run by this host: it is not registered as IServerTask, or its DefaultInterval is null.");
        }

        var (_, message) = await _runner.RunAsync(info, underLegacyLock: false, ct);

        return message;
    }

    public override void Dispose()
    {
        base.Dispose();
        foreach (var subscription in _signalSubscriptions)
        {
            subscription.Dispose();
        }

        foreach (var loop in _perServer)
        {
            loop.Dispose();
        }

        _scheduler?.Dispose();
    }
}
