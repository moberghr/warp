using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Warp.Core.Events;

namespace Warp.Worker;

public class WarpWorker<TContext> : BackgroundService
    where TContext : DbContext
{
    private readonly ILogger<WarpWorker<TContext>> _logger;
    private readonly IWarpWorkerService _warpWorkerService;
    private readonly WorkerGroupConfiguration _groupConfiguration;
    private readonly PauseStateHolder _pauseStateHolder;
    private readonly TimeProvider _timeProvider;
    private readonly Guid _workerGroupId;
    private readonly ServerTaskSignals<TContext> _signals;
    private readonly SemaphoreSlim _signal = new(0, 1);

    public WarpWorker(IWarpWorkerService warpWorkerService, ILogger<WarpWorker<TContext>> logger, WorkerGroupConfiguration groupConfiguration, PauseStateHolder pauseStateHolder, TimeProvider timeProvider, Guid workerGroupId, ServerTaskSignals<TContext> signals)
    {
        _warpWorkerService = warpWorkerService;
        _logger = logger;
        _groupConfiguration = groupConfiguration;
        _pauseStateHolder = pauseStateHolder;
        _timeProvider = timeProvider;
        _workerGroupId = workerGroupId;
        _signals = signals;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var floor = _groupConfiguration.PollingInterval;
        var max = _groupConfiguration.MaxPollingInterval;
        var factor = _groupConfiguration.PollingIntervalFactor;
        var currentDelay = floor;

        // Subscribe to JobEnqueued so any in-process enqueue (Publisher.Publish on this server,
        // MessageRouter creating child jobs, ScheduledJobActivation flipping rows, the listener
        // receiving a cross-server push) wakes the next WaitAsync immediately. The signal lock
        // serialises Release with the CurrentCount check — same pattern as ServerTaskLoop.Signal.
        var signalLock = new Lock();
        using var subscription = _signals.Subscribe(ServerTaskSignal.JobEnqueued, () =>
        {
            lock (signalLock)
            {
                if (_signal.CurrentCount == 0)
                {
                    _signal.Release();
                }
            }
        });

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_pauseStateHolder.IsPaused(_workerGroupId))
                {
                    currentDelay = floor;
                    await WaitAsync(floor, stoppingToken);
                    continue;
                }

                var didProcessJob = await _warpWorkerService.GetAndProcessJob(stoppingToken);
                if (didProcessJob)
                {
                    currentDelay = floor;
                    continue;
                }

                currentDelay = PollingBackoff.Next(currentDelay, floor, max, factor);
                await WaitAsync(currentDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                // Routine in multi-server deployments: another worker/server updated the row
                // first (row lock raced or concurrency token bumped). Not a handler failure —
                // don't spam stack traces at Error level. Short delay, re-fetch.
                _logger.LogDebug(ex, "Worker fetch hit optimistic concurrency; another worker won the row.");
                await WaitAsync(floor, stoppingToken);
            }
            catch (Exception ex)
            {
                // Exception is a transient signal, not an idle-queue signal — do not compound
                // the polling backoff. Sleep a short fixed interval and retry, keeping the
                // service alive across DB hiccups or handler pipeline faults.
                _logger.LogError(ex, "Worker fetch failed");
                await WaitAsync(floor, stoppingToken);
            }
        }

        _logger.LogInformation("Warp worker is stopping.");
    }

    public override void Dispose()
    {
        _signal.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        // Single combined wait: SemaphoreSlim.WaitAsync(delay, ct) is atomic — either the
        // semaphore was released (signal-driven wake, returns true) or the timeout elapsed
        // (cadence-driven wake, returns false), and the count is consistently consumed or
        // preserved either way. The earlier Task.WhenAny pattern had a microsecond window in
        // its cleanup where a signal pulse racing with `linkedCts.CancelAsync()` could be
        // silently consumed by the still-pending signal task, dropping the wake-up.
        // The trade-off is that this path uses wall-clock time and ignores a fake
        // TimeProvider — the only test that relied on TimeProvider for polling cadence is
        // rewritten in real time with short intervals (see
        // WarpWorkerResilienceTests.ExecuteAsync_AfterProcessingJob_ResetsBackoffToFloor).
        _ = _timeProvider;
        _ = await _signal.WaitAsync(delay, ct);
    }
}
