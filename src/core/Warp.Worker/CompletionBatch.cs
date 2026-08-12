using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Warp.Core;
using Warp.Core.Data;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;

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
/// Per-worker buffer of pending completions for dispatcher mode. Flushes all buffered entries
/// in a single transaction when the size or time threshold is reached, when the worker is about
/// to suspend for more work, or on shutdown.
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

    private readonly List<PendingCompletion> _buffer;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly IDatabaseExceptionClassifier _exceptionClassifier;
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval;
    private DateTimeOffset? _firstEntryTimestamp;

    public CompletionBatch(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger logger,
        IDatabaseExceptionClassifier exceptionClassifier,
        int batchSize,
        TimeSpan flushInterval)
    {
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

    public void Add(PendingCompletion entry)
    {
        if (_buffer.Count == 0)
        {
            _firstEntryTimestamp = _timeProvider.GetUtcNow();
        }

        _buffer.Add(entry);
    }

    /// <summary>
    /// Persists the buffered completions. On success (including poison-drops isolated via split)
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
    public async Task FlushAsync()
    {
        if (_buffer.Count == 0)
        {
            return;
        }

        var pending = _buffer.ToArray();
        _buffer.Clear();
        _firstEntryTimestamp = null;

        await FlushRangeAsync(pending, 0, pending.Length, CancellationToken.None);
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

                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

                for (var i = start; i < start + count; i++)
                {
                    var entry = entries[i];
                    context.Entry(entry.Job).State = EntityState.Modified;

                    if (entry.Counters.Count > 0)
                    {
                        context.Set<Counter>().AddRange(entry.Counters);
                    }

                    if (entry.Logs.Count > 0)
                    {
                        context.Set<JobLog>().AddRange(entry.Logs);
                    }

                    // Error-grouping inbox row for a failed attempt (§8.29) — written in the same completion
                    // transaction, off the hot path. The single-worker path does the equivalent in FinalizeJobState.
                    if (entry.Occurrence is { } occurrence)
                    {
                        context.Set<ErrorOccurrence>().Add(occurrence);
                    }
                }

                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

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
            catch (DbUpdateException ex)
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
