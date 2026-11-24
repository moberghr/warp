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

/// <summary>The cluster task scheduler's timings (rule 6.20); outside the generic type so each is one value.</summary>
internal static class ClusterTaskTimings
{
    /// <summary>A signal claims or requests a task at most this often; signals in between fold into it.</summary>
    public static readonly TimeSpan SignalCoalescing = TimeSpan.FromMilliseconds(250);

    /// <summary>An idle run records itself on the row at most this often; a change, a message or a failure always does.</summary>
    public static readonly TimeSpan IdleRecordInterval = TimeSpan.FromMinutes(1);

    /// <summary>The loop never sleeps longer than this, so a dashboard edit of an interval or a lost wake is picked up within it.</summary>
    public static readonly TimeSpan MaxSleep = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Runs the cluster-wide server tasks (those with a lock key) once per tick for the whole cluster (rule
/// 6.20). One loop per server claims every due task in one statement with a lease on its
/// <see cref="ClusterTask"/> row; only the server holding a task's lease runs it, so the other servers
/// spend nothing on it, where each used to take the task's lock every interval. A run that lasts longer
/// than a third of its interval renews its lease; a server that dies mid-run leaves the task to the next
/// claim once the lease lapses. Signals (a job finalized, a message enqueued) run a task at once, or ask
/// the server holding it to run it again. While a server from before the lease is alive, each run also
/// takes the task's old lock, so a task never runs on an old and a new server at once during a deploy.
/// </summary>
internal sealed class ClusterTaskScheduler<TContext> : IDisposable
    where TContext : DbContext
{
    private readonly Dictionary<string, Entry> _entries;
    private readonly IServiceScopeFactory _scopes;
    private readonly IWarpLockProvider _lockProvider;
    private readonly TimeProvider _time;
    private readonly Guid _serverId;
    private readonly ILogger _logger;
    private readonly TimeSpan _startupDelay;
    private readonly TimeSpan _liveWindow;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Lock _gate = new();
    private volatile bool _legacyServersAlive;

    public ClusterTaskScheduler(
        IEnumerable<IServerTask> templates,
        IServiceScopeFactory scopes,
        IWarpLockProvider lockProvider,
        TimeProvider time,
        Guid serverId,
        ILogger logger,
        TimeSpan startupDelay,
        TimeSpan liveWindow)
    {
        _entries = templates.ToDictionary(x => x.Name, x => new Entry(x), StringComparer.Ordinal);
        _scopes = scopes;
        _lockProvider = lockProvider;
        _time = time;
        _serverId = serverId;
        _logger = logger;
        _startupDelay = startupDelay;
        _liveWindow = liveWindow;
    }

    public bool HasTasks => _entries.Count > 0;

    public IEnumerable<(string Name, IReadOnlyList<ServerTaskSignal> Signals)> Subscriptions =>
        _entries.Values.Select(x => (x.Name, (IReadOnlyList<ServerTaskSignal>)x.Signals));

    /// <summary>A signal for <paramref name="name"/>: run it at once here, or ask the server holding it to run it again.</summary>
    public void Signal(string name)
    {
        if (!_entries.TryGetValue(name, out var entry))
        {
            return;
        }

        lock (_gate)
        {
            entry.Signaled = true;
        }

        Wake();
    }

    /// <summary>Creates the rows of tasks the cluster has not seen and applies this server's interval and backoff limit to all of them.</summary>
    internal async Task RegisterAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<IWarpServerContext>().Context;
                var names = _entries.Keys.ToList();
                var existing = await context.Set<ClusterTask>()
                    .Where(x => names.Contains(x.Name))
                    .ToDictionaryAsync(x => x.Name, StringComparer.Ordinal, ct);
                foreach (var entry in _entries.Values)
                {
                    if (!existing.TryGetValue(entry.Name, out var row))
                    {
                        // Due at once, whatever this server's clock says: the lease times are the database's (rule 6.20).
                        row = new ClusterTask { Name = entry.Name, NextRunAt = DateTime.UnixEpoch };
                        context.Set<ClusterTask>().Add(row);
                    }

                    // The latest server to start sets the schedule, as each server set its own per-server
                    // row before; a dashboard edit holds until the next deploy.
                    row.IntervalSeconds = entry.Interval.TotalSeconds;
                }

                await context.SaveChangesAsync(ct);

                return;
            }
            catch (DbUpdateException) when (attempt++ < 3)
            {
                // Another server inserted the same task row first; read it and update it instead.
            }
        }
    }

    internal async Task RunAsync(CancellationToken ct)
    {
        await RegisterWithRetryAsync(ct);

        SpreadFirstRuns(Random.Shared.NextDouble);

        while (!ct.IsCancellationRequested)
        {
            TimeSpan sleep;
            try
            {
                sleep = await PassAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cluster task scheduling pass failed");
                sleep = TimeSpan.FromSeconds(1);
            }

            await WaitAsync(sleep, ct);
        }

        Task[] running;
        lock (_gate)
        {
            running = [.. _entries.Values.Select(x => x.Running).OfType<Task>()];
        }

        await Task.WhenAll(running);
    }

    // Spread the first runs (rule 6.18): each task waits its own random part of the startup delay, as its
    // per-server loop did, and a signal ends only that task's wait. One wait shared by every task let the
    // first job's signal run them all at once. Internal for tests, which pass the sample.
    internal void SpreadFirstRuns(Func<double> sample)
    {
        var start = _time.GetUtcNow().UtcDateTime;
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                entry.NotBefore = start + ServerTaskLoop<TContext>.FirstRunDelay(entry.Interval, _startupDelay, sample());
            }
        }
    }

    // One scheduling pass: honour signals, claim every due task, start what was claimed, and say how
    // long to sleep before the next task falls due. Internal for tests, which drive passes one at a time.
    internal async Task<TimeSpan> PassAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var sleep = ClusterTaskTimings.MaxSleep;

        foreach (var entry in _entries.Values)
        {
            bool signaled;
            lock (_gate)
            {
                signaled = entry.Signaled;
                if (!signaled)
                {
                    continue;
                }

                if (entry.Running != null)
                {
                    // Running here: run again right after, no database write needed.
                    entry.Signaled = false;
                    entry.RerunRequested = true;
                    continue;
                }

                var since = now - entry.LastSignalClaimAt;
                if (since < ClusterTaskTimings.SignalCoalescing)
                {
                    sleep = Min(sleep, ClusterTaskTimings.SignalCoalescing - since);
                    continue;
                }

                entry.Signaled = false;
                entry.LastSignalClaimAt = now;
                entry.NotBefore = DateTime.MinValue;
            }

            if (await RequestAsync(entry.Name, ct))
            {
                Start(entry, ct);
            }
        }

        string[] idle;
        lock (_gate)
        {
            idle = [.. _entries.Values.Where(x => x.Running == null && x.NotBefore <= now).Select(x => x.Name)];
            foreach (var waiting in _entries.Values.Where(x => x.NotBefore > now))
            {
                sleep = Min(sleep, waiting.NotBefore - now);
            }
        }

        if (idle.Length == 0)
        {
            return sleep;
        }

        ClusterTaskClaims claims;
        using (var scope = _scopes.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<IWarpServerContext>().Context;
            var queries = scope.ServiceProvider.GetRequiredService<IWarpSqlQueries<TContext>>();
            claims = await queries.ClaimClusterTasksAsync(context, idle, _serverId, _liveWindow, ct);
        }

        _legacyServersAlive = claims.LegacyServersAlive;
        foreach (var state in claims.Tasks)
        {
            if (state.Claimed)
            {
                Start(_entries[state.Name], ct);
                continue;
            }

            sleep = Min(sleep, state.DueIn);
        }

        return sleep < TimeSpan.Zero ? TimeSpan.Zero : sleep;
    }

    // Internal for tests: the run of a task started by the last pass, if it is still running.
    internal Task? RunningTask(string name)
    {
        lock (_gate)
        {
            return _entries[name].Running;
        }
    }

    private void Start(Entry entry, CancellationToken ct)
    {
        lock (_gate)
        {
            entry.Running = Task.Run(() => RunTaskAsync(entry, ct), CancellationToken.None);
        }
    }

    // One run of a claimed task, with its lease renewed while it lasts, then its completion.
    private async Task RunTaskAsync(Entry entry, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        using var span = WarpTelemetry.StartServerTaskActivity(entry.Name);
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var stopRenewing = new CancellationTokenSource();
        var renewal = RenewWhileRunningAsync(entry, run, stopRenewing.Token);

        string? message = null;
        Exception? failure = null;
        var ran = true;
        try
        {
            (ran, message) = await ExecuteAsync(entry, run.Token);
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
            ran = false;
        }
        catch (Exception ex)
        {
            failure = ex;
            _logger.LogError(ex, "Server task {Name} failed", entry.Name);

            // Exception type name only on the span: messages can carry user data (rule 1.2).
            span?.SetStatus(ActivityStatusCode.Error, WarpTelemetry.TruncateMessage(ex.Message, 256));
            span?.SetTag(WarpTelemetryAttributes.ErrorType, ex.GetType().FullName);
        }
        finally
        {
            await stopRenewing.CancelAsync();
            await renewal;
        }

        try
        {
            span?.SetTag(WarpTelemetryAttributes.WarpTaskLockHeld, ran);
            if (message != null)
            {
                span?.SetTag(WarpTelemetryAttributes.WarpTaskMessage, message);
            }

            if (ran && !ct.IsCancellationRequested)
            {
                await FinishAsync(entry, message, failure, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
        catch (Exception ex)
        {
            // The lease lapses on its own; the next claim after it runs the task again.
            _logger.LogWarning(ex, "Failed to complete server task {Name}", entry.Name);
        }
        finally
        {
            lock (_gate)
            {
                entry.Running = null;
            }

            Wake();
        }
    }

    private async Task<(bool Ran, string? Message)> ExecuteAsync(Entry entry, CancellationToken ct)
    {
        if (!entry.LocksWithTransaction)
        {
            // A task that manages its own transactions (MessageRouter). It needs no lock of its own under
            // the lease; while an older server is alive it takes the session lock that server takes.
            IAsyncDisposable? handle = null;
            if (_legacyServersAlive)
            {
                handle = await _lockProvider.TryAcquireAsync(entry.LockKey, TimeSpan.Zero, ct);
                if (handle == null)
                {
                    return (false, null);
                }
            }

            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var task = Resolve(scope, entry);
                var message = await task.ExecuteAsync(ct);
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

        await using (var scope = _scopes.CreateAsyncScope())
        {
            var task = Resolve(scope, entry);
            var context = scope.ServiceProvider.GetRequiredService<IWarpServerContext>().Context;

            if (_legacyServersAlive)
            {
                // An older server still takes this lock every interval: take it too, around the same
                // transaction, so the two never run the task at once.
                var queries = scope.ServiceProvider.GetRequiredService<IWarpSqlQueries<TContext>>();
                var outcome = await queries.RunUnderTransactionLockAsync<string?>(
                    context,
                    entry.LockKey,
                    async (_, innerCt) => await task.ExecuteAsync(innerCt),
                    ct);
                if (!outcome.LockHeld)
                {
                    return (false, null);
                }

                await task.OnCommittedAsync(ct);

                return (true, outcome.Result);
            }

            // The task runs in one transaction, as it did under its lock (every statement of a pass commits
            // together, and OnCommittedAsync still runs after the commit), without the lock: the lease
            // already keeps every other server out.
            string? result;
            await using (var transaction = await context.Database.BeginTransactionAsync(ct))
            {
                result = await task.ExecuteAsync(ct);
                await transaction.CommitAsync(ct);
            }

            await task.OnCommittedAsync(ct);

            return (true, result);
        }
    }

    private async Task FinishAsync(Entry entry, string? message, Exception? failure, double durationMs)
    {
        var now = _time.GetUtcNow();
        var status = failure == null ? "Completed" : "Failed";
        var didWork = message != null || failure != null;
        bool rerun;
        bool record;
        lock (_gate)
        {
            rerun = entry.RerunRequested || (message != null && entry.RerunImmediately);
            entry.RerunRequested = false;
            record = didWork
                || !string.Equals(status, entry.LastRecordedStatus, StringComparison.Ordinal)
                || now - entry.LastRecordedAt >= ClusterTaskTimings.IdleRecordInterval;
        }

        // An idle run that has nothing to record writes nothing: its lease lapses as the task falls due.
        if (didWork || rerun || record)
        {
            using var scope = _scopes.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<IWarpServerContext>().Context;
            var queries = scope.ServiceProvider.GetRequiredService<IWarpSqlQueries<TContext>>();
            await queries.CompleteClusterTaskAsync(
                context,
                new ClusterTaskCompletion(
                    entry.Name,
                    _serverId,
                    didWork,
                    rerun,
                    record,
                    status,
                    failure?.Message ?? message,
                    durationMs),
                CancellationToken.None);

            if (record)
            {
                lock (_gate)
                {
                    entry.LastRecordedStatus = status;
                    entry.LastRecordedAt = now;
                }
            }
        }

        // Only a run that did something leaves a log row (as on the per-server loop).
        if (failure != null || (entry.LogOnSuccess && message != null))
        {
            await WriteServerLogAsync(entry.Name, status, failure?.Message ?? message, durationMs);
        }
    }

    private async Task RenewWhileRunningAsync(Entry entry, CancellationTokenSource run, CancellationToken stop)
    {
        var every = entry.Interval / 3;
        if (every < TimeSpan.FromSeconds(1))
        {
            every = TimeSpan.FromSeconds(1);
        }

        try
        {
            while (true)
            {
                await Task.Delay(every, _time, stop);
                using var scope = _scopes.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<IWarpServerContext>().Context;
                var queries = scope.ServiceProvider.GetRequiredService<IWarpSqlQueries<TContext>>();
                var held = await queries.RenewClusterTaskLeaseAsync(context, entry.Name, _serverId, stop);
                if (!held)
                {
                    // Another server has it now (this one stalled past its lease): stop, so it is not run twice.
                    _logger.LogWarning("Server task {Name} lost its lease while running; cancelling this run", entry.Name);
                    await run.CancelAsync();

                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The run ended.
        }
        catch (Exception ex)
        {
            // The next renewal retries; if every one fails the lease lapses and another server takes over.
            _logger.LogWarning(ex, "Failed to renew the lease of server task {Name}", entry.Name);
        }
    }

    private async Task<bool> RequestAsync(string name, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IWarpServerContext>().Context;
        var queries = scope.ServiceProvider.GetRequiredService<IWarpSqlQueries<TContext>>();

        return await queries.RequestClusterTaskRunAsync(context, name, _serverId, ct);
    }

    private async Task WriteServerLogAsync(string name, string status, string? message, double durationMs)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<IWarpServerContext>().Context;
            context.Set<ServerLog>().Add(new ServerLog
            {
                ServerId = _serverId,
                TaskName = name,
                Status = status,
                Message = message,
                DurationMs = durationMs,
                Timestamp = _time.GetUtcNow().UtcDateTime,
            });
            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write ServerLog for {Name}", name);
        }
    }

    private async Task RegisterWithRetryAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RegisterAsync(ct);

                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Registering cluster tasks failed; retrying");
                await WaitAsync(TimeSpan.FromSeconds(1), ct);
            }
        }
    }

    private static IServerTask Resolve(AsyncServiceScope scope, Entry entry) =>
        scope.ServiceProvider
            .GetServices<IServerTask>()
            .First(x => x.GetType() == entry.TaskType);

    private void Wake()
    {
        lock (_gate)
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
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

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    public void Dispose() => _wake.Dispose();

    private sealed class Entry(IServerTask template)
    {
        public Type TaskType { get; } = template.GetType();

        public string Name { get; } = template.Name;

        public string LockKey { get; } = template.LockKey!;

        public bool LocksWithTransaction { get; } = template.LocksWithTransaction;

        public TimeSpan Interval { get; } = template.DefaultInterval!.Value;

        public bool RerunImmediately { get; } = template.RerunImmediately;

        public bool LogOnSuccess { get; } = template.LogOnSuccess;

        public ServerTaskSignal[] Signals { get; } = [.. template.Signals];

        public Task? Running { get; set; }

        public bool Signaled { get; set; }

        public bool RerunRequested { get; set; }

        public DateTime LastSignalClaimAt { get; set; } = DateTime.MinValue;

        // Before this the task is not claimed on schedule: its share of the startup delay (rule 6.18).
        public DateTime NotBefore { get; set; } = DateTime.MinValue;

        public string? LastRecordedStatus { get; set; }

        public DateTimeOffset LastRecordedAt { get; set; } = DateTimeOffset.MinValue;
    }
}
