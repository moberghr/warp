using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Events;
using Warp.Core.Notifications;

namespace Warp.Worker.Services;

/// <summary>
/// Runs one orchestration pass per iteration: finalize parents whose children have all
/// reached terminal state, activate continuations whose parent is now terminal, and fail
/// children of deleted parents. Wake-up on <c>JobFinalized</c> events (worker completions
/// and push notifications) is routed through
/// <see cref="ServerTaskSignals{TContext}.SignalJobFinalized"/>.
/// <para>
/// Activating a continuation is an enqueue site (rule 6.3): it flips rows from Awaiting to Enqueued, so it
/// announces them, built from the flip itself because <c>NotificationDispatch.CapturePending</c> sees only
/// Added rows. Buffered and fired from <see cref="OnCommittedAsync"/> like
/// <c>ScheduledJobActivation</c>, since <see cref="ExecuteAsync"/> runs inside the host's lock transaction.
/// Without it, idle workers slept through their backoff before seeing a released continuation: 1,000 jobs
/// in five batch-and-continuation pairs took ~22 s against ~3 s for the same jobs published directly.
/// </para>
/// </summary>
public sealed class Orchestrator<TContext> : IServerTask
    where TContext : DbContext
{
    private readonly DbContext _context;
    private readonly TimeProvider _time;
    private readonly WarpServerConfiguration _configuration;
    private readonly IWarpNotificationTransport _transport;
    private readonly ServerTaskSignals<TContext> _signals;
    private readonly List<Notification> _pendingNotifications = [];

    public Orchestrator(
        IWarpServerContext serverContext,
        TimeProvider time,
        IOptions<WarpServerConfiguration> configuration,
        IWarpNotificationTransport transport,
        ServerTaskSignals<TContext> signals)
    {
        _context = serverContext.Context;
        _time = time;
        _configuration = configuration.Value;
        _transport = transport;
        _signals = signals;
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

    // Post-commit hook (§8.25): called only after the lock transaction has committed, never on a
    // rollback, which leaves the buffer to die with this scoped instance.
    public async Task OnCommittedAsync(CancellationToken ct)
    {
        if (_pendingNotifications.Count == 0)
        {
            return;
        }

        var notifications = _pendingNotifications.ToList();
        _pendingNotifications.Clear();

        // CancellationToken.None: the activations are committed, so workers should hear about them even
        // if shutdown began mid-iteration.
        await NotificationDispatch.DispatchAsync(notifications, _signals, _transport, CancellationToken.None);
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
        // Deleted counts as SETTLED on both sides of the readiness check: a deleted child is
        // deliberately-skipped work (a Skip-mode [Mutex]/[RateLimit] rejecting a routed handler job —
        // reachable since the addon policy axis change — or an operator delete), not pending work.
        // Treating it as pending left the parent Awaiting/Processing forever with no path out.
        var readyParents = await _context.Set<Job>()
            .Where(p => (p.Kind == JobKind.Message || p.Kind == JobKind.Batch)
                && (p.CurrentState == State.Awaiting || p.CurrentState == State.Processing))
            .Where(p => !_context.Set<Job>()
                .Any(c => c.ParentJobId == p.Id && c.Kind == JobKind.Job
                    && c.CurrentState != State.Completed && c.CurrentState != State.Failed
                    && c.CurrentState != State.Deleted && c.CurrentState != State.Awaiting))
            .Where(p => _context.Set<Job>()
                .Any(c => c.ParentJobId == p.Id && c.Kind == JobKind.Job
                    && (c.CurrentState == State.Completed || c.CurrentState == State.Failed || c.CurrentState == State.Deleted)))
            .Take(_configuration.ServerTaskBatchSize)
            .ToListAsync(ct);

        if (readyParents.Count == 0)
        {
            return 0;
        }

        // Two-step fetch (§5.2): one follow-up query collects the distinct (parent, settled state) pairs
        // instead of an `_context.Set<Job>().Any(...)` subquery inside the Select projection (which EF
        // Core has translated unreliably across versions). One round-trip yields every per-state id-set.
        var parentIds = readyParents.ConvertAll(p => p.Id);
        var settledChildStates = await _context.Set<Job>()
            .Where(c => c.Kind == JobKind.Job)
            .Where(c => c.CurrentState == State.Failed || c.CurrentState == State.Completed || c.CurrentState == State.Deleted)
            .Where(c => c.ParentJobId != null && parentIds.Contains(c.ParentJobId.Value))
            .Select(c =>
                new
                {
                    ParentId = c.ParentJobId!.Value,
                    c.CurrentState,
                })
            .Distinct()
            .ToListAsync(ct);
        var failedParentIdSet = settledChildStates
            .Where(x => x.CurrentState == State.Failed)
            .Select(x => x.ParentId)
            .ToHashSet();
        var completedParentIdSet = settledChildStates
            .Where(x => x.CurrentState == State.Completed)
            .Select(x => x.ParentId)
            .ToHashSet();
        var deletedParentIdSet = settledChildStates
            .Where(x => x.CurrentState == State.Deleted)
            .Select(x => x.ParentId)
            .ToHashSet();

        // A Deleted child is settled, but what it MEANS depends on the parent's kind. A message fans out
        // one payload to N handlers, and a Skip-mode [Mutex]/[RateLimit] deleting a surplus routed child is
        // deliberately-skipped delivery — not failed work — so the message completes when any handler
        // completed and finalizes Deleted only when every child was skipped. A batch is a set of work
        // items the caller expects to run, and a Deleted child there is cancelled work (CancelBatch deletes
        // the descendants but not the batch row): under OnlyOnSucceeded any deleted child makes the batch
        // Deleted even when siblings completed — reporting it Completed would be a false green that fires
        // the success continuation over work that never ran. A Failed child still wins (Failed).
        // OnAnyFinishedState follows the Failed precedent: the parent finalizes Completed so its
        // continuation activates through the ordinary path instead of being failed as a deleted parent's
        // orphan in the same tick.
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var parent in readyParents)
        {
            var continuationOptions = parent.ContinuationOptions ?? ContinuationOptions.OnlyOnSucceeded;
            var anyFinishedState = continuationOptions == ContinuationOptions.OnAnyFinishedState;
            var hasFailedChildren = failedParentIdSet.Contains(parent.Id);
            var hasCancelledWork = parent.Kind == JobKind.Batch && deletedParentIdSet.Contains(parent.Id);

            if ((hasFailedChildren || hasCancelledWork) && !anyFinishedState)
            {
                parent.CurrentState = hasFailedChildren ? State.Failed : State.Deleted;
            }
            else if (anyFinishedState || completedParentIdSet.Contains(parent.Id))
            {
                parent.CurrentState = State.Completed;
            }
            else
            {
                parent.CurrentState = State.Deleted;
            }

            parent.ExpireAt = now.Add(jobExpirationTimeout);
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
        var queues = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in awaitingChildren)
        {
            var childId = child.Id;
            if (child.Kind == JobKind.Batch)
            {
                await _context.Set<Job>()
                    .Where(x => x.Id == childId && x.CurrentState == State.Awaiting)
                    .ExecuteUpdateAsync(x => x.SetProperty(p => p.CurrentState, State.Processing), ct);

                // The batch's children are what become runnable, and they may sit on other queues than
                // the batch row; read theirs before the flip, while the Awaiting filter still finds them.
                var childQueues = await _context.Set<Job>()
                    .AsNoTracking()
                    .Where(x => x.ParentJobId == childId && x.CurrentState == State.Awaiting && x.Kind == JobKind.Job)
                    .Select(x => x.Queue)
                    .Distinct()
                    .ToListAsync(ct);

                var flipped = await _context.Set<Job>()
                    .Where(x => x.ParentJobId == childId && x.CurrentState == State.Awaiting && x.Kind == JobKind.Job)
                    .ExecuteUpdateAsync(x => x.SetProperty(p => p.CurrentState, State.Enqueued), ct);

                if (flipped > 0)
                {
                    queues.UnionWith(childQueues);
                }

                activated += flipped;
            }
            else
            {
                var flipped = await _context.Set<Job>()
                    .Where(x => x.Id == childId && x.CurrentState == State.Awaiting)
                    .ExecuteUpdateAsync(x => x.SetProperty(p => p.CurrentState, State.Enqueued), ct);

                if (flipped > 0)
                {
                    queues.Add(child.Queue);
                }

                activated += flipped;
            }
        }

        await AnnounceAsync(queues, ct);

        return activated;
    }

    private async Task AnnounceAsync(HashSet<string> queues, CancellationToken ct)
    {
        if (queues.Count == 0)
        {
            return;
        }

        var notifications = queues.Select(x => new Notification(NotificationKind.JobEnqueued, x)).ToList();

        // Under the task host there is an ambient lock transaction and nothing above is durable yet, so the
        // wake waits for OnCommittedAsync. A direct caller (a test) has none: each update committed on its
        // own, and nothing would call the hook for it.
        if (_context.Database.CurrentTransaction != null)
        {
            _pendingNotifications.AddRange(notifications);

            return;
        }

        await NotificationDispatch.DispatchAsync(notifications, _signals, _transport, ct);
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
