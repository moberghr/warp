using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Events;
using Warp.Core.Handlers;
using Warp.Core.Helper;
using Warp.Core.Notifications;

namespace Warp.Worker.Services;

/// <summary>
/// Routes messages (parent rows with Kind=Message) to handlers by creating one child
/// Kind=Job per registered handler. Wake-up on <c>MessageEnqueued</c> push notifications
/// is routed through <see cref="ServerTaskSignals{TContext}.SignalMessageEnqueued"/>.
/// </summary>
public sealed class MessageRouter<TContext> : IServerTask
    where TContext : DbContext
{
    private readonly TContext _context;
    private readonly TimeProvider _time;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWarpSqlQueries<TContext> _sqlQueries;
    private readonly IWarpNotificationTransport _notificationTransport;
    private readonly ServerTaskSignals<TContext> _signals;
    private readonly WarpWorkerConfiguration _configuration;

    public MessageRouter(
        TContext context,
        TimeProvider time,
        IServiceScopeFactory scopeFactory,
        IWarpSqlQueries<TContext> sqlQueries,
        IWarpNotificationTransport notificationTransport,
        ServerTaskSignals<TContext> signals,
        IOptions<WarpWorkerConfiguration> configuration)
    {
        _context = context;
        _time = time;
        _scopeFactory = scopeFactory;
        _sqlQueries = sqlQueries;
        _notificationTransport = notificationTransport;
        _signals = signals;
        _configuration = configuration.Value;
    }

    public string Name => "MessageRouting";

    // Distributed advisory lock serializes routing across servers. With the new atomic
    // batch-claim (<see cref="IWarpSqlQueries{TContext}.ClaimEnqueuedMessagesAsync"/>)
    // the lock is no longer strictly required for correctness — SKIP LOCKED guarantees
    // distinct rows across concurrent callers — but the lock is kept because:
    //   1. Handler discovery + child-job creation happens in-memory between claim and
    //      commit, so two servers concurrently routing N messages still buy N×handler
    //      lookups of identical work and 2× the SaveChanges traffic for the same outcome.
    //   2. Existing tests assert "exactly one server routes at a time" semantics; relaxing
    //      that is a separate decision.
    public string? LockKey => "warp:message-routing";

    public TimeSpan? DefaultInterval => _configuration.MessageRoutingInterval;

    public IEnumerable<ServerTaskSignal> Signals => [ServerTaskSignal.MessageEnqueued];

    // The whole iteration runs inside an explicit transaction we open in ExecuteAsync, so
    // we cannot use the xact-lock model (which would wrap ExecuteAsync in its own outer
    // transaction). The Medallion session-scoped lock fits: it holds a connection from the
    // pool while we open / commit our own transaction on a separate connection inside.
    public bool LocksWithTransaction => false;

    public async Task<string?> ExecuteAsync(CancellationToken ct)
    {
        var routed = await RunMessageRoutingAsync(ct);

        return routed > 0 ? $"Routed {routed} messages" : null;
    }

    internal async Task<int> RunMessageRoutingAsync(CancellationToken ct)
    {
        var batchSize = _configuration.ServerTaskBatchSize;

        // The transaction wraps the atomic claim AND the SaveChanges that adds child jobs,
        // so a process crash anywhere in between rolls everything back — messages stay in
        // Enqueued and the next router tick re-routes them. Without this wrap, the
        // ClaimEnqueuedMessagesAsync UPDATE auto-commits on its own, and a crash before
        // SaveChanges would leave the message rows orphaned in Processing with no children.
        await using var transaction = await _context.Database.BeginTransactionAsync(ct);

        var messages = await _sqlQueries.ClaimEnqueuedMessagesAsync(_context, batchSize, ct);
        if (messages.Count == 0)
        {
            await transaction.RollbackAsync(ct);
            return 0;
        }

        var routed = RouteInMemory(messages);

        // CapturePending walks the change tracker BEFORE SaveChanges so the snapshot still
        // sees the Added entries. The push notifications fire after the commit so subscribers
        // wake to visible rows, not uncommitted ones.
        var pending = NotificationDispatch.CapturePending(_context);
        await _context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await NotificationDispatch.DispatchAsync(pending, _signals, _notificationTransport, ct);

        return routed;
    }

    private int RouteInMemory(List<Job> messages)
    {
        var routed = 0;

        // Disable change detection across the in-memory loop. At batchSize=1000 the
        // auto-detect O(tracked-entity-count) sweep on every Add would dominate. The
        // happy-path message rows need no UPDATE — the claim SQL already committed
        // Processing to the DB and our in-memory tracked entity reflects that same
        // value, so EF has nothing to write. The error branches DO change CurrentState
        // post-claim (Processing → Failed), so they signal that explicitly with
        // Entry().State = Modified, which is the contract under AutoDetectChangesEnabled = false.
        var previousAutoDetect = _context.ChangeTracker.AutoDetectChangesEnabled;
        _context.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            foreach (var message in messages)
            {
                var now = _time.GetUtcNow().UtcDateTime;

                var messageType = Type.GetType(message.Type!);
                if (messageType == null)
                {
                    message.CurrentState = State.Failed;
                    _context.Entry(message).State = EntityState.Modified;
                    _context.Set<JobLog>().Add(new JobLog
                    {
                        JobId = message.Id,
                        EventType = "Failed",
                        Timestamp = now,
                        Level = "Error",
                        Message = $"Unknown message type: {message.Type}",
                    });
                    continue;
                }

                using var handlerScope = _scopeFactory.CreateScope();
                var handlerTypes = JobDispatcher.DiscoverMessageHandlers(messageType, handlerScope.ServiceProvider);

                if (handlerTypes.Count == 0)
                {
                    message.CurrentState = State.Failed;
                    _context.Entry(message).State = EntityState.Modified;
                    _context.Set<JobLog>().Add(new JobLog
                    {
                        JobId = message.Id,
                        EventType = "Failed",
                        Timestamp = now,
                        Level = "Error",
                        Message = $"No handlers registered for message type {messageType.Name}",
                    });
                    continue;
                }

                foreach (var handlerType in handlerTypes)
                {
                    var job = JobHelper.CreateJob(
                        message: message.Message!,
                        type: message.Type!,
                        scheduleTime: null,
                        queue: message.Queue,
                        parentId: message.Id,
                        state: State.Enqueued,
                        now: now);

                    job.HandlerType = handlerType.AssemblyQualifiedName;
                    job.TraceId = message.TraceId;
                    job.ParentSpanId = message.ParentSpanId;
                    job.Metadata = message.Metadata;

                    _context.Set<Job>().Add(job);
                    _context.Set<JobLog>().Add(new JobLog
                    {
                        JobId = job.Id,
                        EventType = "Created",
                        Timestamp = now,
                        Level = "Information",
                        Message = $"Job {job.Id} created from message {message.Id}",
                    });
                }

                // Per-message audit row: when MessageRouter actually picked up this message
                // and how many handlers got fan-out. Atomic with the claim-flip + child
                // INSERTs because they all commit in the single end-of-batch SaveChanges
                // inside the surrounding transaction. Operators reading the dashboard can
                // correlate "Created" (publisher commit) → "Routed" (this row) → children's
                // "Created" → handler activity, closing the gap that previously made
                // MessageRouter pickup latency invisible per-row.
                _context.Set<JobLog>().Add(new JobLog
                {
                    JobId = message.Id,
                    EventType = "Routed",
                    Timestamp = now,
                    Level = "Information",
                    Message = $"Routed to {handlerTypes.Count} handler(s)",
                });

                routed++;
            }

            return routed;
        }
        finally
        {
            _context.ChangeTracker.AutoDetectChangesEnabled = previousAutoDetect;
        }
    }
}
