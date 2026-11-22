using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Warp.Core;
using Warp.Core.Data;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Services;

namespace Warp.Worker;

/// <summary>
/// A single job completion waiting to be persisted: the mutated <see cref="Job"/> entity,
/// the counters to insert, and the log entries to insert (final state log + any drained handler logs).
/// </summary>
internal readonly record struct PendingCompletion(
    Job Job,
    IReadOnlyList<Counter> Counters,
    IReadOnlyList<JobLog> Logs,
    ErrorOccurrence? Occurrence = null);

/// <summary>
/// What a flush took, for the wake that follows it (rule 6.11): the distinct parents of the flushed
/// jobs, and the flushed jobs that reached a terminal state.
/// </summary>
internal readonly record struct FlushedJobs(IReadOnlyCollection<Guid> Parents, IReadOnlyCollection<Guid> Settled);

/// <summary>
/// The buffer of pending completions of one dispatcher worker GROUP (rule 6.13). Flushes all buffered
/// entries in a single transaction when the size or time threshold is reached, when the group has
/// nothing left running, or on shutdown. One per group, not per worker: a worker's own buffer was
/// flushed every time that worker found the channel empty for a moment, which at ten workers was a
/// transaction for every two jobs.
/// <para>
/// On <see cref="DbUpdateException"/> (concurrency mismatch, phantom row, etc.) the flush
/// recursively splits the batch in half to isolate the failing entries. A single-entry partition
/// that still fails is logged and dropped — <c>StaleJobRecovery</c> will recover the underlying
/// <c>Processing</c> row. Non-<see cref="DbUpdateException"/> failures propagate to the caller.
/// </para>
/// </summary>
internal sealed class CompletionBatch
{
    // Transient-deadlock retry budget for the whole batch flush. Three attempts with a short
    // exponential backoff is enough to clear a SQL Server 1205 / PG 40P01 storm in practice
    // without masking real defects (a deadlock that survives 3 retries indicates a structural
    // issue worth surfacing). 50ms × 2^attempt = 50ms / 100ms / 200ms backoff schedule.
    private const int MaxDeadlockRetries = 3;
    private const int DeadlockRetryBaseDelayMs = 50;

    // Guards the buffer and its timestamp. One worker owns a batch and is the only caller in normal
    // running, so this is never contended there. The second caller is WarpDispatcherWorker.StopAsync,
    // which flushes from the host's shutdown thread and, when the shutdown timeout expires with a
    // handler still running, can do so while the worker loop is adding to the same buffer. Unguarded,
    // a completion added between the copy and the clear was dropped (the job then waited for
    // StaleJobRecovery and ran twice), and List<T> is not safe for that use at all. Held only for the
    // in-memory swap, never across the database write.
    private readonly Lock _gate = new();
    private readonly List<PendingCompletion> _buffer;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly IDatabaseExceptionClassifier _exceptionClassifier;
    private readonly WarpCounterBuffer _counterBuffer;
    private readonly Func<DbContext, IReadOnlyList<Job>, IReadOnlyList<JobLog>, CancellationToken, Task<IReadOnlyCollection<Guid>>> _completeJobs;
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval;
    private DateTimeOffset? _firstEntryTimestamp;

    public CompletionBatch(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger logger,
        IDatabaseExceptionClassifier exceptionClassifier,
        int batchSize,
        TimeSpan flushInterval,
        WarpCounterBuffer counterBuffer,
        Func<DbContext, IReadOnlyList<Job>, IReadOnlyList<JobLog>, CancellationToken, Task<IReadOnlyCollection<Guid>>> completeJobs)
    {
        _counterBuffer = counterBuffer;
        _completeJobs = completeJobs;
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
        _exceptionClassifier = exceptionClassifier;
        _batchSize = batchSize <= 0 ? 1 : batchSize;
        _flushInterval = flushInterval;
        _buffer = new List<PendingCompletion>(_batchSize);
    }

    public int Count => _buffer.Count;

    public bool IsFull => _buffer.Count >= _batchSize;

    public bool IsTimeElapsed =>
        _firstEntryTimestamp is { } first
        && _timeProvider.GetUtcNow() - first >= _flushInterval;

    /// <summary>
    /// Buffers a completion. Returns <c>true</c> when it is the first of a new buffer, which is when the
    /// caller starts the clock on <see cref="FlushInterval"/>.
    /// </summary>
    public bool Add(PendingCompletion entry)
    {
        lock (_gate)
        {
            var first = _buffer.Count == 0;
            if (first)
            {
                _firstEntryTimestamp = _timeProvider.GetUtcNow();
            }

            _buffer.Add(entry);

            return first;
        }
    }

    public TimeSpan FlushInterval => _flushInterval;

    // Takes everything buffered so far and leaves the buffer empty, as one step.
    internal PendingCompletion[] Drain()
    {
        lock (_gate)
        {
            if (_buffer.Count == 0)
            {
                return [];
            }

            var pending = _buffer.ToArray();
            _buffer.Clear();
            _firstEntryTimestamp = null;

            return pending;
        }
    }

    /// <summary>
    /// Persists the buffered completions and returns what it took. On success (including poison-drops isolated via split)
    /// the buffer is cleared. On transient failure (non-<see cref="DbUpdateException"/>) the buffer
    /// is already drained — the exception propagates and the orphaned <c>Processing</c> rows are
    /// recovered by <c>StaleJobRecovery</c>.
    /// <para>
    /// No cancellation parameter on purpose: once the buffer is drained, cancelling an in-flight
    /// flush would orphan the drained entries (the split-on-failure recursion makes re-queuing
    /// them unsound). DB operations run under <see cref="CancellationToken.None"/> so graceful
    /// shutdown still commits any in-flight batch.
    /// </para>
    /// </summary>
    public async Task<FlushedJobs> FlushAsync()
    {
        var pending = Drain();
        if (pending.Length == 0)
        {
            return new FlushedJobs([], []);
        }

        await FlushRangeAsync(pending, 0, pending.Length, CancellationToken.None);

        return new FlushedJobs((IReadOnlyCollection<Guid>?)ParentsOf(pending) ?? [], SettledOf(pending));
    }

    // The flushed jobs that reached a terminal state (not the requeued ones): a continuation may wait
    // on each.
    private static List<Guid> SettledOf(PendingCompletion[] pending)
    {
        var settled = new List<Guid>(pending.Length);
        settled.AddRange(pending
            .Where(x => x.Job.CurrentState is State.Completed or State.Failed or State.Deleted)
            .Select(x => x.Job.Id));

        return settled;
    }

    // The distinct parents of the flushed jobs, for the wake that follows (rule 6.11). Most jobs have
    // none, so nothing is allocated for them.
    private static HashSet<Guid>? ParentsOf(PendingCompletion[] pending)
    {
        HashSet<Guid>? parents = null;
        foreach (var entry in pending)
        {
            if (entry.Job.ParentJobId is { } parent)
            {
                parents ??= [];
                parents.Add(parent);
            }
        }

        return parents;
    }

    private async Task FlushRangeAsync(PendingCompletion[] entries, int start, int count, CancellationToken cancellationToken)
    {
        if (count == 0)
        {
            return;
        }

        // Retry-on-deadlock outer loop: SQL Server 1205 / PG 40P01 are transient (the deadlock
        // monitor picked us as victim; the surviving transaction commits). Retry the whole batch
        // before falling through to the split-on-failure path so we don't fragment the batch
        // unnecessarily on a contention spike.
        var attempt = 0;
        while (true)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<IWarpServerContext>().Context;

                // The jobs and their log rows go in one set-based statement (rule 6.13), not through the
                // change tracker, which writes one UPDATE and one INSERT per row. Only an error-grouping
                // inbox row (§8.29), which a failed attempt carries, is still saved by EF, in the same
                // transaction.
                var jobs = new Job[count];
                var logs = new List<JobLog>(count * 2);
                var occurrences = false;
                for (var i = 0; i < count; i++)
                {
                    var entry = entries[start + i];
                    jobs[i] = entry.Job;
                    logs.AddRange(entry.Logs);

                    if (entry.Occurrence is { } occurrence)
                    {
                        context.Set<ErrorOccurrence>().Add(occurrence);
                        occurrences = true;
                    }
                }

                // No savepoint around the save (rule 6.12): a failure abandons this transaction and the
                // split below starts new ones.
                context.Database.AutoSavepointsEnabled = false;
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

                // In id order, so two statements that ever touch the same rows lock them the same way round.
                Array.Sort(jobs, static (x, y) => x.Id.CompareTo(y.Id));
                var written = await _completeJobs(context, jobs, logs, cancellationToken);
                if (occurrences)
                {
                    await context.SaveChangesAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);

                // Only once the batch has COMMITTED do its increments become real, so a rolled-back or
                // retried batch cannot leave them counted. A job the statement skipped, because its row
                // was deleted while it ran, counts nothing either.
                var writtenIds = written.Count == count ? null : written.ToHashSet();
                for (var i = start; i < start + count; i++)
                {
                    if (writtenIds != null && !writtenIds.Contains(entries[i].Job.Id))
                    {
                        _logger.LogInformation(
                            "Discarding the completion of job {jobId}: its row no longer exists",
                            entries[i].Job.Id);

                        continue;
                    }

                    foreach (var counter in entries[i].Counters)
                    {
                        _counterBuffer.Add(counter.Key, counter.Value);
                    }
                }

                return;
            }
            catch (Exception ex) when (attempt < MaxDeadlockRetries && _exceptionClassifier.IsTransientDeadlock(ex))
            {
                _logger.LogWarning(
                    ex,
                    "Completion flush hit a transient deadlock (attempt {attempt}/{max}); retrying after backoff",
                    attempt + 1,
                    MaxDeadlockRetries);

                await Task.Delay(TimeSpan.FromMilliseconds(DeadlockRetryBaseDelayMs * (1 << attempt)), cancellationToken);
                attempt++;
            }
            catch (Exception ex) when (ex is DbUpdateException or DbException { IsTransient: false })
            {
                if (count == 1)
                {
                    _logger.LogError(
                        ex,
                        "Dropping poison completion for job {jobId}; StaleJobRecovery will recover the underlying Processing row",
                        entries[start].Job.Id);
                    return;
                }

                var mid = count / 2;
                await FlushRangeAsync(entries, start, mid, cancellationToken);
                await FlushRangeAsync(entries, start + mid, count - mid, cancellationToken);

                return;
            }
        }
    }
}
