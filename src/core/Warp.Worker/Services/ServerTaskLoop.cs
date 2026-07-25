using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Events;
using Warp.Core.Logging;

namespace Warp.Worker.Services;

// Non-generic holder so S2743 (static fields in generic types) isn't tripped by per-task
// constants that don't depend on TContext.
file static class ServerTaskLoopConstants
{
    // Cache the per-task IntervalSeconds DB read for IntervalCacheLifetime instead of
    // issuing a SELECT every iteration. Idle tasks (MessageRouter, etc.) loop frequently
    // and the DB value almost never changes — recovering the cost matters more than
    // catching dashboard edits within the second.
    public static readonly TimeSpan IntervalCacheLifetime = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Per-task loop driven by <see cref="ServerTaskHost{TContext}"/>. Owns the task's lifecycle
/// bookkeeping (ServerTask row registration, ServerLog writes), the signal semaphore, and
/// the lock + scope primitive that calls into <see cref="IServerTask.ExecuteAsync"/>.
/// </summary>
internal sealed class ServerTaskLoop<TContext> : IDisposable
    where TContext : DbContext
{
    private readonly Type _taskType;
    private readonly string _name;
    private readonly string? _lockKey;
    private readonly bool _locksWithTransaction;
    private readonly TimeSpan _defaultInterval;
    private readonly bool _rerunImmediately;
    private readonly bool _logOnSuccess;
    private readonly ServerTaskSignal[] _signals;

    private readonly IServiceScopeFactory _scopes;
    private readonly IWarpLockProvider _lockProvider;
    private readonly TimeProvider _time;
    private readonly Guid _serverId;
    private readonly ILogger _logger;

    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly Lock _signalLock = new();
    private int? _serverTaskId;

    private TimeSpan? _cachedInterval;
    private DateTimeOffset _intervalCachedAt = DateTimeOffset.MinValue;
    private bool _intervalCacheValid;

    public ServerTaskLoop(
        IServerTask template,
        IServiceScopeFactory scopes,
        IWarpLockProvider lockProvider,
        TimeProvider time,
        Guid serverId,
        ILogger logger)
    {
        _taskType = template.GetType();
        _name = template.Name;
        _lockKey = template.LockKey;
        _locksWithTransaction = template.LocksWithTransaction;
        _defaultInterval = template.DefaultInterval
            ?? throw new ArgumentException(
                $"Cannot build a loop for {template.GetType().Name}: DefaultInterval is null (auto-run disabled).",
                nameof(template));
        _rerunImmediately = template.RerunImmediately;
        _logOnSuccess = template.LogOnSuccess;
        _signals = [.. template.Signals];
        _scopes = scopes;
        _lockProvider = lockProvider;
        _time = time;
        _serverId = serverId;
        _logger = logger;
    }

    public string Name => _name;

    public Type TaskType => _taskType;

    public IReadOnlyList<ServerTaskSignal> Signals => _signals;

    /// <summary>
    /// Wake the loop's <see cref="WaitForNextRunAsync"/> — next iteration starts immediately.
    /// No-op if the semaphore already has a pending signal. The lock serializes concurrent
    /// callers so that two threads can't both observe <c>CurrentCount == 0</c> and both call
    /// <c>Release()</c> — which would exceed the max count of 1 and throw.
    /// (Same pattern as <c>WarpDispatcher.SignalAll</c>.)
    /// </summary>
    public void Signal()
    {
        lock (_signalLock)
        {
            if (_signal.CurrentCount == 0)
            {
                _signal.Release();
            }
        }
    }

    /// <summary>
    /// Scheduling loop. Registers the ServerTask row once, then alternates between running
    /// one iteration (see <see cref="RunOneIterationAsync"/>) and waiting for the next tick
    /// or wake signal. All bookkeeping + exception handling lives in the inner method; this
    /// one is pure control flow.
    /// </summary>
    internal async Task RunAsync(CancellationToken ct)
    {
        // Initial registration: if the DB is momentarily unhappy (transient SqlException —
        // "session busy" / "severe error" under load), don't let the exception escape and
        // crash the host (BackgroundServiceExceptionBehavior=StopHost is the default in
        // .NET 6+). Log and back off; the next iteration retries.
        while (!ct.IsCancellationRequested && _serverTaskId == null)
        {
            try
            {
                await EnsureRegisteredAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Server task {Name} registration failed; retrying", _name);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var interval = await GetIntervalAsync(ct);
                if (interval == null)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10), ct);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    continue;
                }

                var didWork = await RunOneIterationAsync(ct);
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                if (didWork && _rerunImmediately)
                {
                    continue;
                }

                await WaitForNextRunAsync(interval.Value, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Any transient failure outside RunOneIterationAsync (which has its own
                // bookkeeping + swallow). Logged here so a flaky DB connection during
                // GetIntervalAsync / WaitForNextRunAsync doesn't crash the host.
                _logger.LogError(ex, "Server task {Name} loop iteration failed", _name);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Runs the task once under its lock, writes the ServerTask / ServerLog bookkeeping, and
    /// swallows exceptions after logging them. Returns <c>true</c> when work was done (so the
    /// outer loop can re-run immediately if <see cref="IServerTask.RerunImmediately"/> is set),
    /// <c>false</c> otherwise.
    /// </summary>
    // Internal for tests: lets tests drive a single iteration with full bookkeeping.
    internal async Task<bool> RunOneIterationAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var taskSpan = WarpTelemetry.StartServerTaskActivity(_name);

        // Default optimistically — we either acquired the lock (or no lock was needed) for any
        // path that reaches the body. The catch block below treats lockHeld as true in the
        // exception-from-task case (the lock was held while the task threw).
        var lockHeld = true;
        try
        {
            string? message;
            (lockHeld, message) = await TryAcquireLockAndExecuteAsync(ct);
            var elapsed = sw.Elapsed.TotalMilliseconds;
            taskSpan?.SetTag(WarpTelemetryAttributes.WarpTaskLockHeld, lockHeld);

            if (!lockHeld)
            {
                return false;
            }

            if (message != null)
            {
                taskSpan?.SetTag(WarpTelemetryAttributes.WarpTaskMessage, message);
            }

            await TryUpdateServerTaskAsync("Completed", message, elapsed);
            if (_logOnSuccess)
            {
                await TryWriteServerLogAsync("Completed", message, elapsed);
            }

            return message != null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            taskSpan?.SetTag(WarpTelemetryAttributes.WarpTaskLockHeld, lockHeld);

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Server task {Name} failed", _name);

            // Exception type name only — messages can carry user data (CLAUDE.md §1.3).
            taskSpan?.SetStatus(ActivityStatusCode.Error, WarpTelemetry.TruncateMessage(ex.Message, 256));
            taskSpan?.SetTag(WarpTelemetryAttributes.ErrorType, ex.GetType().FullName);
            taskSpan?.SetTag(WarpTelemetryAttributes.WarpTaskLockHeld, lockHeld);
            var elapsed = sw.Elapsed.TotalMilliseconds;
            await TryUpdateServerTaskAsync("Failed", ex.Message, elapsed);
            await TryWriteServerLogAsync("Failed", ex.Message, elapsed);

            return false;
        }
    }

    /// <summary>
    /// Test-only entry point. Runs the task once inside its lock + scope with no bookkeeping
    /// and no swallowed exceptions. Test-triggered runs never write ServerTask/ServerLog rows,
    /// so the dashboard history reflects only auto-scheduled runs.
    /// </summary>
    internal async Task<string?> RunOnceAsync(CancellationToken ct)
    {
        var (_, message) = await TryAcquireLockAndExecuteAsync(ct);

        return message;
    }

    /// <summary>
    /// Acquires the distributed lock (if <see cref="_lockKey"/> is set), runs the task in a
    /// fresh scope, and returns both whether the lock was held and the task's result message.
    /// When <c>_lockKey</c> is null, <c>LockHeld</c> is always <c>true</c> (no lock required).
    /// </summary>
    private async Task<(bool LockHeld, string? Message)> TryAcquireLockAndExecuteAsync(CancellationToken ct)
    {
        // Transaction-scoped advisory lock path: collapses acquire + work + release into one
        // transaction. The task's ExecuteAsync runs against a context whose connection has the
        // xact-lock held; SaveChangesAsync joins the outer transaction (so the lock is released
        // automatically on COMMIT). Saves 2 round-trips per iteration vs. the session-lock path.
        if (_lockKey != null && _locksWithTransaction)
        {
            await using var scope = _scopes.CreateAsyncScope();
            var task = scope.ServiceProvider
                .GetServices<IServerTask>()
                .First(x => x.GetType() == _taskType);

            // The xact-lock must be held on the SAME context the task writes through (the scoped
            // server context — same instance the task resolved), so the task's SaveChanges joins the
            // lock transaction and commits atomically with it. Resolving TContext here would open the
            // lock on a different connection than the task's writes, defeating the atomicity.
            var ctx = scope.ServiceProvider.GetRequiredService<IWarpServerContext>().Context;
            var sqlQueries = scope.ServiceProvider.GetRequiredService<IWarpSqlQueries<TContext>>();

            var outcome = await sqlQueries.RunUnderTransactionLockAsync<string?>(
                ctx,
                _lockKey,
                async (_, innerCt) => await task.ExecuteAsync(innerCt),
                ct);

            // Post-commit hook: RunUnderTransactionLockAsync has committed the task's transaction, so the task
            // may now act on its committed work (e.g. dispatch operational notifications, §8.25). Only when the
            // lock was actually held (else the task didn't run); skipped on a throw (the transaction rolled back
            // and the exception already propagated).
            if (outcome.LockHeld)
            {
                await task.OnCommittedAsync(ct);
            }

            return (outcome.LockHeld, outcome.Result);
        }

        // Session-scoped (Medallion) path — required for tasks whose work spans multiple
        // transactions (e.g. MessageRouter looping through messages with per-message commits).
        IAsyncDisposable? handle = null;
        if (_lockKey != null)
        {
            handle = await _lockProvider.TryAcquireAsync(_lockKey, TimeSpan.Zero, ct);
            if (handle == null)
            {
                return (false, null);
            }
        }

        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var task = scope.ServiceProvider
                .GetServices<IServerTask>()
                .First(x => x.GetType() == _taskType);

            var message = await task.ExecuteAsync(ct);

            // Post-execute hook: the session-lock task manages its own transactions, so its work is committed
            // by the time ExecuteAsync returns. A throw skips this (propagates), so the hook never runs for
            // work that didn't complete.
            await task.OnCommittedAsync(ct);

            return (true, message);
        }
        finally
        {
            if (handle != null)
            {
                await handle.DisposeAsync();
            }
        }
    }

    private async Task EnsureRegisteredAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();

        var existing = await context.Set<ServerTask>()
            .Where(x => x.ServerId == _serverId)
            .Where(x => x.TaskName == _name)
            .FirstOrDefaultAsync(ct);

        if (existing != null)
        {
            existing.IntervalSeconds = _defaultInterval.TotalSeconds;
            await context.SaveChangesAsync(ct);
            _serverTaskId = existing.Id;

            return;
        }

        var entity = new ServerTask
        {
            ServerId = _serverId,
            TaskName = _name,
            IntervalSeconds = _defaultInterval.TotalSeconds,
        };
        context.Set<ServerTask>().Add(entity);
        await context.SaveChangesAsync(ct);
        _serverTaskId = entity.Id;
    }

    // Internal for tests: exercises the cache directly without driving the full RunAsync loop.
    internal async Task<TimeSpan?> GetIntervalAsync(CancellationToken ct)
    {
        if (_serverTaskId == null)
        {
            return null;
        }

        var now = _time.GetUtcNow();
        if (_intervalCacheValid && now - _intervalCachedAt < ServerTaskLoopConstants.IntervalCacheLifetime)
        {
            return _cachedInterval;
        }

        using var scope = _scopes.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();

        var seconds = await context.Set<ServerTask>()
            .Where(x => x.Id == _serverTaskId)
            .Select(x => x.IntervalSeconds)
            .FirstOrDefaultAsync(ct);

        _cachedInterval = seconds.HasValue ? TimeSpan.FromSeconds(seconds.Value) : null;
        _intervalCachedAt = now;
        _intervalCacheValid = true;

        return _cachedInterval;
    }

    // Internal for tests: lets tests seed _serverTaskId without running EnsureRegisteredAsync.
    internal void SetServerTaskIdForTest(int id) => _serverTaskId = id;

    private async Task WaitForNextRunAsync(TimeSpan interval, CancellationToken ct)
    {
        try
        {
            await _signal.WaitAsync(interval, ct);
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down; let the outer loop's token check exit cleanly.
        }
    }

    private async Task UpdateServerTaskAsync(string status, string? message, double durationMs)
    {
        if (_serverTaskId == null)
        {
            return;
        }

        using var scope = _scopes.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        var now = _time.GetUtcNow().UtcDateTime;

        await context.Set<ServerTask>()
            .Where(x => x.Id == _serverTaskId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LastStatus, status)
                .SetProperty(x => x.LastMessage, message)
                .SetProperty(x => x.LastRun, now)
                .SetProperty(x => x.LastDurationMs, durationMs));
    }

    // Internal for tests: exercises the wrapper directly without driving the full RunAsync loop.
    internal async Task TryUpdateServerTaskAsync(string status, string? message, double durationMs)
    {
        try
        {
            await UpdateServerTaskAsync(status, message, durationMs);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update ServerTask for {Name}", _name);
        }
    }

    private async Task WriteServerLogAsync(string status, string? message, double durationMs)
    {
        using var scope = _scopes.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();

        context.Set<ServerLog>().Add(new ServerLog
        {
            ServerId = _serverId,
            ServerTaskId = _serverTaskId,
            Status = status,
            Message = message,
            DurationMs = durationMs,
            Timestamp = _time.GetUtcNow().UtcDateTime,
        });
        await context.SaveChangesAsync();
    }

    private async Task TryWriteServerLogAsync(string status, string? message, double durationMs)
    {
        try
        {
            await WriteServerLogAsync(status, message, durationMs);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write ServerLog for {Name}", _name);
        }
    }

    public void Dispose() => _signal.Dispose();
}
