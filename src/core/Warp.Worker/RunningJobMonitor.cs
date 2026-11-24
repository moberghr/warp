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
        // Taken per entry under its own lock, so a job whose worker has let go (Dispose) gives up nothing,
        // and a row a failed save took can go back to it.
        var taken = new List<Taken>(running.Length);
        var logs = new List<JobLog>();
        var watched = new List<Entry>(running.Length);
        foreach (var entry in running)
        {
            if (entry.TryTake(_timeProvider.GetElapsedTime(entry.StartedAt) >= _tick) is not { } rows)
            {
                continue;
            }

            watched.Add(entry);
            if (rows.Count > 0)
            {
                taken.Add(rows);
                rows.AddTo(logs);
            }
        }

        if (watched.Count == 0 || (!renew && logs.Count == 0))
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IWarpServerContext>().Context;

        IReadOnlyCollection<Guid> cancelled = [];
        if (renew)
        {
            // In id order, as the completion writes them, so the two never lock rows the opposite way round.
            var ordered = watched.OrderBy(x => x.JobId).ToArray();
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
            try
            {
                context.Set<JobLog>().AddRange(logs);
                await context.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // Back to their jobs, for the next tick or the job's own finalizing write. Only a job that
                // finished meanwhile has nothing to give them back to.
                var lost = taken.Count(x => !x.PutBack());
                _logger.LogWarning(e, "Failed to write the logs of {count} running jobs; {lost} had already finished and lose this tick's rows", taken.Count, lost);
            }
        }

        // Whether or not the logs were saved. Not awaited: CancelAsync returns only once the job token's
        // callbacks have run, and a handler's callback that blocks would hold the whole group's tick.
        foreach (var entry in watched.Where(x => cancelled.Contains(x.JobId)))
        {
            _logger.LogInformation("Job {jobId} cancellation requested, cancelling handler", entry.JobId);
            _ = CancelAsync(entry);
        }
    }

    private async Task CancelAsync(Entry entry)
    {
        try
        {
            await entry.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Cancelling job {jobId} failed", entry.JobId);
        }
    }

    // What one tick took from one running job, so a failed save can return it.
    private sealed class Taken
    {
        private readonly Entry _entry;
        private readonly JobLog? _processing;
        private readonly List<JobLog> _logs;
        private readonly List<JobLog> _progress;

        public Taken(Entry entry, JobLog? processing, List<JobLog> logs, List<JobLog> progress)
        {
            _entry = entry;
            _processing = processing;
            _logs = logs;
            _progress = progress;
        }

        public int Count => (_processing == null ? 0 : 1) + _logs.Count + _progress.Count;

        public void AddTo(List<JobLog> rows)
        {
            if (_processing != null)
            {
                rows.Add(_processing);
            }

            rows.AddRange(_logs);
            rows.AddRange(_progress);
        }

        public bool PutBack() => _entry.PutBack(_processing, _logs, _progress);
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

        // This job's rows for one tick: its "Processing" row once it has run a whole tick (rule 6.12), and
        // its drained logs and progress. Null once its worker has let go.
        public Taken? TryTake(bool includeProcessing)
        {
            lock (_gate)
            {
                if (_released)
                {
                    return null;
                }

                var processing = includeProcessing ? ProcessingLog.TakeFor(JobId) : null;

                return new Taken(this, processing, Logs.Drain(), Progress.Drain());
            }
        }

        // False when the job has finished since: its own write has gone, and nothing will read these.
        public bool PutBack(JobLog? processing, List<JobLog> logs, List<JobLog> progress)
        {
            lock (_gate)
            {
                if (_released)
                {
                    return false;
                }

                if (processing != null)
                {
                    ProcessingLog.Restore(processing);
                }

                Logs.Requeue(logs);
                Progress.Forget(progress);

                return true;
            }
        }

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
