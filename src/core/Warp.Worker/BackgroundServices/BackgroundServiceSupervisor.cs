using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Warp.Core.BackgroundServices;
using Warp.Core.Logging;

namespace Warp.Worker.BackgroundServices;

// Non-generic holder so S2743 (static fields in generic types) is not tripped by constants
// that don't depend on TContext — same pattern as ServerTaskLoopConstants.
file static class BackgroundServiceSupervisorConstants
{
    public static readonly TimeSpan[] BackoffSequence =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16),
        TimeSpan.FromSeconds(30),
    ];

    public static readonly TimeSpan HealthyResetThreshold = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Per-service supervisor. Owns the restart loop, exponential backoff, healthy-reset timer,
/// and lifecycle log emission. Polymorphic over <see cref="IBackgroundServiceStrategy"/> so
/// per-server and singleton services share the same loop logic.
/// </summary>
internal sealed class BackgroundServiceSupervisor<TContext>
    where TContext : DbContext
{
    private readonly WarpBackgroundService _service;
    private readonly IBackgroundServiceStrategy _strategy;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly TimeSpan _acquirePollInterval;
    private readonly ILogger _logger;
    private readonly BackgroundServiceLogCollector _collector;
    private readonly BackgroundServiceLifecycleLogger _lifecycleLogger;
    private readonly TimeSpan _logFlushInterval;
    private readonly IBackgroundServiceStatusObserver _statusObserver;

    public BackgroundServiceSupervisor(
        WarpBackgroundService service,
        IBackgroundServiceStrategy strategy,
        IServiceScopeFactory scopes,
        TimeProvider time,
        TimeSpan acquirePollInterval,
        TimeSpan logFlushInterval,
        ILogger logger,
        BackgroundServiceLogCollector collector,
        BackgroundServiceLifecycleLogger lifecycleLogger,
        IBackgroundServiceStatusObserver statusObserver)
    {
        _service = service;
        _strategy = strategy;
        _scopes = scopes;
        _time = time;
        _acquirePollInterval = acquirePollInterval;
        _logFlushInterval = logFlushInterval;
        _logger = logger;
        _collector = collector;
        _lifecycleLogger = lifecycleLogger;
        _statusObserver = statusObserver;
    }

    public async Task RunAsync(CancellationToken hostStoppingToken)
    {
        _collector.Start(_logFlushInterval);
        var serviceName = _service.Name;

        try
        {
            await RunInternalAsync(hostStoppingToken);
        }
        finally
        {
            // The supervisor flushes the collector before its own DeleteAsync call so logs from
            // normal runtime (non-shutdown) are persisted before the instance row is removed.
            // NOTE: during host graceful shutdown, BackgroundServiceHost.StopAsync issues a
            // fire-and-forget DELETE of @me's Instance + Lease rows BEFORE awaiting supervisor
            // completion (the failover-speed lever). Final log entries enqueued during shutdown
            // may hit FK-violation noise in the collector's flush; the violations are caught and
            // don't propagate, but a small number of shutdown-time log entries may be silently
            // dropped. This is the accepted trade-off — fast failover beats capturing the last
            // few shutdown lines.
            await _collector.StopAsync(CancellationToken.None);
            await _collector.DisposeAsync();
            await TryDeleteAsync(serviceName);
        }
    }

    private async Task RunInternalAsync(CancellationToken hostStoppingToken)
    {
        // Step 1: Register with the state service. If the scope doesn't match the definition,
        // sit out forever — don't invoke user code until the deploy is resolved.
        var outcome = await TryRegisterAsync(hostStoppingToken);
        if (outcome == null)
        {
            // Registration itself failed (e.g. host stopped during startup). Exit cleanly.
            return;
        }

        if (outcome == RegistrationOutcome.ConfigurationMismatch)
        {
            _logger.LogError(
                "BackgroundService {Name} has a scope mismatch; supervisor will not start until the deploy is resolved",
                _service.Name);

            // Query the stored scope so the lifecycle log shows the conflict clearly.
            // This runs once on the mismatch path, so the extra round-trip is acceptable.
            var storedScope = await TryGetDefinedScopeAsync(_service.Name, hostStoppingToken);
            _lifecycleLogger.LogConfigurationMismatch(_service.Scope, storedScope ?? _service.Scope);

            await hostStoppingToken.WhenCancelledAsync();
            _lifecycleLogger.LogStopped();

            return;
        }

        // Step 2: Main restart loop.
        //
        // Each iteration is wrapped in a supervisor-fault catch: if any of the supervisor's
        // own DB writes (SetStatus, RecordFault, ResetRestartCount) throws, the iteration is
        // treated as faulted — log an Error, emit a SupervisorFault lifecycle entry, advance
        // backoff, and retry the iteration. Service execution is gated on the supervisor's
        // ability to persist status: dashboard truthfulness is preferred over running blind.
        // The user-fault counter (RestartCount) is NOT incremented for supervisor faults.
        var backoffIndex = 0;
        var attempt = 0;

        while (!hostStoppingToken.IsCancellationRequested)
        {
            BackgroundServiceExecutionScope? executionScope = null;
            try
            {
                executionScope = await _strategy.AcquireAsync(hostStoppingToken);

                if (executionScope == null)
                {
                    // Singleton waiting for the lease — set Waiting status and poll.
                    await SetStatusAsync(_service.Name, BackgroundServiceStatus.Waiting, hostStoppingToken);
                    await Task.Delay(_acquirePollInterval, hostStoppingToken);

                    continue;
                }

                // Acquired — set Running status and emit lifecycle log.
                await SetStatusAsync(_service.Name, BackgroundServiceStatus.Running, hostStoppingToken);

                if (_strategy.Scope == ServiceScope.Singleton)
                {
                    _lifecycleLogger.LogLeaseAcquired();
                }
                else
                {
                    _lifecycleLogger.LogStarted();
                }

                var startedAt = _time.GetUtcNow();
                var faultException = default(Exception?);
                var leaseLost = false;

                WarpTelemetry.BackgroundServicesStarted.Add(1, new KeyValuePair<string, object?>("service_name", _service.Name));

                try
                {
                    await _service.InvokeExecuteAsync(executionScope.Token);

                    // User code returned without cancellation — treat as fault.
                    faultException = new InvalidOperationException(
                        $"{_service.Name}.ExecuteAsync returned without cancellation. " +
                        "WarpBackgroundService must run until its CancellationToken is cancelled.");
                }
                catch (OperationCanceledException) when (hostStoppingToken.IsCancellationRequested)
                {
                    // Graceful host shutdown — let it bubble to the outer catch.
                    throw;
                }
                catch (OperationCanceledException) when (
                    executionScope.Token.IsCancellationRequested
                    && !hostStoppingToken.IsCancellationRequested)
                {
                    // Lease was lost (singleton: internal CTS fired). Release scope and restart.
                    leaseLost = true;
                }
                catch (Exception ex)
                {
                    faultException = ex;
                }

                // Release the scope (unsubscribes signal, disposes linked CTS, releases lease).
                // Null out the local so the iteration's finally block doesn't double-release.
                await TryReleaseAsync(executionScope);
                executionScope = null;

                if (leaseLost)
                {
                    _lifecycleLogger.LogLeaseLost("lease lost between heartbeats");

                    // Emit Faulted then Waiting so the dashboard timeline shows the full
                    // Running → Faulted → Waiting transition deterministically.
                    // Without the Waiting write the next AcquireAsync may immediately re-acquire
                    // and the timeline jumps from Faulted straight to Running.
                    await SetStatusAsync(_service.Name, BackgroundServiceStatus.Faulted, hostStoppingToken);
                    await SetStatusAsync(_service.Name, BackgroundServiceStatus.Waiting, hostStoppingToken);

                    // Re-enter acquire loop as a waiter — backoff resets so the lease attempt is immediate.
                    backoffIndex = 0;
                    attempt = 0;
                    continue;
                }

                if (faultException != null)
                {
                    WarpTelemetry.BackgroundServicesFaulted.Add(
                        1,
                        new KeyValuePair<string, object?>("service_name", _service.Name),
                        new KeyValuePair<string, object?>("exception_type", faultException.GetType().Name));
                    _lifecycleLogger.LogFaulted(faultException);
                    _logger.LogError(faultException, "BackgroundService {Name} faulted", _service.Name);
                    await RecordFaultAsync(_service.Name, faultException, hostStoppingToken);
                }

                // Healthy-reset check: if the service ran for ≥5 min before this fault, reset
                // the backoff — it was a transient blip on an otherwise healthy service.
                //
                // ORDER CONTRACT: RecordFaultAsync (above) increments RestartCount BEFORE this
                // block clears it. HealthyResetTests asserts RestartCount == 1 after a second
                // fault, which only holds if the order stays Record→Reset (not Reset→Record).
                // If you reorder these, update HealthyResetTestsBase's assertion accordingly.
                var ranFor = _time.GetUtcNow() - startedAt;
                if (ranFor >= BackgroundServiceSupervisorConstants.HealthyResetThreshold)
                {
                    await ResetRestartCountAsync(_service.Name, hostStoppingToken);
                    backoffIndex = 0;
                }

                var backoffSequence = BackgroundServiceSupervisorConstants.BackoffSequence;
                var backoff = backoffSequence[Math.Min(backoffIndex, backoffSequence.Length - 1)];
                backoffIndex++;
                attempt++;

                await SetStatusAsync(_service.Name, BackgroundServiceStatus.Restarting, hostStoppingToken);
                _lifecycleLogger.LogRestarting(attempt, backoff);

                WarpTelemetry.BackgroundServicesRestarts.Add(1, new KeyValuePair<string, object?>("service_name", _service.Name));

                await Task.Delay(backoff, _time, hostStoppingToken);
            }
            catch (OperationCanceledException) when (hostStoppingToken.IsCancellationRequested)
            {
                _lifecycleLogger.LogStopped();
                break;
            }
            catch (Exception ex)
            {
                // Supervisor-side fault — typically a DB write that failed (timeout under load,
                // pool exhaustion, transient connection error). Log an Error, emit a
                // SupervisorFault lifecycle entry, advance backoff, and retry the iteration.
                // RestartCount stays untouched: it counts user-code faults, not infrastructure ones.
                _logger.LogError(ex, "BackgroundService {Name}: supervisor iteration faulted", _service.Name);
                _lifecycleLogger.LogSupervisorFault(ex);

                WarpTelemetry.BackgroundServicesFaulted.Add(
                    1,
                    new KeyValuePair<string, object?>("service_name", _service.Name),
                    new KeyValuePair<string, object?>("exception_type", ex.GetType().Name));

                var backoffSequence = BackgroundServiceSupervisorConstants.BackoffSequence;
                var backoff = backoffSequence[Math.Min(backoffIndex, backoffSequence.Length - 1)];
                backoffIndex++;

                try
                {
                    // Supervisor-fault recovery uses REAL time, not `_time`. The injected
                    // TimeProvider exists so user-fault backoff (1s, 2s, 4s...) can be compressed
                    // by fake-time tests, but supervisor faults are infrastructure-recovery delays
                    // (DB blip, pool exhaustion) that should pass on the wall clock regardless of
                    // what fake-time the test is driving for its own choreography.
                    await Task.Delay(backoff, hostStoppingToken);
                }
                catch (OperationCanceledException)
                {
                    _lifecycleLogger.LogStopped();
                    break;
                }
            }
            finally
            {
                // Release the execution scope if the iteration body threw before the inline
                // release call. Prevents singleton-lease leaks when a status write fails
                // between AcquireAsync and the explicit release point.
                if (executionScope != null)
                {
                    await TryReleaseAsync(executionScope);
                }
            }
        }

        // Note: instance row deletion is handled by RunAsync after the collector is flushed,
        // to ensure log entries are persisted before the instance FK is removed.
    }

    private async Task<RegistrationOutcome?> TryRegisterAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var stateService = scope.ServiceProvider.GetRequiredService<IBackgroundServiceStateService>();

                return await stateService.RegisterAsync(_service.Name, _service.Scope, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "BackgroundService {Name} registration failed; retrying in 1s", _service.Name);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    // Status / fault / reset writes propagate exceptions to the supervisor's outer iteration
    // catch. The observer is only invoked after the DB write succeeds — observers can rely on
    // "if I was called, the transition was persisted." This is the contract documented on
    // IBackgroundServiceStatusObserver.
    private async Task SetStatusAsync(string name, BackgroundServiceStatus status, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var stateService = scope.ServiceProvider.GetRequiredService<IBackgroundServiceStateService>();
        await stateService.SetStatusAsync(name, status, ct);

        try
        {
            _statusObserver.OnStatusChanged(name, status);
        }
        catch (Exception ex)
        {
            // A misbehaving observer must not take the supervisor down.
            _logger.LogWarning(ex, "BackgroundService {Name}: status observer threw on transition to {Status}", name, status);
        }
    }

    private async Task RecordFaultAsync(string name, Exception ex, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var stateService = scope.ServiceProvider.GetRequiredService<IBackgroundServiceStateService>();
        await stateService.RecordFaultAsync(name, ex, ct);
    }

    private async Task ResetRestartCountAsync(string name, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var stateService = scope.ServiceProvider.GetRequiredService<IBackgroundServiceStateService>();
        await stateService.ResetRestartCountAsync(name, ct);
    }

    private async Task<ServiceScope?> TryGetDefinedScopeAsync(string name, CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var stateService = scope.ServiceProvider.GetRequiredService<IBackgroundServiceStateService>();

            return await stateService.GetDefinedScopeAsync(name, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "BackgroundService {Name}: failed to query stored scope for mismatch log", name);

            return null;
        }
    }

    private async Task TryDeleteAsync(string name)
    {
        using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            using var scope = _scopes.CreateScope();
            var stateService = scope.ServiceProvider.GetRequiredService<IBackgroundServiceStateService>();
            await stateService.DeleteAsync(name, cleanupCts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BackgroundService {Name}: failed to delete instance row on shutdown", name);
        }
    }

    private async Task TryReleaseAsync(BackgroundServiceExecutionScope scope)
    {
        if (scope.Release == null)
        {
            return;
        }

        try
        {
            await scope.Release.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BackgroundService {Name}: error releasing execution scope", _service.Name);
        }
    }
}

file static class CancellationTokenExtensions
{
    internal static Task WhenCancelledAsync(this CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), tcs);

        return tcs.Task;
    }
}
