using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Events;
using Warp.Core.Models;
using Warp.Core.Notifications;

namespace Warp.Core.Services;

public interface IJobCommandService
{
    Task DeleteJob(Guid jobId);

    Task RequeueJob(Guid jobId);

    Task<BulkResultModel> BulkDeleteJobs(Guid[] jobIds);

    Task<BulkResultModel> BulkRequeueJobs(Guid[] jobIds);

    Task<BulkResultModel> DeleteFailedJobsByType(string type);

    Task<BulkResultModel> RequeueFailedJobsByType(string type);
}

public class JobCommandService<TContext> : IJobCommandService
    where TContext : DbContext
{
    private readonly TContext _context;
    private readonly TimeProvider _timeProvider;
    private readonly WarpConfiguration _configuration;
    private readonly IWarpNotificationTransport _notificationTransport;
    private readonly IWarpSqlQueries<TContext> _sqlQueries;
    private readonly ServerTaskSignals<TContext> _signals;

    public JobCommandService(TContext context, TimeProvider timeProvider, IOptions<WarpConfiguration> configuration, IWarpNotificationTransport notificationTransport, IWarpSqlQueries<TContext> sqlQueries, ServerTaskSignals<TContext> signals)
    {
        _context = context;
        _timeProvider = timeProvider;
        _configuration = configuration.Value;
        _notificationTransport = notificationTransport;
        _sqlQueries = sqlQueries;
        _signals = signals;
    }

    public async Task DeleteJob(Guid jobId)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync();

        var job = await _sqlQueries.LockJobByIdWaitAsync(_context, jobId, default);

        if (job == null)
        {
            await transaction.RollbackAsync();
            throw new ArgumentException("Job not found.", nameof(jobId));
        }

        if (job.CurrentState == State.Deleted)
        {
            await transaction.RollbackAsync();
            return;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // Processing jobs: signal graceful cancellation instead of immediate state change.
        // The worker will detect this via RunJobMonitor and set the final state.
        if (job.CurrentState == State.Processing)
        {
            job.CancellationMode = CancellationMode.Graceful;

            await _context.Set<JobLog>().AddAsync(new JobLog
            {
                JobId = job.Id,
                EventType = "CancellationRequested",
                Timestamp = now,
                Level = "Information",
                Message = $"Graceful cancellation requested for job {job.Id}",
            });
            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
            return;
        }

        DecrementStatForState(job.CurrentState);

        job.CurrentState = State.Deleted;
        job.ExpireAt = now.Add(_configuration.JobExpirationTimeout);

        _context.Set<Counter>().Add(new Counter { Key = "stats:deleted", Value = 1 });

        await _context.Set<JobLog>().AddAsync(new JobLog
        {
            JobId = job.Id,
            EventType = "Deleted",
            Timestamp = now,
            Level = "Information",
            Message = $"Job {job.Id} was deleted",
        });
        await _context.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    public async Task RequeueJob(Guid jobId)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync();

        var job = await _sqlQueries.LockJobByIdWaitAsync(_context, jobId, default);

        if (job == null)
        {
            await transaction.RollbackAsync();
            throw new ArgumentException("Job not found.", nameof(jobId));
        }

        if (job.CurrentState == State.Enqueued)
        {
            await transaction.RollbackAsync();
            return;
        }

        // Can't requeue a Processing job — worker is still executing it.
        // Use DeleteJob to cancel it instead.
        if (job.CurrentState == State.Processing)
        {
            await transaction.RollbackAsync();
            return;
        }

        DecrementStatForState(job.CurrentState);

        job.CurrentState = State.Enqueued;
        job.ScheduleTime = _timeProvider.GetUtcNow().UtcDateTime;
        job.ExpireAt = null;

        // Restore parent counters so they wait for this job again
        Job? parent = null;
        if (job.ParentJobId != null)
        {
            parent = await _sqlQueries.LockJobByIdWaitAsync(_context, job.ParentJobId.Value, default);
            if (parent != null)
            {
                parent.JobCount++;
                if (parent.CurrentState == State.Completed || parent.CurrentState == State.Failed)
                {
                    parent.CurrentState = parent.Kind == JobKind.Batch ? State.Awaiting : State.Processing;
                    parent.ExpireAt = null;
                }
            }
        }

        // Only clear HandlerType for direct jobs — message-spawned jobs need it to re-execute the correct handler
        if (parent == null || parent.Kind != JobKind.Message)
        {
            job.HandlerType = null;
        }

        await _context.Set<JobLog>().AddAsync(new JobLog
        {
            JobId = job.Id,
            EventType = "Requeued",
            Timestamp = _timeProvider.GetUtcNow().UtcDateTime,
            Level = "Information",
            Message = $"Job {job.Id} was requeued",
        });
        await _context.SaveChangesAsync();
        await transaction.CommitAsync();

        // Requeue lands the row in Enqueued with ScheduleTime=now — wake dispatcher / bare workers immediately.
        var queue = string.IsNullOrEmpty(job.Queue) ? "default" : job.Queue;
        await NotificationDispatch.DispatchAsync(
            [new Notification(NotificationKind.JobEnqueued, queue)],
            _signals,
            _notificationTransport);
    }

    public async Task<BulkResultModel> BulkDeleteJobs(Guid[] jobIds)
    {
        var result = new BulkResultModel();

        if (jobIds.Length == 0)
        {
            return result;
        }

        // Dedupe — matches 1-by-1 where repeating an ID after the first delete is a no-op
        // success (state is already Deleted). Credit each duplicate to Succeeded so totals
        // stay at jobIds.Length.
        var uniqueIds = jobIds.Distinct().ToArray();
        result.Succeeded += jobIds.Length - uniqueIds.Length;

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var expireAt = now.Add(_configuration.JobExpirationTimeout);

        // Chunk to keep parameter counts under SQL Server's 2100 limit and to bound lock footprint.
        const int chunkSize = 500;

        foreach (var chunk in uniqueIds.Chunk(chunkSize))
        {
            var snapshot = await _context.Set<Job>()
                .AsNoTracking()
                .Where(x => chunk.Contains(x.Id))
                .Select(x =>
                    new
                    {
                        x.Id,
                        x.CurrentState,
                    })
                .ToListAsync();

            // IDs missing from the DB count as Skipped (matches per-row ArgumentException behavior).
            result.Skipped += chunk.Length - snapshot.Count;

            // Already-Deleted matches the single-job no-op success path.
            result.Succeeded += snapshot.Count(x => x.CurrentState == State.Deleted);

            var groups = snapshot
                .Where(x => x.CurrentState != State.Deleted)
                .GroupBy(x => x.CurrentState)
                .ToList();

            if (groups.Count == 0)
            {
                continue;
            }

            await using var transaction = await _context.Database.BeginTransactionAsync();

            foreach (var group in groups)
            {
                var sourceState = group.Key;

                // Sort ids — gives concurrent bulk callers identical UPDATE statements, so
                // the engine plans an identical scan/lock order and no cycle can form across
                // the row-lock acquisition phase.
                var ids = group.Select(x => x.Id).Order().ToArray();

                // Conditional UPDATE: only mutates rows still in sourceState. A concurrent
                // RequeueJob holds the row lock from LockJobByIdWaitAsync; our UPDATE blocks
                // behind it and re-evaluates the predicate after Requeue commits. If state
                // moved off sourceState, the row is excluded. This is a tie-breaker, not a
                // priority — whichever writer commits first wins, the loser observes the new
                // state and skips. Either way exactly one of {Delete, Requeue} ever wins per
                // job, with no half-updates and no log/counter rows for the loser.
                //
                // Processing jobs don't flip state here: we signal graceful cancellation and
                // let RunJobMonitor pick it up — same contract as the single-job path.
                var affected = sourceState == State.Processing
                    ? await _context.Set<Job>()
                        .Where(x => ids.Contains(x.Id))
                        .Where(x => x.CurrentState == State.Processing)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(j => j.CancellationMode, CancellationMode.Graceful))
                    : await _context.Set<Job>()
                        .Where(x => ids.Contains(x.Id))
                        .Where(x => x.CurrentState == sourceState)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(j => j.CurrentState, State.Deleted)
                            .SetProperty(j => j.ExpireAt, expireAt));

                if (affected == 0)
                {
                    result.Skipped += ids.Length;
                    continue;
                }

                // Re-query inside the same transaction to identify the exact IDs that flipped.
                // Our UPDATE's row locks block any other writer from interleaving here.
                var changedIds = sourceState == State.Processing
                    ? await _context.Set<Job>()
                        .AsNoTracking()
                        .Where(x => ids.Contains(x.Id))
                        .Where(x => x.CurrentState == State.Processing)
                        .Where(x => x.CancellationMode == CancellationMode.Graceful)
                        .Select(x => x.Id)
                        .ToListAsync()
                    : await _context.Set<Job>()
                        .AsNoTracking()
                        .Where(x => ids.Contains(x.Id))
                        .Where(x => x.CurrentState == State.Deleted)
                        .Select(x => x.Id)
                        .ToListAsync();

                if (sourceState == State.Processing)
                {
                    foreach (var id in changedIds)
                    {
                        _context.Set<JobLog>().Add(new JobLog
                        {
                            JobId = id,
                            EventType = "CancellationRequested",
                            Timestamp = now,
                            Level = "Information",
                            Message = $"Graceful cancellation requested for job {id}",
                        });
                    }
                }
                else
                {
                    foreach (var id in changedIds)
                    {
                        _context.Set<JobLog>().Add(new JobLog
                        {
                            JobId = id,
                            EventType = "Deleted",
                            Timestamp = now,
                            Level = "Information",
                            Message = $"Job {id} was deleted",
                        });
                    }

                    _context.Set<Counter>().Add(new Counter { Key = "stats:deleted", Value = affected });
                    if (sourceState == State.Completed)
                    {
                        _context.Set<Counter>().Add(new Counter { Key = "stats:succeeded", Value = -affected });
                    }
                    else if (sourceState == State.Failed)
                    {
                        _context.Set<Counter>().Add(new Counter { Key = "stats:failed", Value = -affected });
                    }
                }

                result.Succeeded += affected;
                result.Skipped += ids.Length - affected;
            }

            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        return result;
    }

    public async Task<BulkResultModel> BulkRequeueJobs(Guid[] jobIds)
    {
        var result = new BulkResultModel();

        if (jobIds.Length == 0)
        {
            return result;
        }

        // Dedupe — matches 1-by-1 where repeating an ID after the first requeue is a no-op
        // success (state is already Enqueued). Credit each duplicate to Succeeded so totals
        // stay at jobIds.Length.
        var uniqueIds = jobIds.Distinct().ToArray();
        result.Succeeded += jobIds.Length - uniqueIds.Length;

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var queuesToNotify = new HashSet<string>(StringComparer.Ordinal);

        const int chunkSize = 500;

        foreach (var chunk in uniqueIds.Chunk(chunkSize))
        {
            var snapshot = await _context.Set<Job>()
                .AsNoTracking()
                .Where(x => chunk.Contains(x.Id))
                .Select(x =>
                    new
                    {
                        x.Id,
                        x.CurrentState,
                        x.ParentJobId,
                        x.Queue,
                    })
                .ToListAsync();

            result.Skipped += chunk.Length - snapshot.Count;

            // already-Enqueued and Processing match the single-job no-op success path.
            result.Succeeded += snapshot.Count(x => x.CurrentState == State.Enqueued || x.CurrentState == State.Processing);

            var requeueable = snapshot
                .Where(x => x.CurrentState != State.Enqueued && x.CurrentState != State.Processing)
                .ToList();

            if (requeueable.Count == 0)
            {
                continue;
            }

            var queueById = requeueable.ToDictionary(x => x.Id, x => string.IsNullOrEmpty(x.Queue) ? "default" : x.Queue);

            // Pre-fetch parent kinds (lock-free) so the child UPDATE step can decide HandlerType
            // clearing without holding any parent lock. Job.Kind is immutable after creation,
            // so reading it without a lock is safe.
            var parentIds = requeueable
                .Where(x => x.ParentJobId != null)
                .Select(x => x.ParentJobId!.Value)
                .Distinct()
                .ToArray();

            var parentKinds = parentIds.Length == 0
                ? []
                : await _context.Set<Job>()
                    .AsNoTracking()
                    .Where(p => parentIds.Contains(p.Id))
                    .Select(p =>
                        new
                        {
                            p.Id,
                            p.Kind,
                        })
                    .ToDictionaryAsync(x => x.Id, x => x.Kind);

            await using var transaction = await _context.Database.BeginTransactionAsync();

            // Step 1: UPDATE all child rows first. ExecuteUpdateAsync acquires statement-level
            // row locks held until commit, matching the child-then-parent lock order used by
            // single RequeueJob (line 107 → 139). This is the must-fix for the deadlock that
            // would otherwise occur when a concurrent single RequeueJob races us for the same
            // (child, parent) pair.
            var perParentAffected = new Dictionary<Guid, int>();

            // Parentless requeues — HandlerType always cleared (matches single-job RequeueJob).
            var parentless = requeueable.Where(x => x.ParentJobId == null).ToList();
            foreach (var group in parentless.GroupBy(x => x.CurrentState))
            {
                var sourceState = group.Key;

                // Sort ids — gives concurrent bulk callers identical UPDATE statements.
                var ids = group.Select(x => x.Id).Order().ToArray();

                var affected = await ExecuteRequeueUpdate(ids, sourceState, now, clearHandler: true);
                ApplyRequeueAccounting(result, affected, sourceState, ids.Length);
                await AddRequeueLogsForFlipped(ids, now);
                CollectQueues(ids, queueById, affected, queuesToNotify);
            }

            // Parented children — clearHandler decided from pre-fetched parent kind (no lock).
            // Parent missing from the kind dict ⇒ treat as no-parent ⇒ clearHandler = true
            // (matches single RequeueJob's behavior when LockJobByIdWaitAsync returns null).
            // Sort by parent id so concurrent BulkRequeueJobs callers acquire parent locks in
            // identical order — eliminates the cross-caller cycle on Phase 2 parent locks.
            var byParent = requeueable
                .Where(x => x.ParentJobId != null)
                .GroupBy(x => x.ParentJobId!.Value)
                .OrderBy(g => g.Key)
                .ToList();

            foreach (var pg in byParent)
            {
                var parentId = pg.Key;
                var clearHandler = !parentKinds.TryGetValue(parentId, out var parentKind)
                    || parentKind != JobKind.Message;
                var totalAffected = 0;

                foreach (var group in pg.GroupBy(x => x.CurrentState))
                {
                    var sourceState = group.Key;
                    var ids = group.Select(x => x.Id).Order().ToArray();

                    var affected = await ExecuteRequeueUpdate(ids, sourceState, now, clearHandler);
                    totalAffected += affected;

                    ApplyRequeueAccounting(result, affected, sourceState, ids.Length);
                    await AddRequeueLogsForFlipped(ids, now);
                    CollectQueues(ids, queueById, affected, queuesToNotify);
                }

                if (totalAffected > 0)
                {
                    perParentAffected[parentId] = totalAffected;
                }
            }

            // Step 2: Lock each parent and bump JobCount. Sorted iteration — concurrent
            // BulkRequeueJobs callers walk parents in PK order, so no two callers can hold
            // parent locks in opposing orders, eliminating the cross-caller deadlock cycle.
            foreach (var (parentId, affectedCount) in perParentAffected.OrderBy(kvp => kvp.Key))
            {
                var parent = await _sqlQueries.LockJobByIdWaitAsync(_context, parentId, default);
                if (parent == null)
                {
                    continue;
                }

                parent.JobCount += affectedCount;
                if (parent.CurrentState == State.Completed || parent.CurrentState == State.Failed)
                {
                    parent.CurrentState = parent.Kind == JobKind.Batch ? State.Awaiting : State.Processing;
                    parent.ExpireAt = null;
                }
            }

            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        if (queuesToNotify.Count > 0)
        {
            var notifications = queuesToNotify
                .Select(q => new Notification(NotificationKind.JobEnqueued, q))
                .ToArray();
            await NotificationDispatch.DispatchAsync(notifications, _signals, _notificationTransport);
        }

        return result;
    }

    private async Task<int> ExecuteRequeueUpdate(Guid[] ids, State sourceState, DateTime now, bool clearHandler)
    {
        // Conditional UPDATE: only flips rows still in sourceState. Concurrent DeleteJob holds
        // the row lock from LockJobByIdWaitAsync; this UPDATE blocks, then sees the post-Delete
        // state and excludes the row. Tie-breaker semantic — whichever writer commits first
        // wins, exactly one of {Delete, Requeue} ever wins per row.
        if (clearHandler)
        {
            return await _context.Set<Job>()
                .Where(x => ids.Contains(x.Id))
                .Where(x => x.CurrentState == sourceState)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.CurrentState, State.Enqueued)
                    .SetProperty(j => j.ScheduleTime, now)
                    .SetProperty(j => j.ExpireAt, (DateTime?)null)
                    .SetProperty(j => j.HandlerType, (string?)null));
        }

        return await _context.Set<Job>()
            .Where(x => ids.Contains(x.Id))
            .Where(x => x.CurrentState == sourceState)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.CurrentState, State.Enqueued)
                .SetProperty(j => j.ScheduleTime, now)
                .SetProperty(j => j.ExpireAt, (DateTime?)null));
    }

    private void ApplyRequeueAccounting(BulkResultModel result, int affected, State sourceState, int groupSize)
    {
        if (affected == 0)
        {
            result.Skipped += groupSize;

            return;
        }

        if (sourceState == State.Completed)
        {
            _context.Set<Counter>().Add(new Counter { Key = "stats:succeeded", Value = -affected });
        }
        else if (sourceState == State.Failed)
        {
            _context.Set<Counter>().Add(new Counter { Key = "stats:failed", Value = -affected });
        }
        else if (sourceState == State.Deleted)
        {
            _context.Set<Counter>().Add(new Counter { Key = "stats:deleted", Value = -affected });
        }

        result.Succeeded += affected;
        result.Skipped += groupSize - affected;
    }

    private async Task AddRequeueLogsForFlipped(Guid[] ids, DateTime now)
    {
        // Re-query inside the transaction to identify rows our UPDATE just flipped. ScheduleTime
        // = now narrows the result to our batch — collisions only matter if two BulkRequeueJobs
        // calls land on the same tick, in which case both add identical Requeued log entries.
        var flippedIds = await _context.Set<Job>()
            .AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .Where(x => x.CurrentState == State.Enqueued)
            .Where(x => x.ScheduleTime == now)
            .Select(x => x.Id)
            .ToListAsync();

        foreach (var id in flippedIds)
        {
            _context.Set<JobLog>().Add(new JobLog
            {
                JobId = id,
                EventType = "Requeued",
                Timestamp = now,
                Level = "Information",
                Message = $"Job {id} was requeued",
            });
        }
    }

    private static void CollectQueues(Guid[] ids, Dictionary<Guid, string> queueById, int affected, HashSet<string> queues)
    {
        if (affected == 0)
        {
            return;
        }

        // Worst case: queues includes a queue where every row lost the race. The notification
        // is then a harmless wake-up — workers fetch, find nothing, go back to sleep.
        foreach (var id in ids)
        {
            if (queueById.TryGetValue(id, out var queue))
            {
                queues.Add(queue);
            }
        }
    }

    public async Task<BulkResultModel> DeleteFailedJobsByType(string type)
    {
        var result = new BulkResultModel();
        while (true)
        {
            var ids = await _context.Set<Job>()
                .Where(x => x.Kind == JobKind.Job && x.CurrentState == State.Failed && x.Type == type)
                .Select(x => x.Id)
                .Take(1000)
                .ToListAsync();

            if (ids.Count == 0)
            {
                break;
            }

            var batchResult = await BulkDeleteJobs([.. ids]);
            result.Succeeded += batchResult.Succeeded;
            result.Skipped += batchResult.Skipped;
        }

        return result;
    }

    public async Task<BulkResultModel> RequeueFailedJobsByType(string type)
    {
        var result = new BulkResultModel();
        while (true)
        {
            var ids = await _context.Set<Job>()
                .Where(x => x.Kind == JobKind.Job && x.CurrentState == State.Failed && x.Type == type)
                .Select(x => x.Id)
                .Take(1000)
                .ToListAsync();

            if (ids.Count == 0)
            {
                break;
            }

            var batchResult = await BulkRequeueJobs([.. ids]);
            result.Succeeded += batchResult.Succeeded;
            result.Skipped += batchResult.Skipped;
        }

        return result;
    }

    private void DecrementStatForState(State state)
    {
        var key = state switch
        {
            State.Completed => "stats:succeeded",
            State.Failed => "stats:failed",
            State.Deleted => "stats:deleted",
            _ => null,
        };

        if (key != null)
        {
            _context.Set<Counter>().Add(new Counter { Key = key, Value = -1 });
        }
    }
}
