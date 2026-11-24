using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
/// <see cref="ServerTaskSignals{TContext}.SignalJobFinalized()"/>.
/// <para>
/// A wake names the parents of the jobs that finished, and a pass checks those. Every parent in flight
/// is checked only on a sweep: once per <c>OrchestrationInterval</c>, and whenever a wake could not
/// name its parents. The sweep is the backstop for a wake that never arrived (a crashed process, a
/// delete from a process with no server while database push is off).
/// </para>
/// <para>
/// Activating a continuation is an enqueue site (rule 6.3): it flips rows from Awaiting to Enqueued, so it
/// announces them, built from the flip itself because <c>NotificationDispatch.CapturePending</c> sees only
/// Added rows. Buffered and fired from <see cref="OnCommittedAsync"/> like
/// <c>ScheduledJobActivation</c>, since <see cref="ExecuteAsync"/> runs inside the host's lock transaction.
/// Without it, idle workers slept through their backoff before seeing a released continuation: 1,000 jobs
/// in five batch-and-continuation pairs took ~22 s against ~3 s for the same jobs published directly.
/// </para>
/// </summary>
public sealed class Orchestrator<TContext> : IServerTask, IDisposable
    where TContext : DbContext
{
    private readonly DbContext _context;
    private readonly TimeProvider _time;
    private readonly WarpServerConfiguration _configuration;
    private readonly IWarpNotificationTransport _transport;
    private readonly ServerTaskSignals<TContext> _signals;
    private readonly List<Notification> _pendingNotifications = [];

    // What this pass took from the signals, until its work is known to be committed.
    private Guid[] _takenParents = [];
    private Guid[] _takenSettledJobs = [];
    private bool _takenSweep;

    public Orchestrator(
        IWarpServerContext serverContext,
        [FromKeyedServices(DatabaseClock.Key)] TimeProvider time,
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
        ForgetTakenWork();
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
        // Which parents to look at: the ones a finished job named, and all of them when a sweep is due.
        // A pass that throws gives the work back, since the signal that brought it will not come again.
        // So does one whose work is rolled back after it returned (see Dispose).
        var (hintedParents, sweep) = _signals.TakeFinalizedParents(_configuration.OrchestrationInterval);
        var settledJobs = _signals.TakeSettledJobs();
        _takenParents = [.. _takenParents, .. hintedParents];
        _takenSettledJobs = [.. _takenSettledJobs, .. settledJobs];
        _takenSweep |= sweep;
        try
        {
            return await RunOrchestrationCoreAsync(hintedParents, settledJobs, sweep, ct);
        }
        catch
        {
            ReturnTakenWork();

            throw;
        }
    }

    /// <summary>
    /// Gives back the work of a pass that was never committed. The task host runs
    /// <see cref="ExecuteAsync"/> inside a transaction and calls <see cref="OnCommittedAsync"/> only after
    /// the commit, then disposes the pass's scope. A commit that fails, or a run cancelled because its
    /// lease was lost, comes after <see cref="ExecuteAsync"/> returned: the work is rolled back, and the
    /// parents it was told about would otherwise be forgotten until the next sweep.
    /// </summary>
    public void Dispose() => ReturnTakenWork();

    private void ReturnTakenWork()
    {
        _signals.ReturnFinalizedParents(_takenParents, _takenSweep);
        _signals.NoteFinalized([], _takenSettledJobs);
        ForgetTakenWork();
    }

    private void ForgetTakenWork()
    {
        _takenParents = [];
        _takenSettledJobs = [];
        _takenSweep = false;
    }

    private async Task<bool> RunOrchestrationCoreAsync(Guid[] hintedParents, Guid[] settledJobs, bool sweep, CancellationToken ct)
    {
        var jobExpirationTimeout = _configuration.JobExpirationTimeout;

        // The jobs that reached a terminal state: the ones the wakes named, plus every parent this pass
        // finalizes. Each may be what an awaiting continuation waits on.
        var settled = new List<Guid>(settledJobs);

        var finalized = 0;
        foreach (var chunk in hintedParents.Chunk(ParentIdsPerStatement))
        {
            var readyHinted = await HintedReadyParentsQuery(_context, chunk).ToListAsync(ct);
            finalized += await FinalizeParentsAsync(readyHinted, settled, jobExpirationTimeout, ct);
            _context.ChangeTracker.Clear();
        }

        if (sweep)
        {
            // Bound the candidate set so one iteration can't churn through tens of thousands of
            // parents while holding the orchestration lock. A full slice means more may be waiting, and
            // the re-run that follows a pass that did work (RerunImmediately) has to sweep again to
            // find them.
            var readySwept = await ReadyParentsQuery(_context, _configuration.ServerTaskBatchSize).ToListAsync(ct);
            if (readySwept.Count >= _configuration.ServerTaskBatchSize)
            {
                _signals.RequestParentSweep();
            }

            finalized += await FinalizeParentsAsync(readySwept, settled, jobExpirationTimeout, ct);
            _context.ChangeTracker.Clear();
        }

        // After the finalization above, so a parent it has just completed releases its continuation in
        // this same pass.
        var (readyParents, deletedParents) = await LoadAwaitedParentsAsync(settled, sweep, ct);
        var activated = await ActivateContinuationsAsync(readyParents, ct);
        _context.ChangeTracker.Clear();
        var cleaned = await FailChildrenOfDeletedParentsAsync(deletedParents, jobExpirationTimeout, ct);
        _context.ChangeTracker.Clear();

        return finalized > 0 || activated > 0 || cleaned > 0;
    }

    // Internal and static so OrchestratorReadyParentsPlanTests can measure the pages THIS query reads
    // (rule 6.11), not a copy of it.
    //
    // The shape is load-bearing, not style (same lesson as the claim, rule 6.9). Each kind is its own
    // branch with a SCALAR kind and an ORDER BY the (Kind, CurrentState, CreateTime) index supplies,
    // so the planner takes that index without needing a good row estimate. Written as one
    // Kind IN (Message, Batch) AND CurrentState IN (...) predicate, it estimated one row either way
    // and picked (CurrentState, ScheduleTime) with Kind as a Filter. Every job passes through
    // Processing, so that index range is full of entries for rows that have since left it until
    // vacuum catches up, and the query walked all of them to return nothing: measured 10,004 pages
    // per call against 6, on a table with no batch or message in it.
    internal static IQueryable<Job> ReadyParentsQuery(DbContext context, int batchSize)
    {
        var settledParents = WhereChildrenSettled(
            context,
            context.Set<Job>().Where(p => p.CurrentState == State.Awaiting || p.CurrentState == State.Processing));

        var readyMessages = settledParents
            .Where(p => p.Kind == JobKind.Message)
            .OrderBy(p => p.CurrentState)
            .ThenBy(p => p.CreateTime)
            .Take(batchSize);

        var readyBatches = settledParents
            .Where(p => p.Kind == JobKind.Batch)
            .OrderBy(p => p.CurrentState)
            .ThenBy(p => p.CreateTime)
            .Take(batchSize);

        return readyMessages
            .Concat(readyBatches)
            .Take(batchSize);
    }

    // The same readiness check for the parents a finished job named (rule 6.11), found by primary key.
    // The sweep above reads every parent in flight to find the ready ones, about 11 pages each on
    // PostgreSQL, and it ran on every job completion: 10,924 pages per pass with 1,000 messages in
    // flight and none ready. This reads the named parents only, whatever else is in flight.
    internal static IQueryable<Job> HintedReadyParentsQuery(DbContext context, IReadOnlyCollection<Guid> parentIds) =>
        WhereChildrenSettled(
            context,
            context.Set<Job>()
                .Where(p => parentIds.Contains(p.Id))
                .Where(p => p.Kind == JobKind.Message || p.Kind == JobKind.Batch)
                .Where(p => p.CurrentState == State.Awaiting || p.CurrentState == State.Processing));

    // Deleted counts as SETTLED on both sides of the readiness check: a deleted child is
    // deliberately-skipped work (a Skip-mode [Mutex]/[RateLimit] rejecting a routed handler job, or an
    // operator delete), not pending work. Treating it as pending left the parent Awaiting/Processing
    // forever with no path out.
    private static IQueryable<Job> WhereChildrenSettled(DbContext context, IQueryable<Job> parents) =>
        parents
            .Where(p => !context.Set<Job>()
                .Any(c => c.ParentJobId == p.Id && c.Kind == JobKind.Job
                    && c.CurrentState != State.Completed && c.CurrentState != State.Failed
                    && c.CurrentState != State.Deleted && c.CurrentState != State.Awaiting))
            .Where(p => context.Set<Job>()
                .Any(c => c.ParentJobId == p.Id && c.Kind == JobKind.Job
                    && (c.CurrentState == State.Completed || c.CurrentState == State.Failed || c.CurrentState == State.Deleted)));

    // How many parent ids go into one statement, and how many awaited parents one pass looks at in full.
    private const int ParentIdsPerStatement = 1000;

    // How many awaited parents one sweep looks at when there are more than a pass takes in full.
    private const int AwaitedParentsPerSweep = 5 * ParentIdsPerStatement;

    // "Which awaiting rows can move?" is answered in three steps, each of which can only be served one
    // way, and that is the point (rule 6.11, a third query). It used to be one statement per question:
    // awaiting rows WHERE EXISTS(parent WHERE id = the row's parent AND state = Completed). PostgreSQL
    // was free to find that parent through the state index and check the id afterwards, and with stale
    // statistics it did: for EACH awaiting row it read EVERY Completed row in the table and discarded
    // all but one (126,250 rows discarded for 505 awaiting rows and 250 completed ones, 5,063 pages per
    // call, on almost every job completion). The cost was awaiting rows times completed rows.
    //
    // 1. the DISTINCT parents that awaiting rows wait on: one pass over the Awaiting range
    // 2. those parents' states, by primary key: a batch of 100 jobs waits on ONE parent, so this is one
    //    lookup where a per-row check made a hundred
    // 3. the awaiting children of the parents that turned out ready (or deleted), by parent id
    //
    // Internal and static so OrchestratorReadyParentsPlanTests measures the pages THESE read, not a copy.
    internal static IQueryable<Guid> AwaitedParentIdsQuery(DbContext context) =>
        context.Set<Job>()
            .Where(c => c.CurrentState == State.Awaiting && c.ParentJobId != null)
            .Select(c => c.ParentJobId!.Value)
            .Distinct();

    // Step 1 for a known set of jobs: which of them does an awaiting row wait on? Served by the
    // (ParentJobId, CurrentState) index with both columns bound, so its cost follows the jobs asked
    // about and not the number of awaiting rows.
    internal static IQueryable<Guid> AwaitedParentIdsAmongQuery(DbContext context, IReadOnlyCollection<Guid> jobIds) =>
        context.Set<Job>()
            .Where(c => c.CurrentState == State.Awaiting && c.ParentJobId != null)
            .Where(c => jobIds.Contains(c.ParentJobId!.Value))
            .Select(c => c.ParentJobId!.Value)
            .Distinct();

    internal static IQueryable<AwaitedParent> AwaitedParentStatesQuery(DbContext context, IReadOnlyCollection<Guid> parentIds) =>
        context.Set<Job>()
            .Where(p => parentIds.Contains(p.Id))
            .Select(p =>
                new AwaitedParent
                {
                    Id = p.Id,
                    CurrentState = p.CurrentState,
                    ContinuationOptions = p.ContinuationOptions,
                });

    internal static IQueryable<Job> AwaitingChildrenOfQuery(DbContext context, IReadOnlyCollection<Guid> parentIds, int batchSize) =>
        context.Set<Job>()
            .Where(c => c.CurrentState == State.Awaiting && c.ParentJobId != null)
            .Where(c => parentIds.Contains(c.ParentJobId!.Value))
            .Take(batchSize);

    // Steps 1 and 2, once per pass, for both the activation and the deleted-parent sweep.
    //
    // Up to ParentIdsPerStatement awaited parents, every pass reads all of them: one scan of the
    // awaiting rows and one lookup statement, a handful of pages. Past that the same pass would cost a
    // scan of every awaiting row and a statement per thousand parents on every job completion (100,000
    // continuations on 100,000 parents: 100 statements a pass), so the Orchestrator changes how it
    // works: it asks only about the jobs it was told reached a terminal state, and reads the whole
    // awaited set on a sweep alone, AwaitedParentsPerSweep parents at a time, carrying on each sweep
    // from where the last one stopped.
    private async Task<(List<Guid> Ready, List<Guid> Deleted)> LoadAwaitedParentsAsync(List<Guid> settled, bool sweep, CancellationToken ct)
    {
        var ready = new List<Guid>();
        var deleted = new List<Guid>();

        List<Guid> parentIds;
        if (_signals.ManyAwaitedParents && !sweep)
        {
            parentIds = [];
            foreach (var chunk in settled.Chunk(ParentIdsPerStatement))
            {
                parentIds.AddRange(await AwaitedParentIdsAmongQuery(_context, chunk).ToListAsync(ct));
            }
        }
        else
        {
            var allAwaited = await AwaitedParentIdsQuery(_context).ToListAsync(ct);
            _signals.ManyAwaitedParents = allAwaited.Count > ParentIdsPerStatement;
            if (_signals.ManyAwaitedParents)
            {
                (parentIds, _signals.AwaitedSweepCursor) = NextSweepSlice(allAwaited, _signals.AwaitedSweepCursor, settled, AwaitedParentsPerSweep);
            }
            else
            {
                parentIds = allAwaited;
            }
        }

        foreach (var chunk in parentIds.Chunk(ParentIdsPerStatement))
        {
            var parents = await AwaitedParentStatesQuery(_context, chunk)
                .AsNoTracking()
                .ToListAsync(ct);

            foreach (var parent in parents)
            {
                if (parent.CurrentState == State.Completed
                    || (parent.CurrentState == State.Failed && parent.ContinuationOptions == ContinuationOptions.OnAnyFinishedState))
                {
                    ready.Add(parent.Id);
                }
                else if (parent.CurrentState == State.Deleted)
                {
                    deleted.Add(parent.Id);
                }
            }
        }

        return (ready, deleted);
    }

    // The next slice of a large awaited set, in id order from where the last sweep stopped and round
    // again, plus the awaited parents this pass was told about, which must not wait for their turn.
    internal static (List<Guid> Slice, Guid Cursor) NextSweepSlice(List<Guid> allAwaited, Guid cursor, IReadOnlyCollection<Guid> settled, int take)
    {
        allAwaited.Sort();
        var start = allAwaited.BinarySearch(cursor);
        start = start >= 0 ? start + 1 : ~start;

        var slice = new HashSet<Guid>();
        for (var i = 0; i < take && i < allAwaited.Count; i++)
        {
            cursor = allAwaited[(start + i) % allAwaited.Count];
            slice.Add(cursor);
        }

        if (settled.Count > 0)
        {
            var awaited = allAwaited.ToHashSet();
            slice.UnionWith(settled.Where(awaited.Contains));
        }

        return ([.. slice], cursor);
    }

    private async Task<int> FinalizeParentsAsync(List<Job> readyParents, List<Guid> settled, TimeSpan jobExpirationTimeout, CancellationToken ct)
    {
        if (readyParents.Count == 0)
        {
            return 0;
        }

        settled.AddRange(readyParents.Select(x => x.Id));

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

    private async Task<int> ActivateContinuationsAsync(List<Guid> readyParents, CancellationToken ct)
    {
        if (readyParents.Count == 0)
        {
            return 0;
        }

        // Every id here is ready, so taking the first slice starves nothing: the rest go next pass.
        var awaitingChildren = await AwaitingChildrenOfQuery(_context, [.. readyParents.Take(ParentIdsPerStatement)], _configuration.ServerTaskBatchSize)
            .AsNoTracking()
            .ToListAsync(ct);

        if (awaitingChildren.Count == 0)
        {
            return 0;
        }

        // More was ready than this pass takes. The re-run that follows finds the rest by looking, unless
        // the awaited set is too large to look through, so name them for it.
        if (readyParents.Count > ParentIdsPerStatement || awaitingChildren.Count >= _configuration.ServerTaskBatchSize)
        {
            _signals.NoteFinalized([], readyParents);
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

    private async Task<int> FailChildrenOfDeletedParentsAsync(List<Guid> deletedParents, TimeSpan jobExpirationTimeout, CancellationToken ct)
    {
        if (deletedParents.Count == 0)
        {
            return 0;
        }

        var now = _time.GetUtcNow().UtcDateTime;

        var orphaned = await AwaitingChildrenOfQuery(_context, [.. deletedParents.Take(ParentIdsPerStatement)], _configuration.ServerTaskBatchSize)
            .ToListAsync(ct);

        if (orphaned.Count == 0)
        {
            return 0;
        }

        if (deletedParents.Count > ParentIdsPerStatement || orphaned.Count >= _configuration.ServerTaskBatchSize)
        {
            _signals.NoteFinalized([], deletedParents);
        }

        // A row failed here reached a terminal state, and a continuation of its own may wait on it.
        var failed = orphaned.ConvertAll(x => x.Id);

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

                failed.AddRange(batchChildIds);

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
        _signals.NoteFinalized([], failed);

        return orphaned.Count;
    }

    // A parent some awaiting row is waiting on: what decides whether that row may move.
    internal sealed class AwaitedParent
    {
        public Guid Id { get; init; }

        public State CurrentState { get; init; }

        public ContinuationOptions? ContinuationOptions { get; init; }
    }
}
