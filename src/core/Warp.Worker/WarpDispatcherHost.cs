using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core.Data;
using Warp.Core.Data.Queries;
using Warp.Core.Events;
using Warp.Core.Notifications;
using Warp.Core.Services;
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
    private readonly WarpCounterBuffer _counterBuffer;
    private readonly IWarpSqlQueries<TContext> _sqlQueries;
    private readonly DispatcherRegistry _dispatcherRegistry;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IDatabaseExceptionClassifier _exceptionClassifier;
    private readonly List<BackgroundService> _workers = [];
    private readonly List<CompletionFlusher<TContext>> _flushers = [];

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
        IDatabaseExceptionClassifier exceptionClassifier,
        WarpCounterBuffer counterBuffer,
        IWarpSqlQueries<TContext> sqlQueries)
    {
        _counterBuffer = counterBuffer;
        _sqlQueries = sqlQueries;
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
                _dispatcherRegistry,
                _signals);

            await dispatcher.StartAsync(cancellationToken);
            _workers.Add(dispatcher);

            // One completion buffer for the whole group (rule 6.13).
            var batch = new CompletionBatch(
                _serviceScopeFactory,
                _timeProvider,
                _loggerFactory.CreateLogger<CompletionBatch>(),
                _exceptionClassifier,
                _configuration.CompletionBatchSize,
                _configuration.CompletionFlushInterval,
                _counterBuffer,
                _sqlQueries.CompleteJobsAsync);

            // ... and one loop that writes it, so no worker waits on a completion write (rule 6.13).
            var flusher = new CompletionFlusher<TContext>(
                batch,
                dispatcher.Availability,
                _timeProvider,
                _signals,
                _notificationTransport,
                _loggerFactory.CreateLogger<CompletionFlusher<TContext>>());
            flusher.Start();
            _flushers.Add(flusher);

            // One keep-alive and log flush loop for every job the group runs (rule 6.14).
            var monitor = new RunningJobMonitor(
                _serviceScopeFactory,
                _timeProvider,
                _configuration,
                _sqlQueries.RenewRunningJobsAsync,
                _loggerFactory.CreateLogger<RunningJobMonitor>());

            foreach (var workerId in registration.WorkerIds)
            {
                var worker = new WarpDispatcherWorker<TContext>(
                    workerId,
                    dispatcher.JobReader,
                    _serviceScopeFactory,
                    _loggerFactory.CreateLogger<WarpDispatcherWorker<TContext>>(),
                    _configurationOptions,
                    _timeProvider,
                    dispatcher.Availability,
                    _counterBuffer,
                    flusher,
                    monitor);

                await worker.StartAsync(cancellationToken);
                _workers.Add(worker);
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var tasks = _workers.Select(x => x.StopAsync(cancellationToken));
        await Task.WhenAll(tasks);

        // Only once every worker has stopped: nothing is added after this, and the final flush writes
        // the last completions.
        await Task.WhenAll(_flushers.Select(x => x.StopAsync(cancellationToken)));
        foreach (var flusher in _flushers)
        {
            flusher.Dispose();
        }
    }
}
