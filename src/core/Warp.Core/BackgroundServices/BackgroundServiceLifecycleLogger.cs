using Microsoft.Extensions.Logging;

namespace Warp.Core.BackgroundServices;

/// <summary>
/// Emits structured lifecycle events into the per-instance
/// <see cref="IBackgroundServiceLogCollector"/> with <c>Source = Lifecycle</c>.
/// Called by the supervisor at well-known transition points so operators can see
/// <c>Started</c>, <c>Faulted</c>, <c>Restarting</c>, etc. in the dashboard log tail.
/// </summary>
public sealed class BackgroundServiceLifecycleLogger
{
    private readonly IBackgroundServiceLogCollector _collector;

    public BackgroundServiceLifecycleLogger(IBackgroundServiceLogCollector collector)
    {
        _collector = collector;
    }

    /// <summary>Emits a <c>Lifecycle / Information</c> row: the service has started executing.</summary>
    public void LogStarted()
    {
        _collector.Enqueue(BackgroundServiceLogSource.Lifecycle, LogLevel.Information, "Service started", null);
    }

    /// <summary>
    /// Emits a <c>Lifecycle / Information</c> row: the singleton lease was acquired by this
    /// server and the service is entering <c>ExecuteAsync</c>.
    /// </summary>
    public void LogLeaseAcquired()
    {
        _collector.Enqueue(BackgroundServiceLogSource.Lifecycle, LogLevel.Information, "Singleton lease acquired", null);
    }

    /// <summary>
    /// Emits a <c>Lifecycle / Warning</c> row: the singleton lease was lost (heartbeat missed,
    /// lease expired, or forcibly stolen).
    /// </summary>
    public void LogLeaseLost(string reason)
    {
        _collector.Enqueue(BackgroundServiceLogSource.Lifecycle, LogLevel.Warning, $"Singleton lease lost: {reason}", null);
    }

    /// <summary>
    /// Emits a <c>Lifecycle / Error</c> row: <c>ExecuteAsync</c> threw an unhandled exception
    /// (or returned without cancellation, which is treated as a fault).
    /// </summary>
    public void LogFaulted(Exception ex)
    {
        _collector.Enqueue(BackgroundServiceLogSource.Lifecycle, LogLevel.Error, $"Service faulted: {ex.GetType().Name}: {ex.Message}", ex);
    }

    /// <summary>
    /// Emits a <c>Lifecycle / Error</c> row: a supervisor-side operation (status write, fault
    /// record, restart-count reset) failed. Distinct from <see cref="LogFaulted"/>, which is for
    /// failures originating in user code. Treated by the supervisor as a faulted iteration:
    /// backoff applies and the next iteration retries the whole cycle.
    /// </summary>
    public void LogSupervisorFault(Exception ex)
    {
        _collector.Enqueue(
            BackgroundServiceLogSource.Lifecycle,
            LogLevel.Error,
            $"Supervisor faulted: {ex.GetType().Name}: {ex.Message}",
            ex);
    }

    /// <summary>
    /// Emits a <c>Lifecycle / Warning</c> row: the supervisor is waiting
    /// <paramref name="delay"/> before the next <c>ExecuteAsync</c> invocation.
    /// </summary>
    public void LogRestarting(int attempt, TimeSpan delay)
    {
        _collector.Enqueue(
            BackgroundServiceLogSource.Lifecycle,
            LogLevel.Warning,
            $"Service restarting (attempt {attempt}); backoff delay {delay.TotalSeconds:F1}s",
            null);
    }

    /// <summary>Emits a <c>Lifecycle / Information</c> row: the service has stopped cleanly.</summary>
    public void LogStopped()
    {
        _collector.Enqueue(BackgroundServiceLogSource.Lifecycle, LogLevel.Information, "Service stopped", null);
    }

    /// <summary>
    /// Emits a <c>Lifecycle / Error</c> row: the service's declared
    /// <see cref="ServiceScope"/> does not match the value stored in the
    /// <c>BackgroundServiceDefinition</c> row. The supervisor will not start the service.
    /// </summary>
    public void LogConfigurationMismatch(ServiceScope declaredScope, ServiceScope storedScope)
    {
        _collector.Enqueue(
            BackgroundServiceLogSource.Lifecycle,
            LogLevel.Error,
            $"Configuration mismatch: declared scope is {declaredScope} but definition row has {storedScope}; service will not start until the deploy is resolved",
            null);
    }
}
