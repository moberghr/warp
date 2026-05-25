using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Events;

namespace Warp.Worker.Services;

/// <summary>
/// Runs one orchestration pass per iteration: finalize parents whose children have all
/// reached terminal state, activate continuations whose parent is now terminal, and fail
/// children of deleted parents. Wake-up on <c>JobFinalized</c> events (worker completions
/// and push notifications) is routed through
/// <see cref="ServerTaskSignals{TContext}.SignalJobFinalized"/>.
/// </summary>
public sealed class Orchestrator<TContext> : IServerTask
    where TContext : DbContext
{
    private readonly TContext _context;
    private readonly TimeProvider _time;
    private readonly WarpWorkerConfiguration _configuration;

    public Orchestrator(
        TContext context,
        TimeProvider time,
        IOptions<WarpWorkerConfiguration> configuration)
    {
        _context = context;
        _time = time;
        _configuration = configuration.Value;
    }

    public string Name => "Orchestration";

    public string? LockKey => "warp:orchestration";

    public TimeSpan? DefaultInterval => _configuration.OrchestrationInterval;

    public IEnumerable<ServerTaskSignal> Signals => [ServerTaskSignal.JobFinalized];

    public async Task<string?> ExecuteAsync(CancellationToken ct)
    {
        var workDone = await RunOrchestrationCoreAsync(ct);

        return workDone ? "Orchestration pass completed" : null;
    }

    internal async Task<bool> RunOrchestrationCoreAsync(CancellationToken ct)
    {
        var jobExpirationTimeout = _configuration.JobExpirationTimeout;

        var finalized = await FinalizeParentsAsync(jobExpirationTimeout, ct);
        _context.ChangeTracker.Clear();
        var activated = await ActivateContinuationsAsync(ct);
        _context.ChangeTracker.Clear();
        var cleaned = await FailChildrenOfDeletedParentsAsync(jobExpirationTimeout, ct);
        _context.ChangeTracker.Clear();

        return finalized > 0 || activated > 0 || cleaned > 0;
    }

    private async Task<int> FinalizeParentsAsync(TimeSpan jobExpirationTimeout, CancellationToken ct)
    {
        // Bound the candidate set so one iteration can't churn through tens of thousands of
        // parents while holding the orchestration lock. RerunImmediately = true means the
        // outer loop re-ticks instantly, and the next iteration sees the remaining rows.
        var readyParents = await _context.Set<Job>()
            .Where(p => (p.Kind == JobKind.Message || p.Kind == JobKind.Batch)
                && (p.CurrentState == State.Awaiting || p.CurrentState == State.Processing))
            .Where(p => !_context.Set<Job>()
                .Any(c => c.ParentJobId == p.Id && c.Kind == JobKind.Job
                    && c.CurrentState != State.Completed && c.CurrentState != State.Failed
                    && c.CurrentState != State.Awaiting))
            .Where(p => _context.Set<Job>()
                .Any(c => c.ParentJobId == p.Id && c.Kind == JobKind.Job
                    && (c.CurrentState == State.Completed || c.CurrentState == State.Failed)))
            .Take(_configuration.ServerTaskBatchSize)
            .ToListAsync(ct);

        if (readyParents.Count == 0)
        {
            return 0;
        }

        // Two-step fetch (§5.2): a single follow-up query collects every parent id that
        // owns at least one failed child, instead of an `_context.Set<Job>().Any(...)`
        // subquery inside the Select projection (which EF Core has translated unreliably
        // across versions). Folds what used to be a per-parent N+1 of AnyAsync calls into
        // one round-trip without breaking the projection rule.
        var parentIds = readyParents.ConvertAll(p => p.Id);
        var parentIdsWithFailedChild = await _context.Set<Job>()
            .Where(c => c.Kind == JobKind.Job && c.CurrentState == State.Failed)
            .Where(c => c.ParentJobId != null && parentIds.Contains(c.ParentJobId.Value))
            .Select(c => c.ParentJobId!.Value)
            .Distinct()
            .ToListAsync(ct);
        var failedParentIdSet = parentIdsWithFailedChild.ToHashSet();

        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var parent in readyParents)
        {
            var continuationOptions = parent.ContinuationOptions ?? ContinuationOptions.OnlyOnSucceeded;
            var hasFailedChildren = failedParentIdSet.Contains(parent.Id);

            if (hasFailedChildren && continuationOptions != ContinuationOptions.OnAnyFinishedState)
            {
                parent.CurrentState = State.Failed;
            }
            else
            {
                parent.CurrentState = State.Completed;
            }

            parent.ExpireAt = now.Add(jobExpirationTimeout);

            var kindLabel = parent.Kind == JobKind.Batch ? "Batch" : "Message";
            var message = parent.CurrentState == State.Completed
                ? $"{kindLabel} completed — all children finished"
                : $"{kindLabel} failed — one or more children failed";

            _context.Set<JobLog>().Add(new JobLog
            {
                JobId = parent.Id,
                EventType = parent.CurrentState == State.Completed ? "Completed" : "Failed",
                Level = "Information",
                Timestamp = now,
                Message = message,
            });
        }

        await _context.SaveChangesAsync(ct);

        return readyParents.Count;
    }

    private async Task<int> ActivateContinuationsAsync(CancellationToken ct)
    {
        var awaitingChildren = await _context.Set<Job>()
            .AsNoTracking()
            .Where(c => c.CurrentState == State.Awaiting && c.ParentJobId != null)
            .Where(c => _context.Set<Job>().Any(p =>
                p.Id == c.ParentJobId
                && (p.CurrentState == State.Completed
                    || (p.CurrentState == State.Failed && p.ContinuationOptions == ContinuationOptions.OnAnyFinishedState))))
            .Take(_configuration.ServerTaskBatchSize)
            .ToListAsync(ct);

        if (awaitingChildren.Count == 0)
        {
            return 0;
        }

        var activated = 0;
        foreach (var child in awaitingChildren)
        {
            var childId = child.Id;
            if (child.Kind == JobKind.Batch)
            {
                await _context.Set<Job>()
                    .Where(x => x.Id == childId && x.CurrentState == State.Awaiting)
                    .ExecuteUpdateAsync(x => x.SetProperty(p => p.CurrentState, State.Processing), ct);

                activated += await _context.Set<Job>()
                    .Where(x => x.ParentJobId == childId && x.CurrentState == State.Awaiting && x.Kind == JobKind.Job)
                    .ExecuteUpdateAsync(x => x.SetProperty(p => p.CurrentState, State.Enqueued), ct);
            }
            else
            {
                activated += await _context.Set<Job>()
                    .Where(x => x.Id == childId && x.CurrentState == State.Awaiting)
                    .ExecuteUpdateAsync(x => x.SetProperty(p => p.CurrentState, State.Enqueued), ct);
            }
        }

        return activated;
    }

    private async Task<int> FailChildrenOfDeletedParentsAsync(TimeSpan jobExpirationTimeout, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        var orphaned = await _context.Set<Job>()
            .Where(c => c.CurrentState == State.Awaiting && c.ParentJobId != null)
            .Where(c => _context.Set<Job>().Any(p =>
                p.Id == c.ParentJobId && p.CurrentState == State.Deleted))
            .Take(_configuration.ServerTaskBatchSize)
            .ToListAsync(ct);

        if (orphaned.Count == 0)
        {
            return 0;
        }

        foreach (var child in orphaned)
        {
            child.CurrentState = State.Failed;
            child.ExpireAt = now.Add(jobExpirationTimeout);

            _context.Set<JobLog>().Add(new JobLog
            {
                JobId = child.Id,
                EventType = "Failed",
                Timestamp = now,
                Level = "Warning",
                Message = "Failed — parent job was deleted",
            });

            if (child.Kind == JobKind.Batch)
            {
                var batchChildIds = await _context.Set<Job>()
                    .Where(x => x.ParentJobId == child.Id && x.CurrentState == State.Awaiting)
                    .Select(x => x.Id)
                    .ToListAsync(ct);

                await _context.Set<Job>()
                    .Where(x => x.ParentJobId == child.Id && x.CurrentState == State.Awaiting)
                    .ExecuteUpdateAsync(
                        x => x
                            .SetProperty(p => p.CurrentState, State.Failed)
                            .SetProperty(p => p.ExpireAt, now.Add(jobExpirationTimeout)),
                        ct);

                foreach (var batchChildId in batchChildIds)
                {
                    _context.Set<JobLog>().Add(new JobLog
                    {
                        JobId = batchChildId,
                        EventType = "Failed",
                        Timestamp = now,
                        Level = "Warning",
                        Message = "Failed — parent batch was deleted",
                    });
                }
            }
        }

        await _context.SaveChangesAsync(ct);

        return orphaned.Count;
    }
}
