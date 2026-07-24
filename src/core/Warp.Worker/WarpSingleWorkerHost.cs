using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core.Data.Queries;
using Warp.Core.Events;
using Warp.Core.Notifications;
using Warp.Worker.Services;

namespace Warp.Worker;

/// <summary>
/// Hosted service that constructs and manages the lifecycle of per-worker
/// <see cref="WarpWorkerService{TContext}"/> + <see cref="WarpWorker{TContext}"/> pairs when
/// <see cref="WarpServerConfiguration.UseDispatcher"/> is false. Depends on
/// <see cref="ServerRegistrationState"/> having been populated by
/// <see cref="WarpServerRegistration{TContext}"/>, which is registered first. No-ops when
/// dispatcher mode is enabled.
/// </summary>
public class WarpSingleWorkerHost<TContext> : IHostedService
    where TContext : DbContext
{
    private readonly WarpServerConfiguration _configuration;
    private readonly IOptions<WarpServerConfiguration> _configurationOptions;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly PauseStateHolder _pauseStateHolder;
    private readonly IWarpNotificationTransport _notificationTransport;
    private readonly IWarpSqlQueries<TContext> _sqlQueries;
    private readonly ServerRegistrationState _state;
    private readonly ServerTaskSignals<TContext> _signals;
    private readonly ILoggerFactory _loggerFactory;
    private readonly List<BackgroundService> _workers = [];

    public WarpSingleWorkerHost(
        IOptions<WarpServerConfiguration> configuration,
        IServiceScopeFactory serviceScopeFactory,
        TimeProvider timeProvider,
        PauseStateHolder pauseStateHolder,
        IWarpNotificationTransport notificationTransport,
        IWarpSqlQueries<TContext> sqlQueries,
        ServerRegistrationState state,
        ServerTaskSignals<TContext> signals,
        ILoggerFactory loggerFactory)
    {
        _configuration = configuration.Value;
        _configurationOptions = configuration;
        _serviceScopeFactory = serviceScopeFactory;
        _timeProvider = timeProvider;
        _pauseStateHolder = pauseStateHolder;
        _notificationTransport = notificationTransport;
        _sqlQueries = sqlQueries;
        _state = state;
        _signals = signals;
        _loggerFactory = loggerFactory;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_configuration.UseDispatcher)
        {
            return;
        }

        // Advisory: DB push is enabled (a real transport was substituted for the null one) but this
        // server runs in single-worker mode, so JobEnqueued pushes have no dispatcher to short-cut —
        // individual bare workers still poll (they wake on the local ServerTaskSignals.JobEnqueued
        // signal, but cross-server JobEnqueued pushes only accelerate dispatcher-mode fetch). This
        // warning moved here from NotificationListenerTask when the listener relocated to Warp.Core
        // (which has no access to WarpServerConfiguration.UseDispatcher); this host runs precisely
        // when UseDispatcher=false, so the condition is identical.
        if (_notificationTransport is not NullNotificationTransport)
        {
            _loggerFactory.CreateLogger<WarpSingleWorkerHost<TContext>>().LogWarning(
                "Warp DB push is enabled but UseDispatcher=false; worker fetch will keep polling. " +
                "Enable UseDispatcher on WarpServerConfiguration to get the full benefit.");
        }

        foreach (var registration in _state.Groups)
        {
            foreach (var workerId in registration.WorkerIds)
            {
                var workerService = new WarpWorkerService<TContext>(
                    workerId,
                    _serviceScopeFactory,
                    _loggerFactory.CreateLogger<WarpWorkerService<TContext>>(),
                    _configurationOptions,
                    registration.Config,
                    _timeProvider,
                    _sqlQueries,
                    _notificationTransport,
                    _signals);

                var worker = new WarpWorker<TContext>(
                    workerService,
                    _loggerFactory.CreateLogger<WarpWorker<TContext>>(),
                    registration.Config,
                    _pauseStateHolder,
                    _timeProvider,
                    registration.GroupEntityId,
                    _signals);

                await worker.StartAsync(cancellationToken);
                _workers.Add(worker);
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var tasks = _workers.Select(x => x.StopAsync(cancellationToken));
        await Task.WhenAll(tasks);
    }
}
