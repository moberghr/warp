using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Logging;

namespace Warp.Worker.Services;

/// <summary>
/// Runs a task every server runs for itself (no lock key: the heartbeat) on its interval. It is not part
/// of the lease scheduler (rule 6.20): there is nothing to claim, the server just does it. Wait the
/// interval, run, and log a failure; the next interval tries again. A signal ends the wait at once.
/// <para>
/// Nothing is recorded for a run that succeeds: what the heartbeat does is its record
/// (<c>Server.LastHeartbeatTime</c>). A failure, or a run that returns a message from a task that logs
/// on success, writes a <see cref="ServerLog"/> row with the task's name.
/// </para>
/// </summary>
internal sealed class PerServerTaskLoop<TContext> : IDisposable
    where TContext : DbContext
{
    private readonly ServerTaskInfo _info;
    private readonly ServerTaskRunner<TContext> _runner;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly Guid _serverId;
    private readonly ILogger _logger;
    private readonly TimeSpan _startupDelay;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Lock _gate = new();

    public PerServerTaskLoop(
        ServerTaskInfo info,
        ServerTaskRunner<TContext> runner,
        IServiceScopeFactory scopes,
        TimeProvider time,
        Guid serverId,
        ILogger logger,
        TimeSpan startupDelay)
    {
        _info = info;
        _runner = runner;
        _scopes = scopes;
        _time = time;
        _serverId = serverId;
        _logger = logger;
        _startupDelay = startupDelay;
    }

    public IReadOnlyList<Warp.Core.Events.ServerTaskSignal> Signals => _info.Signals;

    /// <summary>Ends the current wait, so the task runs now.</summary>
    public void Signal()
    {
        lock (_gate)
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // The first run waits a random part of the startup delay (rule 6.18); a signal ends the wait.
        var wait = ClusterTaskTimings.FirstRunDelay(_info.Interval, _startupDelay, Random.Shared.NextDouble());
        while (!ct.IsCancellationRequested)
        {
            if (wait > TimeSpan.Zero)
            {
                await WaitAsync(wait, ct);
            }

            if (ct.IsCancellationRequested)
            {
                return;
            }

            var started = Stopwatch.GetTimestamp();
            var didWork = await RunOnceAsync(ct);

            // The interval runs from the start of a run, so the heartbeat keeps its cadence however long a
            // beat takes; a run that did work on a task that reruns at once does not wait at all.
            var left = _info.Interval - Stopwatch.GetElapsedTime(started);
            if (left < ClusterTaskTimings.ClaimRetry)
            {
                left = ClusterTaskTimings.ClaimRetry;
            }

            wait = didWork && _info.RerunImmediately ? TimeSpan.Zero : left;
        }
    }

    // One run with its failure handling. Internal for tests. Returns whether the task reported work.
    internal async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        using var span = WarpTelemetry.StartServerTaskActivity(_info.Name);

        // Kept for trace readers: a per-server task never competes, so it always "holds" its run.
        span?.SetTag(WarpTelemetryAttributes.WarpTaskLockHeld, true);
        try
        {
            var (_, message) = await _runner.RunAsync(_info, underLegacyLock: false, ct);
            if (message != null)
            {
                span?.SetTag(WarpTelemetryAttributes.WarpTaskMessage, message);
                if (_info.LogOnSuccess)
                {
                    await WriteServerLogAsync("Completed", message, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
            }

            return message != null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Server task {Name} failed", _info.Name);

            // Exception type name only on the span: messages can carry user data (rule 1.2).
            span?.SetStatus(ActivityStatusCode.Error, WarpTelemetry.TruncateMessage(ex.Message, 256));
            span?.SetTag(WarpTelemetryAttributes.ErrorType, ex.GetType().FullName);
            await WriteServerLogAsync("Failed", ex.Message, Stopwatch.GetElapsedTime(started).TotalMilliseconds);

            return false;
        }
    }

    public void Dispose() => _wake.Dispose();

    private async Task WriteServerLogAsync(string status, string? message, double durationMs)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<IWarpServerContext>().Context;
            context.Set<ServerLog>().Add(new ServerLog
            {
                ServerId = _serverId,
                TaskName = _info.Name,
                Status = status,
                Message = message,
                DurationMs = durationMs,
                Timestamp = _time.GetUtcNow().UtcDateTime,
            });
            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write ServerLog for {Name}", _info.Name);
        }
    }

    private async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await _wake.WaitAsync(timeout, ct);
        }
        catch (OperationCanceledException)
        {
            // Shutting down; the caller's token check ends the loop.
        }
    }
}
