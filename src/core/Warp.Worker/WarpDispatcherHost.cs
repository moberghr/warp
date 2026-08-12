using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core.Data;
using Warp.Core.Events;
using Warp.Core.Notifications;
using Warp.Worker.Services;

namespace Warp.Worker;

/// <summary>
/// Hosted service that constructs and manages the lifecycle of the dispatcher +
/// dispatcher-workers when <see cref="WarpServerConfiguration.UseDispatcher"/> is true.
/// Depends on <see cref="ServerRegistrationState"/> having been populated by
/// <see cref="WarpServerRegistration{TContext}"/>, which is registered first.
/// No-ops when dispatcher mode is disabled.
/// </summary>
public class WarpDispatcherHost<TContext> : IHostedService
    where TContext : DbContext
{
    private readonly WarpServerConfiguration _configuration;
    private readonly IOptions<WarpServerConfiguration> _configurationOptions;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly PauseStateHolder _pauseStateHolder;
    private readonly IWarpNotificationTransport _notificationTransport;
    private readonly ServerRegistrationState _state;
    private readonly ServerTaskSignals<TContext> _signals;
    private readonly DispatcherRegistry _dispatcherRegistry;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IDatabaseExceptionClassifier _exceptionClassifier;
    private readonly List<BackgroundService> _workers = [];

    public WarpDispatcherHost(
        IOptions<WarpServerConfiguration> configuration,
        IServiceScopeFactory serviceScopeFactory,
        TimeProvider timeProvider,
        PauseStateHolder pauseStateHolder,
        IWarpNotificationTransport notificationTransport,
        ServerRegistrationState state,
        ServerTaskSignals<TContext> signals,
        DispatcherRegistry dispatcherRegistry,
        ILoggerFactory loggerFactory,
        IDatabaseExceptionClassifier exceptionClassifier)
    {
        _configuration = configuration.Value;
        _configurationOptions = configuration;
        _serviceScopeFactory = serviceScopeFactory;
        _timeProvider = timeProvider;
        _pauseStateHolder = pauseStateHolder;
        _notificationTransport = notificationTransport;
        _state = state;
        _signals = signals;
        _dispatcherRegistry = dispatcherRegistry;
        _loggerFactory = loggerFactory;
        _exceptionClassifier = exceptionClassifier;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_configuration.UseDispatcher)
        {
            return;
        }

        foreach (var registration in _state.Groups)
        {
            var dispatcher = new WarpDispatcher<TContext>(
                _serviceScopeFactory,
                _loggerFactory.CreateLogger<WarpDispatcher<TContext>>(),
                _configurationOptions,
                registration.Config,
                _timeProvider,
                _pauseStateHolder,
                registration.GroupEntityId,
                _dispatcherRegistry);

            await dispatcher.StartAsync(cancellationToken);
            _workers.Add(dispatcher);

            foreach (var workerId in registration.WorkerIds)
            {
                var worker = new WarpDispatcherWorker<TContext>(
                    workerId,
                    dispatcher.JobReader,
                    _serviceScopeFactory,
                    _loggerFactory.CreateLogger<WarpDispatcherWorker<TContext>>(),
                    _configurationOptions,
                    _timeProvider,
                    _notificationTransport,
                    _signals,
                    _exceptionClassifier,
                    dispatcher.Availability);

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
