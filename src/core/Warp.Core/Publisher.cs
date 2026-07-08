using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Events;
using Warp.Core.Handlers;
using Warp.Core.Helper;
using Warp.Core.Logging;
using Warp.Core.Notifications;

namespace Warp.Core;

/// <summary>
/// Persists jobs and messages to the Warp store via the calling scope's <c>DbContext</c>.
/// The publish methods only <em>stage</em> rows on the change tracker — nothing is committed (and
/// no worker can pick the work up) until <see cref="SaveChangesAsync"/> runs, which is what makes
/// the outbox pattern work: enqueue alongside your own entities and a single
/// <c>SaveChanges</c> either commits everything or nothing. All methods return the new job's id.
/// Resolved as a scoped service; inject <c>IPublisher</c>.
/// </summary>
public interface IPublisher
{
    /// <summary>Stages an <see cref="IMessage"/> on the default queue. The message fans out to all
    /// registered <c>IMessageHandler&lt;T&gt;</c> as independent child jobs once routed.</summary>
    /// <returns>The id of the created message job.</returns>
    Task<Guid> Publish<T>(T message)
        where T : class, IMessage;

    /// <summary>Stages an <see cref="IMessage"/> on the given queue (null = default queue).</summary>
    /// <returns>The id of the created message job.</returns>
    Task<Guid> Publish<T>(T message, string? queue)
        where T : class, IMessage;

    /// <summary>Stages an <see cref="IJob"/> for immediate execution on the default queue.</summary>
    /// <returns>The id of the created job.</returns>
    Task<Guid> Enqueue<T>(T job)
        where T : class, IJob;

    /// <summary>Stages an <see cref="IJob"/> for immediate execution on the given queue (null = default).</summary>
    /// <returns>The id of the created job.</returns>
    Task<Guid> Enqueue<T>(T job, string? queue)
        where T : class, IJob;

    /// <summary>Stages an <see cref="IJob"/> as a continuation of <paramref name="parentJobId"/> —
    /// it runs after the parent reaches a terminal state (subject to the parent's continuation options).</summary>
    /// <returns>The id of the created job.</returns>
    Task<Guid> Enqueue<T>(T job, Guid parentJobId)
        where T : class, IJob;

    /// <summary>Stages an <see cref="IJob"/> as a continuation of <paramref name="parentJobId"/> on the given queue.</summary>
    /// <returns>The id of the created job.</returns>
    Task<Guid> Enqueue<T>(T job, Guid parentJobId, string? queue)
        where T : class, IJob;

    /// <summary>Stages an <see cref="IJob"/> using a fully-specified <see cref="JobParameters"/>
    /// (schedule time, queue, parent id, ad-hoc metadata).</summary>
    /// <returns>The id of the created job.</returns>
    Task<Guid> Enqueue<T>(T job, JobParameters jobParameters)
        where T : class, IJob;

    /// <summary>Stages an <see cref="IJob"/> to become eligible for execution at
    /// <paramref name="scheduleTime"/> (UTC). It sits in <c>State.Scheduled</c> until then; a
    /// past time runs immediately.</summary>
    /// <returns>The id of the created job.</returns>
    Task<Guid> Schedule<T>(T job, DateTime scheduleTime)
        where T : class, IJob;

    /// <summary>Schedules an <see cref="IJob"/> for <paramref name="scheduleTime"/> (UTC) on the given queue.</summary>
    /// <returns>The id of the created job.</returns>
    Task<Guid> Schedule<T>(T job, DateTime scheduleTime, string? queue)
        where T : class, IJob;

    /// <summary>Schedules an <see cref="IJob"/> for <paramref name="scheduleTime"/> (UTC) as a continuation of <paramref name="parentJobId"/>.</summary>
    /// <returns>The id of the created job.</returns>
    Task<Guid> Schedule<T>(T job, DateTime scheduleTime, Guid parentJobId)
        where T : class, IJob;

    /// <summary>Schedules an <see cref="IJob"/> for <paramref name="scheduleTime"/> (UTC) as a continuation of <paramref name="parentJobId"/> on the given queue.</summary>
    /// <returns>The id of the created job.</returns>
    Task<Guid> Schedule<T>(T job, DateTime scheduleTime, Guid parentJobId, string? queue)
        where T : class, IJob;

    /// <summary>Commits all staged jobs/messages (and any other tracked changes on the scope's
    /// <c>DbContext</c>) in one transaction, then dispatches push notifications for what was saved.
    /// Nothing published becomes visible to workers until this completes.</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

public class Publisher<TContext> : IPublisher
    where TContext : DbContext
{
    private readonly TContext _context;
    private readonly TimeProvider _timeProvider;
    private readonly IServiceProvider _serviceProvider;
    private readonly IWarpNotificationTransport _notificationTransport;
    private readonly ServerTaskSignals<TContext> _signals;

    public Publisher(TContext context, TimeProvider timeProvider, IServiceProvider serviceProvider, IWarpNotificationTransport notificationTransport, ServerTaskSignals<TContext> signals)
    {
        WarpModelGuard.EnsureWarpModelApplied(context);

        _context = context;
        _timeProvider = timeProvider;
        _serviceProvider = serviceProvider;
        _notificationTransport = notificationTransport;
        _signals = signals;
    }

    // --- IMessage: create Message-kind Job ---
    public async Task<Guid> Publish<T>(T message)
        where T : class, IMessage
    {
        return await CreateMessage(message);
    }

    public async Task<Guid> Publish<T>(T message, string? queue)
        where T : class, IMessage
    {
        return await CreateMessage(message, queue);
    }

    private async Task<Guid> CreateMessage<T>(T message, string? queue = null)
        where T : class, IMessage
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var resolvedQueue = queue ?? "default";

        var publishCtx = await RunPublishPipeline(message, seed: null, CancellationToken.None);

        // Snapshot caller's trace context before opening the producer span. While the producer
        // span is open, Activity.Current is the producer; reading these afterwards would parent
        // the consumer to the one-tick producer span instead of to the actual caller.
        var callerTraceId = Activity.Current?.TraceId;
        var callerSpanId = Activity.Current?.SpanId;

        using var producerSpan = WarpTelemetry.StartProducerActivity(resolvedQueue, WarpTelemetryAttributes.OperationSend);

        // ITimeoutMessage auto-schedules itself: a delay > 0 parks the row in Scheduled until
        // ScheduledJobActivation flips it to Enqueued. Delay <= 0 falls through to immediate
        // delivery (mirrors Schedule semantics elsewhere — past scheduleTime → Enqueued).
        var (scheduleTime, state) = ResolveDelivery(message, now);

        var msg = new Job
        {
            Kind = JobKind.Message,
            Type = message.GetType().AssemblyQualifiedName!,
            Message = JsonSerializer.Serialize(message),
            Queue = resolvedQueue,
            CreateTime = now,
            ScheduleTime = scheduleTime,
            CurrentState = state,
            JobCount = 0,
            Metadata = SerializeMetadata(publishCtx.Metadata),
        };

        // Trace propagation: inherit from execution context if inside a handler
        var executionContext = JobExecutionContext.Current;
        if (executionContext != null)
        {
            msg.TraceId = executionContext.TraceId;
            msg.SpawnedByJobId = executionContext.JobId;
        }
        else if (callerTraceId is { } msgActivityTrace)
        {
            msg.TraceId = new Guid(msgActivityTrace.ToHexString());
        }
        else
        {
            msg.TraceId = msg.Id;
        }

        if (callerSpanId is { } msgSpanId && msgSpanId != default)
        {
            msg.ParentSpanId = msgSpanId.ToHexString();
        }

        if (producerSpan != null)
        {
            producerSpan.SetTag(WarpTelemetryAttributes.MessagingMessageId, msg.Id.ToString());
            producerSpan.SetTag(WarpTelemetryAttributes.MessagingConversationId, msg.TraceId.ToString());
            producerSpan.SetTag(WarpTelemetryAttributes.WarpJobKind, JobKind.Message.ToString());
            producerSpan.SetTag(WarpTelemetryAttributes.WarpJobType, WarpTelemetry.GetShortTypeName(msg.Type));
        }

        WarpTelemetry.JobsEnqueued.Add(1, new KeyValuePair<string, object?>("queue", msg.Queue), new KeyValuePair<string, object?>("kind", "message"));

        await _context.Set<Job>().AddAsync(msg);

        return msg.Id;
    }

    // --- IJob: create Job rows directly ---
    public async Task<Guid> Enqueue<T>(T job)
        where T : class, IJob
        => await CreateJob(job, null, null, null);

    public async Task<Guid> Enqueue<T>(T job, string? queue)
        where T : class, IJob
        => await CreateJob(job, null, queue, null);

    public async Task<Guid> Enqueue<T>(T job, Guid parentJobId)
        where T : class, IJob
        => await CreateJob(job, null, null, parentJobId);

    public async Task<Guid> Enqueue<T>(T job, Guid parentJobId, string? queue)
        where T : class, IJob
        => await CreateJob(job, null, queue, parentJobId);

    public async Task<Guid> Enqueue<T>(T job, JobParameters jobParameters)
        where T : class, IJob
        => await CreateJob(job, jobParameters.ScheduleTime, jobParameters.Queue, jobParameters.ParentId, jobParameters.Metadata);

    public async Task<Guid> Schedule<T>(T job, DateTime scheduleTime)
        where T : class, IJob
        => await CreateJob(job, scheduleTime, null, null);

    public async Task<Guid> Schedule<T>(T job, DateTime scheduleTime, string? queue)
        where T : class, IJob
        => await CreateJob(job, scheduleTime, queue, null);

    public async Task<Guid> Schedule<T>(T job, DateTime scheduleTime, Guid parentJobId)
        where T : class, IJob
        => await CreateJob(job, scheduleTime, null, parentJobId);

    public async Task<Guid> Schedule<T>(T job, DateTime scheduleTime, Guid parentJobId, string? queue)
        where T : class, IJob
        => await CreateJob(job, scheduleTime, queue, parentJobId);

    private async Task<Guid> CreateJob<T>(
        T job,
        DateTime? scheduleTime,
        string? queue,
        Guid? parentId,
        Dictionary<string, object>? adHocMetadata = null)
        where T : class, IJob
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var publishCtx = await RunPublishPipeline(job, adHocMetadata, CancellationToken.None);

        var newJob = JobHelper.CreateJob(
            job,
            scheduleTime,
            queue,
            parentId,
            null,
            now,
            metadata: SerializeMetadata(publishCtx.Metadata));

        // Snapshot caller's trace context before opening the producer span. The consumer's
        // ParentSpanId must be the caller's span, not the one-tick producer span we open below.
        var callerTraceId = Activity.Current?.TraceId;
        var callerSpanId = Activity.Current?.SpanId;

        using var producerSpan = WarpTelemetry.StartProducerActivity(newJob.Queue, WarpTelemetryAttributes.OperationSend);

        // Automatic trace propagation: execution context > parent's trace > self
        var executionContext = JobExecutionContext.Current;
        if (executionContext != null)
        {
            newJob.TraceId = executionContext.TraceId;
            newJob.SpawnedByJobId = executionContext.JobId;
        }
        else if (parentId != null)
        {
            // Inherit trace from parent — check change tracker first (parent may not be committed yet)
            var trackedParent = _context.ChangeTracker.Entries<Job>()
                .FirstOrDefault(e => e.Entity.Id == parentId);
            newJob.TraceId = trackedParent?.Entity.TraceId
                ?? await _context.Set<Job>()
                    .Where(x => x.Id == parentId)
                    .Select(x => x.TraceId)
                    .FirstOrDefaultAsync()
                ?? newJob.Id;
        }
        else if (callerTraceId is { } jobActivityTrace)
        {
            newJob.TraceId = new Guid(jobActivityTrace.ToHexString());
        }
        else
        {
            newJob.TraceId = newJob.Id; // Root of a new trace
        }

        if (callerSpanId is { } jobSpanId && jobSpanId != default)
        {
            newJob.ParentSpanId = jobSpanId.ToHexString();
        }

        if (producerSpan != null)
        {
            producerSpan.SetTag(WarpTelemetryAttributes.MessagingMessageId, newJob.Id.ToString());
            producerSpan.SetTag(WarpTelemetryAttributes.MessagingConversationId, newJob.TraceId.ToString());
            producerSpan.SetTag(WarpTelemetryAttributes.WarpJobKind, JobKind.Job.ToString());
            producerSpan.SetTag(WarpTelemetryAttributes.WarpJobType, WarpTelemetry.GetShortTypeName(newJob.Type));
            producerSpan.SetTag(WarpTelemetryAttributes.WarpJobScheduled, scheduleTime != null);
        }

        WarpTelemetry.JobsEnqueued.Add(1, new KeyValuePair<string, object?>("queue", newJob.Queue), new KeyValuePair<string, object?>("kind", "job"));

        await _context.Set<Job>().AddAsync(newJob);
        await _context.Set<JobLog>().AddAsync(new JobLog
        {
            JobId = newJob.Id,
            EventType = "Created",
            Level = "Information",
            Timestamp = now,
            Message = $"Job created in queue \"{newJob.Queue}\"",
        });

        return newJob.Id;
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var pending = NotificationDispatch.CapturePending(_context);
        await _context.SaveChangesAsync(cancellationToken);
        await NotificationDispatch.DispatchAsync(pending, _signals, _notificationTransport, cancellationToken);
    }

    private async Task<PublishContext<T>> RunPublishPipeline<T>(T job, Dictionary<string, object>? seed, CancellationToken ct)
    {
        var metadata = new Dictionary<string, object>();

        // Seed with inherited metadata from parent execution context
        var executionContext = JobExecutionContext.Current;
        if (executionContext?.MetadataJson != null)
        {
            var inherited = JsonSerializer.Deserialize<Dictionary<string, object>>(executionContext.MetadataJson);
            if (inherited != null)
            {
                foreach (var kvp in inherited)
                {
                    // Addon operational policy / live state is per-handler, never inherited.
                    if (MetadataInheritance.NonInheritableKeys.Contains(kvp.Key))
                    {
                        continue;
                    }

                    metadata[kvp.Key] = kvp.Value;
                }
            }
        }

        // Seed with ad-hoc metadata (overrides inherited)
        if (seed != null)
        {
            foreach (var kvp in seed)
            {
                metadata[kvp.Key] = kvp.Value;
            }
        }

        var context = new PublishContext<T> { Job = job, Metadata = metadata };

        var behaviors = _serviceProvider.GetServices<IPublishPipelineBehavior<T>>().ToArray();
        if (behaviors.Length == 0)
        {
            return context;
        }

        PublishDelegate chain = () => Task.CompletedTask;
        for (var i = behaviors.Length - 1; i >= 0; i--)
        {
            var behavior = behaviors[i];
            var next = chain;
            chain = () => behavior.PublishAsync(context, next, ct);
        }

        await chain();
        return context;
    }

    private static string? SerializeMetadata(Dictionary<string, object> metadata)
    {
        return metadata.Count > 0 ? JsonSerializer.Serialize(metadata) : null;
    }

    private static (DateTime ScheduleTime, State State) ResolveDelivery(IMessage message, DateTime now)
    {
        if (message is ITimeoutMessage timeout && timeout.Delay > TimeSpan.Zero)
        {
            return (now + timeout.Delay, State.Scheduled);
        }

        return (now, State.Enqueued);
    }
}
