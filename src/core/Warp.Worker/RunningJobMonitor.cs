using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Logging;

namespace Warp.Worker;

/// <summary>
/// Watches every job a worker group is running, in one loop (rule 6.14): flushes their handler logs
/// and progress rows, renews their keep-alive and finds the ones a user asked to cancel.
/// <para>
/// Each running job used to have a monitor of its own, so a group running N long jobs sent N
/// keep-alive updates and N cancellation reads every <c>CancellationCheckInterval</c>, plus a save per
/// job per <c>LogFlushInterval</c> for any that logged. Here it is one statement per check for the
/// whole group, renewing every job and returning the ones flagged for cancellation, and one save per
/// flush for all the logs.
/// </para>
/// <para>
/// The loop runs only while something is registered: it starts with the first job and ends when the
/// last one leaves, so an idle group costs nothing.
/// </para>
/// </summary>
internal sealed class RunningJobMonitor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly RenewRunningJobs _renew;
    private readonly TimeSpan _tick;
    private readonly TimeSpan _renewEvery;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private Task? _loop;
    private TimeSpan _sinceRenewal;

    public RunningJobMonitor(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        WarpServerConfiguration configuration,
        RenewRunningJobs renew,
        ILogger logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _renew = renew;
        _logger = logger;
        _renewEvery = configuration.CancellationCheckInterval;
        _tick = configuration.LogFlushInterval < _renewEvery ? configuration.LogFlushInterval : _renewEvery;
    }

    /// <summary>
    /// Renews the keep-alive of the given jobs, stamping each with its worker, and returns the ones whose
    /// cancellation was requested. Supplied by the provider (<c>IWarpSqlQueries.RenewRunningJobsAsync</c>).
    /// </summary>
    public delegate Task<IReadOnlyCollection<Guid>> RenewRunningJobs(
        DbContext context,
        IReadOnlyList<Guid> jobIds,
        IReadOnlyList<Guid> workerIds,
        DateTime now,
        CancellationToken ct);

    /// <summary>
    /// Starts watching a job. Dispose the result when the job's handler has returned, before its
    /// finalizing write: from then on the job is neither renewed nor flushed by this monitor.
    /// </summary>
    public IDisposable Register(
        Guid jobId,
        Guid workerId,
        JobLogCollector logs,
        JobProgressCollector progress,
        DeferredJobLog processingLog,
        CancellationTokenSource jobCts,
        CancellationToken stoppingToken)
    {
        var entry = new Entry(this, jobId, workerId, logs, progress, processingLog, jobCts, _timeProvider.GetTimestamp(), stoppingToken);
        lock (_gate)
        {
            _entries[jobId] = entry;
            if (_loop == null)
            {
                _sinceRenewal = TimeSpan.Zero;
                _loop = Task.Run(RunAsync, CancellationToken.None);
            }
        }

        return entry;
    }

    private void Remove(Entry entry)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(entry.JobId, out var current) && ReferenceEquals(current, entry))
            {
                _entries.Remove(entry.JobId);
            }
        }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            await Task.Delay(_tick, CancellationToken.None).ConfigureAwait(false);

            Entry[] running;
            lock (_gate)
            {
                if (_entries.Count == 0)
                {
                    _loop = null;

                    return;
                }

                // A worker whose host is stopping no longer has its job watched, as when each job's
                // monitor ended with the worker's stopping token.
                running = [.. _entries.Values.Where(x => !x.StoppingToken.IsCancellationRequested)];
            }

            _sinceRenewal += _tick;
            var renew = _sinceRenewal >= _renewEvery;
            if (renew)
            {
                _sinceRenewal = TimeSpan.Zero;
            }

            await TickAsync(running, renew).ConfigureAwait(false);
        }
    }

    private async Task TickAsync(Entry[] running, bool renew)
    {
        var logs = new List<JobLog>();
        foreach (var entry in running)
        {
            // A job's "Processing" row goes out with the first flush once it has run a whole tick, so a
            // job that finishes sooner writes it with its completion instead (rule 6.12).
            if (_timeProvider.GetElapsedTime(entry.StartedAt) >= _tick && entry.ProcessingLog.Take() is { } processing)
            {
                logs.Add(processing);
            }

            logs.AddRange(entry.Logs.Drain());
            logs.AddRange(entry.Progress.Drain());
        }

        if (running.Length == 0 || (!renew && logs.Count == 0))
        {
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<IWarpServerContext>().Context;

            IReadOnlyCollection<Guid> cancelled = [];
            if (renew)
            {
                // In id order, as the completion writes them, so the two never lock rows the opposite
                // way round.
                var ordered = running.OrderBy(x => x.JobId).ToArray();
                var now = _timeProvider.GetUtcNow().UtcDateTime;
                try
                {
                    cancelled = await _renew(context, [.. ordered.Select(x => x.JobId)], [.. ordered.Select(x => x.WorkerId)], now, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    // The next check renews again; a missed renewal costs nothing until InvisibilityTimeout.
                    _logger.LogWarning(e, "Failed to renew the keep-alive of {count} running jobs", ordered.Length);
                }
            }

            if (logs.Count > 0)
            {
                context.Set<JobLog>().AddRange(logs);
                await context.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            }

            // After their logs are saved, as each job's own monitor did.
            foreach (var entry in running.Where(x => cancelled.Contains(x.JobId)))
            {
                _logger.LogInformation("Job {jobId} cancellation requested, cancelling handler", entry.JobId);
                await entry.CancelAsync().ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to write the logs of {count} running jobs", running.Length);
        }
    }

    private sealed class Entry : IDisposable
    {
        private readonly RunningJobMonitor _owner;
        private readonly CancellationTokenSource _jobCts;
        private readonly Lock _gate = new();
        private bool _released;

        public Entry(
            RunningJobMonitor owner,
            Guid jobId,
            Guid workerId,
            JobLogCollector logs,
            JobProgressCollector progress,
            DeferredJobLog processingLog,
            CancellationTokenSource jobCts,
            long startedAt,
            CancellationToken stoppingToken)
        {
            _owner = owner;
            JobId = jobId;
            WorkerId = workerId;
            Logs = logs;
            Progress = progress;
            ProcessingLog = processingLog;
            _jobCts = jobCts;
            StoppingToken = stoppingToken;
            StartedAt = startedAt;
        }

        public Guid JobId { get; }

        public Guid WorkerId { get; }

        public JobLogCollector Logs { get; }

        public JobProgressCollector Progress { get; }

        public DeferredJobLog ProcessingLog { get; }

        public CancellationToken StoppingToken { get; }

        public long StartedAt { get; }

        // Cancels the handler unless the job has already left: its worker disposes the token source
        // soon after, so cancelling a job that has gone could touch a disposed one.
        public async Task CancelAsync()
        {
            lock (_gate)
            {
                if (_released)
                {
                    return;
                }

                _released = true;
            }

            _owner.Remove(this);
            try
            {
                await _jobCts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The job finished and its worker let go of the token source in the meantime.
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _released = true;
            }

            _owner.Remove(this);
        }
    }
}
