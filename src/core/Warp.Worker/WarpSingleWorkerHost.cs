using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Data.Queries;
using Warp.Core.Events;
using Warp.Core.Notifications;
using Warp.Core.Services;
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
    private readonly WarpCounterBuffer _counterBuffer;
    private readonly ILoggerFactory _loggerFactory;
    private readonly List<BackgroundService> _workers = [];
    private readonly GatedStart _gatedStart;

    // The 8.0 constructor, kept so code built against it still binds. Without a startup gate the host behaves as
    // if registration had completed in StartAsync (RequireDatabaseAtStartup on).
    public WarpSingleWorkerHost(
        IOptions<WarpServerConfiguration> configuration,
        IServiceScopeFactory serviceScopeFactory,
        [FromKeyedServices(DatabaseClock.Key)] TimeProvider timeProvider,
        PauseStateHolder pauseStateHolder,
        IWarpNotificationTransport notificationTransport,
        IWarpSqlQueries<TContext> sqlQueries,
        ServerRegistrationState state,
        ServerTaskSignals<TContext> signals,
        ILoggerFactory loggerFactory,
        WarpCounterBuffer counterBuffer)
        : this(configuration, serviceScopeFactory, timeProvider, pauseStateHolder, notificationTransport, sqlQueries, state, signals, loggerFactory, counterBuffer, startupGate: null)
    {
    }

    public WarpSingleWorkerHost(
        IOptions<WarpServerConfiguration> configuration,
        IServiceScopeFactory serviceScopeFactory,
        [FromKeyedServices(DatabaseClock.Key)] TimeProvider timeProvider,
        PauseStateHolder pauseStateHolder,
        IWarpNotificationTransport notificationTransport,
        IWarpSqlQueries<TContext> sqlQueries,
        ServerRegistrationState state,
        ServerTaskSignals<TContext> signals,
        ILoggerFactory loggerFactory,
        WarpCounterBuffer counterBuffer,
        WarpStartupGate? startupGate)
    {
        _gatedStart = new GatedStart(startupGate, loggerFactory.CreateLogger<WarpSingleWorkerHost<TContext>>());
        _counterBuffer = counterBuffer;
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

    // Starts the workers once the server is registered: now, or (RequireDatabaseAtStartup off, database
    // unreachable) when the background registration succeeds.
    public Task StartAsync(CancellationToken cancellationToken) =>
        _configuration.UseDispatcher
            ? Task.CompletedTask
            : _gatedStart.StartAsync(StartWorkersAsync, cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gatedStart.StopAsync();

        var tasks = _workers.Select(x => x.StopAsync(cancellationToken));
        await Task.WhenAll(tasks);
    }

    private async Task StartWorkersAsync(CancellationToken cancellationToken)
    {
        // No "push without dispatcher" warning: a cross-server JobEnqueued push reaches every bare worker too. The
        // listener (NotificationListenerTask) fires ServerTaskSignals.SignalJobEnqueued, which each WarpWorker subscribes
        // to, as well as the dispatcher wake (§2.9).
        foreach (var registration in _state.Groups)
        {
            // One keep-alive and log flush loop for every job the group runs (rule 6.14).
            var monitor = new RunningJobMonitor(
                _serviceScopeFactory,
                _timeProvider,
                _configuration,
                _sqlQueries.RenewRunningJobsAsync,
                _loggerFactory.CreateLogger<RunningJobMonitor>());

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
                    _signals,
                    _counterBuffer,
                    monitor);

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
}
