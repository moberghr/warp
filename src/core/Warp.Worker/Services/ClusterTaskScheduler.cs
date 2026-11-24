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

/// <summary>The cluster task scheduler's timings (rule 6.23); outside the generic type so each is one value.</summary>
internal static class ClusterTaskTimings
{
    /// <summary>
    /// How long a run a signal started holds before the task may run again (rule 6.18): the rest of the
    /// signal interval since the run started, never longer than the task's own interval. Zero for a run
    /// on schedule, and for a run that did work on a task that reruns at once.
    /// </summary>
    public static TimeSpan HoldAfterRun(ServerTaskInfo info, TimeSpan signalInterval, TimeSpan sinceRunStarted, bool signalStarted, bool didWork)
    {
        if (!signalStarted || (didWork && info.RerunImmediately))
        {
            return TimeSpan.Zero;
        }

        var rest = signalInterval - sinceRunStarted;
        if (rest <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return rest < info.Interval ? rest : info.Interval;
    }

    /// <summary>How soon to look again at a task that was due but that this pass did not get.</summary>
    public static readonly TimeSpan ClaimRetry = TimeSpan.FromMilliseconds(50);

    /// <summary>An idle run records itself on the row at most this often; a change, a message or a failure always does.</summary>
    public static readonly TimeSpan IdleRecordInterval = TimeSpan.FromMinutes(1);

    /// <summary>The loop never sleeps longer than this, so a dashboard edit of an interval or a lost wake is picked up within it.</summary>
    public static readonly TimeSpan MaxSleep = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Runs the cluster-wide server tasks (those with a lock key) once per tick for the whole cluster (rule
/// 6.23). One loop per server claims every due task in one statement with a lease on its
/// <see cref="ClusterTask"/> row; only the server holding a task's lease runs it, so the other servers
/// spend nothing on it, where each used to take the task's lock every interval. A run that lasts longer
/// than a third of its interval renews its lease; a server that dies mid-run leaves the task to the next
/// claim once the lease lapses. Signals (a job finalized, a message enqueued) run a task at once, or ask
/// the server holding it to run it again; a signal-woken task runs at most once per signal interval for
/// the whole cluster (rule 6.18). While a server from before the lease is alive, each run also
/// takes the task's old lock, so a task never runs on an old and a new server at once during a deploy.
/// </summary>
internal sealed class ClusterTaskScheduler<TContext> : IDisposable
    where TContext : DbContext
{
    private readonly Dictionary<string, Entry> _entries;
    private readonly IServiceScopeFactory _scopes;
    private readonly ServerTaskRunner<TContext> _runner;
    private readonly TimeProvider _time;
    private readonly Guid _serverId;
    private readonly ILogger _logger;
    private readonly TimeSpan _signalInterval;
    private readonly TimeSpan _liveWindow;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private int _wakes;
    private readonly Lock _gate = new();

    // True until a claim has said otherwise: a signal that arrived during registration is honoured by the
    // first pass BEFORE its claim, and a run started then must still take the old lock, since a server
    // from before the lease may be alive (rule 6.23, rolling deploy). Taking it needlessly costs one lock.
    private volatile bool _legacyServersAlive = true;

    // Whether another server is alive, as the last claim said; true until one has. A lone server keeps the
    // lease an idle run of a signal task leaves behind, since no other server could claim it (rule 6.23).
    private volatile bool _otherServersAlive = true;

    // The same answer, shared with the database clock and the heartbeat (rule 2.20): while a server from before
    // 8.0 is alive they keep to this server's own clock. Null when the host's clock is not a DatabaseClock.
    private readonly LegacyServerPresence? _legacyServers;

    public ClusterTaskScheduler(
        IEnumerable<ServerTaskInfo> tasks,
        ServerTaskRunner<TContext> runner,
        IServiceScopeFactory scopes,
        TimeProvider time,
        Guid serverId,
        ILogger logger,
        TimeSpan signalInterval,
        TimeSpan liveWindow,
        LegacyServerPresence? legacyServers = null)
    {
        _legacyServers = legacyServers;
        _entries = tasks.ToDictionary(x => x.Name, x => new Entry(x), StringComparer.Ordinal);
        _scopes = scopes;
        _runner = runner;
        _time = time;
        _serverId = serverId;
        _logger = logger;
        _signalInterval = signalInterval;
        _liveWindow = liveWindow;
    }

    public IEnumerable<(string Name, IReadOnlyList<ServerTaskSignal> Signals)> Subscriptions =>
        _entries.Values.Select(x => (x.Name, x.Signals));

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

    /// <summary>Creates the rows of tasks the cluster has not seen and applies this server's interval to all of them.</summary>
    internal async Task RegisterAsync(CancellationToken ct)
    {
        // One insert-if-absent statement: servers starting together each register every task, and the one
        // that loses an insert just updates the interval. The latest server to start sets the schedule, as
        // each server set its own per-server row before; a dashboard edit holds until the next deploy. A
        // new row is due at once, whatever this server's clock says (rule 6.23).
        using var scope = _scopes.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IWarpServerContext>().Context;
        var queries = scope.ServiceProvider.GetRequiredService<IWarpSqlQueries<TContext>>();
        var tasks = _entries.Values
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => new ClusterTaskRegistration(x.Name, x.Interval.TotalSeconds))
            .ToList();
        await queries.RegisterClusterTasksAsync(context, tasks, ct);
    }

    internal async Task RunAsync(CancellationToken ct)
    {
        await RegisterWithRetryAsync(ct);

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

                // Another server may hold it: ask at most once per signal interval, so a burst of signals
                // is one request statement, not one each (rule 6.18).
                var since = now - entry.LastSignalClaimAt;
                if (since < _signalInterval)
                {
                    sleep = Min(sleep, _signalInterval - since);
                    continue;
                }

                entry.Signaled = false;
                entry.LastSignalClaimAt = now;
            }

            bool claimed;
            try
            {
                claimed = await RequestAsync(entry.Name, ct);
            }
            catch
            {
                // The signal was taken above; a request that failed has not passed it on, so put it back
                // for the next pass instead of losing it until the task's next scheduled run.
                lock (_gate)
                {
                    entry.Signaled = true;
                    entry.LastSignalClaimAt = DateTime.MinValue;
                }

                throw;
            }

            if (claimed)
            {
                lock (_gate)
                {
                    entry.SignalStarted = true;
                }

                Start(entry, ct);
            }
        }

        // The claim statement is sent only when a task is due by what the last claim reported (or its due
        // time is unknown), so a pass woken for something else (a signal for one task, a run that just
        // ended) does not ask the database about the others.
        now = _time.GetUtcNow().UtcDateTime;
        string[] idle;
        bool claimDue;
        lock (_gate)
        {
            var idleEntries = _entries.Values
                .Where(x => x.Running == null)
                .ToList();
            idle = [.. idleEntries.Select(x => x.Name)];
            claimDue = idleEntries.Exists(x => x.DueAt <= now);
        }

        if (claimDue)
        {
            ClusterTaskClaims claims;
            using (var scope = _scopes.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<IWarpServerContext>().Context;
                var queries = scope.ServiceProvider.GetRequiredService<IWarpSqlQueries<TContext>>();
                claims = await queries.ClaimClusterTasksAsync(context, idle, _serverId, _liveWindow, ct);
            }

            // Counted from the answer, not from the pass's start: the statement's own time then makes this
            // server a moment late for the next due time, never a moment early (an early claim finds
            // nothing due and has to be sent again).
            var answered = _time.GetUtcNow().UtcDateTime;
            _legacyServersAlive = claims.LegacyServersAlive;
            _otherServersAlive = claims.OtherServersAlive;
            _legacyServers?.Report(claims.LegacyServersAlive);
            foreach (var state in claims.Tasks)
            {
                var entry = _entries[state.Name];
                lock (_gate)
                {
                    entry.DueAt = answered + state.DueIn;
                }

                if (state.Claimed)
                {
                    lock (_gate)
                    {
                        entry.ScheduledRun = state.Scheduled;
                    }

                    Start(entry, ct);
                }
            }
        }

        // Sleep until the next task is due. A claimed task counts too: its idle run writes nothing and
        // wakes nobody, and when one pass claims every task (they share due times, rule 6.23) there is no
        // unclaimed task to take the time from. A task still running past its due time is not waited for
        // here: its run wakes the loop when it ends.
        now = _time.GetUtcNow().UtcDateTime;
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                if (entry.DueAt > now)
                {
                    sleep = Min(sleep, entry.DueAt - now);
                }
                else if (entry.Running == null)
                {
                    // Due, yet not claimed by this pass: another server is claiming it this instant.
                    sleep = Min(sleep, ClusterTaskTimings.ClaimRetry);
                }
            }
        }

        return sleep;
    }

    /// <summary>
    /// Forgets the due times the last claims reported, so the next pass asks the database again. For tests
    /// that change a task's row behind the scheduler; nothing in production does.
    /// </summary>
    internal void ForgetDueTimes()
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                entry.DueAt = DateTime.MinValue;
            }
        }
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
        bool signalStarted;
        bool scheduledRun;
        lock (_gate)
        {
            signalStarted = entry.SignalStarted;
            entry.SignalStarted = false;

            // Only a run the claim took because its tick came; a signal run or a rerun is not one.
            scheduledRun = entry.ScheduledRun && !signalStarted;
            entry.ScheduledRun = false;
        }

        using var span = WarpTelemetry.StartServerTaskActivity(entry.Name);
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var stopRenewing = new CancellationTokenSource();
        var renewal = RenewWhileRunningAsync(entry, run, stopRenewing.Token);

        string? message = null;
        Exception? failure = null;
        var ran = true;
        TimeSpan? runTime = null;
        var held = false;
        try
        {
            (ran, message) = await _runner.RunAsync(entry.Info, _legacyServersAlive, run.Token, scheduledRun);
            runTime = Stopwatch.GetElapsedTime(started);

            // A run a signal started holds the lease for the rest of the signal interval before it
            // completes (rule 6.18), the renewal still running: a signal meanwhile, here or on another
            // server, becomes a run request the completion turns into one rerun, so the cluster runs the
            // task at most once per interval however many signals arrive. Its completion is then always
            // written, so a request from another server is not left behind the lease until the next tick.
            var hold = ClusterTaskTimings.HoldAfterRun(entry.Info, _signalInterval, runTime.Value, signalStarted, message != null);
            if (ran && hold > TimeSpan.Zero)
            {
                held = true;
                await Task.Delay(hold, _time, run.Token);
            }
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
            span?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            span?.SetTag(WarpTelemetryAttributes.ErrorType, ex.GetType().FullName);
        }
        finally
        {
            await stopRenewing.CancelAsync();
            await renewal;
        }

        // Wake the scheduling loop only when a claim could now succeed. A run that wrote no completion
        // still holds its lease, and one that only released it left the task due when it was, so a pass
        // right after either would claim nothing: that wake doubled the idle claim statements.
        var wake = true;
        try
        {
            span?.SetTag(WarpTelemetryAttributes.WarpTaskLockHeld, ran);
            if (message != null)
            {
                span?.SetTag(WarpTelemetryAttributes.WarpTaskMessage, message);
            }

            if (ran && !ct.IsCancellationRequested)
            {
                var elapsed = runTime ?? Stopwatch.GetElapsedTime(started);
                var claimable = await FinishAsync(entry, message, failure, elapsed.TotalMilliseconds, held);

                // A run that outlasted half its interval may have been skipped by the passes it ran through
                // (a running task is not claimed), so the loop has to look at it again.
                wake = claimable || elapsed >= entry.Interval / 2;
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

                // A signal that arrived after the completion above read the rerun flag (or when no completion
                // was written at all) was parked on a run that is now over: nothing would read it until the
                // task's next run, a whole interval away, and the last finished job of a burst would wait
                // that long. Turn it back into a signal for the next pass.
                if (entry.RerunRequested)
                {
                    entry.RerunRequested = false;
                    entry.Signaled = true;
                    wake = true;
                }
            }

            if (wake)
            {
                Wake();
            }
        }
    }

    // Returns whether the scheduling loop has to look at the task again: its completion moved the next
    // run, or left the task due at once.
    private async Task<bool> FinishAsync(Entry entry, string? message, Exception? failure, double durationMs, bool held)
    {
        var now = _time.GetUtcNow();
        var status = failure == null ? "Completed" : "Failed";
        var didWork = message != null || failure != null;
        bool rerun;
        bool record;
        lock (_gate)
        {
            rerun = entry.RerunRequested || (message != null && entry.RerunImmediately);

            // A rerun a signal asked for is signal-started too, so it holds in turn while signals keep coming.
            entry.SignalStarted = entry.RerunRequested;
            entry.RerunRequested = false;
            record = didWork
                || !string.Equals(status, entry.LastRecordedStatus, StringComparison.Ordinal)
                || now - entry.LastRecordedAt >= ClusterTaskTimings.IdleRecordInterval;
        }

        // An idle run that has nothing to record writes nothing: its lease lapses as the task falls due.
        var changed = didWork || rerun || record || held;

        // Except for a task that signals run: its lease must end with its run. A signal on ANOTHER server
        // can claim the task only when the lease is free, and that server has no way to tell a lease an
        // idle run left behind from a run in progress, so it could only leave a request nobody would read
        // before the lease lapsed: a continuation or a routed message then waited up to a tick and a half
        // instead of the signal interval (rule 6.18). One write per idle run of those tasks for the whole
        // cluster; it leaves the next run where the claim put it, so no claim follows it. Skipped while the
        // last claim saw no other server alive: nobody else could claim the task, and a signal here claims a
        // lease of its own outright. A server that starts meanwhile waits at most until this one's next claim.
        var releases = entry.Signals.Count > 0 && _otherServersAlive;
        var lookAgain = changed;
        if (changed || releases)
        {
            using var scope = _scopes.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<IWarpServerContext>().Context;
            var queries = scope.ServiceProvider.GetRequiredService<IWarpSqlQueries<TContext>>();
            var dueNow = await queries.CompleteClusterTaskAsync(
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

            // A run request that reached the row during the run makes the task due at once, whatever the
            // completion itself asked for.
            lookAgain |= dueNow;
            lock (_gate)
            {
                if (lookAgain)
                {
                    // The completion may have moved the next run (a rerun, a run request): ask again.
                    entry.DueAt = DateTime.MinValue;
                }

                if (record)
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

        return lookAgain;
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
                try
                {
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
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Inside the loop, so the next renewal retries: one transient failure used to end the
                    // renewals for the rest of the run, and a long run then outlived its lease while another
                    // server ran the same task, with nothing left to notice and cancel this one.
                    _logger.LogWarning(ex, "Failed to renew the lease of server task {Name}", entry.Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The run ended.
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

    // Internal for tests: how many times the scheduling loop was woken.
    internal int Wakes => Volatile.Read(ref _wakes);

    private void Wake()
    {
        Interlocked.Increment(ref _wakes);
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

    private sealed class Entry(ServerTaskInfo info)
    {
        public ServerTaskInfo Info { get; } = info;

        public string Name => Info.Name;

        public TimeSpan Interval => Info.Interval;

        public bool RerunImmediately => Info.RerunImmediately;

        public bool LogOnSuccess => Info.LogOnSuccess;

        public IReadOnlyList<ServerTaskSignal> Signals => Info.Signals;

        public Task? Running { get; set; }

        public bool Signaled { get; set; }

        public bool RerunRequested { get; set; }

        // The next run of this task here was asked for by a signal, so it holds afterwards (rule 6.18).
        public bool SignalStarted { get; set; }

        // The last claim took this task because its scheduled time had come (not only for a run request).
        public bool ScheduledRun { get; set; }

        public DateTime LastSignalClaimAt { get; set; } = DateTime.MinValue;

        // When the task is next due, by this server's clock, as the last claim reported it. MinValue
        // while unknown, which makes the next pass ask.
        public DateTime DueAt { get; set; } = DateTime.MinValue;

        public string? LastRecordedStatus { get; set; }

        public DateTimeOffset LastRecordedAt { get; set; } = DateTimeOffset.MinValue;
    }
}
