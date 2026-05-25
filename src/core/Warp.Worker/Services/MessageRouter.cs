using Microsoft.EntityFrameworkCore;
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
    private readonly WarpWorkerConfiguration _configuration;

    public MessageRouter(
        TContext context,
        TimeProvider time,
        IServiceScopeFactory scopeFactory,
        IWarpSqlQueries<TContext> sqlQueries,
        IWarpNotificationTransport notificationTransport,
        IOptions<WarpWorkerConfiguration> configuration)
    {
        _context = context;
        _time = time;
        _scopeFactory = scopeFactory;
        _sqlQueries = sqlQueries;
        _notificationTransport = notificationTransport;
        _configuration = configuration.Value;
    }

    public string Name => "MessageRouting";

    // Distributed advisory lock serializes routing across servers. This is necessary for
    // correctness, not just optimisation: the Job entity has no concurrency token, and
    // `LockNextEnqueuedMessageAsync`'s `FOR NO KEY UPDATE SKIP LOCKED` releases the row
    // lock at statement end since this loop doesn't wrap each iteration in a transaction.
    // Without the advisory lock, two servers could load the same message row, both
    // discover handlers, and both insert duplicate child Job rows — handlers would fire
    // twice per message.
    // <para>
    // For high-volume multi-server message routing this becomes a throughput ceiling
    // (only one server routes at a time). The right scaling fix is an atomic
    // UPDATE-RETURNING claim like <c>WarpDispatcher.ClaimEnqueuedJobsAsync</c> plus
    // stale-message recovery. Tracked as a follow-up.
    // </para>
    public string? LockKey => "warp:message-routing";

    public TimeSpan? DefaultInterval => _configuration.MessageRoutingInterval;

    public IEnumerable<ServerTaskSignal> Signals => [ServerTaskSignal.MessageEnqueued];

    // Hard requirement, not stylistic. RunMessageRoutingAsync commits once per message
    // (SaveChangesAsync inside the for-loop), so a single ExecuteAsync call produces
    // multiple distinct transactions. The xact-lock model wraps the entire ExecuteAsync
    // in ONE transaction — folding this task into that path would prevent intermediate
    // commits and either deadlock or batch all routed messages into a single mega-tx
    // (defeating the point of per-message visibility for retry / failure isolation).
    // The Medallion session-scoped lock (selected by LockKey above + this flag = false)
    // is what allows multi-iteration commits inside one held lock.
    public bool LocksWithTransaction => false;

    public async Task<string?> ExecuteAsync(CancellationToken ct)
    {
        var routed = await RunMessageRoutingAsync(ct);

        return routed > 0 ? $"Routed {routed} messages" : null;
    }

    internal async Task<int> RunMessageRoutingAsync(CancellationToken ct)
    {
        var totalRouted = 0;
        var batchSize = _configuration.ServerTaskBatchSize;

        // Bound the per-call loop: if there's a backlog of N >> batchSize messages, we
        // process up to batchSize then return. ServerTaskLoop's RerunImmediately = true
        // re-ticks us instantly so the backlog still drains, but each iteration is short
        // enough for cancellation to propagate and other servers (with the lock dropped
        // here) can pick up rows we haven't reached yet.
        for (var i = 0; i < batchSize; i++)
        {
            var message = await _sqlQueries.LockNextEnqueuedMessageAsync(_context, ct);

            if (message == null)
            {
                break;
            }

            var messageType = Type.GetType(message.Type!);
            if (messageType == null)
            {
                message.CurrentState = State.Failed;
                _context.Set<JobLog>().Add(new JobLog
                {
                    JobId = message.Id,
                    EventType = "Failed",
                    Timestamp = _time.GetUtcNow().UtcDateTime,
                    Level = "Error",
                    Message = $"Unknown message type: {message.Type}",
                });
                await _context.SaveChangesAsync(ct);

                continue;
            }

            using var handlerScope = _scopeFactory.CreateScope();
            var handlerTypes = JobDispatcher.DiscoverMessageHandlers(messageType, handlerScope.ServiceProvider);

            if (handlerTypes.Count == 0)
            {
                message.CurrentState = State.Failed;
                _context.Set<JobLog>().Add(new JobLog
                {
                    JobId = message.Id,
                    EventType = "Failed",
                    Timestamp = _time.GetUtcNow().UtcDateTime,
                    Level = "Error",
                    Message = $"No handlers registered for message type {messageType.Name}",
                });
                await _context.SaveChangesAsync(ct);

                continue;
            }

            var now = _time.GetUtcNow().UtcDateTime;
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

            message.CurrentState = State.Processing;
            _context.Set<JobLog>().Add(new JobLog
            {
                JobId = message.Id,
                EventType = "Processing",
                Timestamp = now,
                Level = "Information",
                Message = $"Routed to {handlerTypes.Count} handler{(handlerTypes.Count == 1 ? string.Empty : "s")}",
            });

            // Capture pending JobEnqueued notifications before commit so push wakes the
            // dispatcher as soon as the child rows are visible. Without this, push works
            // for direct enqueues but messages-via-routing fall back to polling.
            var pending = NotificationDispatch.CapturePending(_context);
            await _context.SaveChangesAsync(ct);
            await NotificationDispatch.FireAsync(_notificationTransport, pending, ct);

            totalRouted++;
        }

        return totalRouted;
    }
}
