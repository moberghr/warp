using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

public interface IBatchPublisher
{
    Task<Guid> StartNew<T>(List<T> batchJobMessages, string? name = null, ContinuationOptions options = ContinuationOptions.OnlyOnSucceeded, Dictionary<string, object>? metadata = null)
        where T : class, IJob;

    Task<Guid> ContinueBatchWith<T>(List<T> batchJobMessages, Guid parentId, string? name = null, ContinuationOptions options = ContinuationOptions.OnlyOnSucceeded, Dictionary<string, object>? metadata = null)
        where T : class, IJob;

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

public sealed class BatchPublisher<TContext> : IBatchPublisher, IDisposable
    where TContext : DbContext
{
    private readonly TContext _context;
    private readonly WarpConfiguration _warpConfiguration;
    private readonly TimeProvider _timeProvider;
    private readonly IServiceProvider _serviceProvider;
    private readonly IWarpNotificationTransport _notificationTransport;
    private readonly ServerTaskSignals<TContext> _signals;
    private bool _staged;

    public BatchPublisher(TContext context, IOptions<WarpConfiguration> configuration, TimeProvider timeProvider, IServiceProvider serviceProvider, IWarpNotificationTransport notificationTransport, ServerTaskSignals<TContext> signals)
    {
        WarpModelGuard.EnsureWarpModelApplied(context);

        _context = context;
        _warpConfiguration = configuration.Value;
        _timeProvider = timeProvider;
        _serviceProvider = serviceProvider;
        _notificationTransport = notificationTransport;
        _signals = signals;
    }

    public async Task<Guid> StartNew<T>(List<T> batchJobMessages, string? name = null, ContinuationOptions options = ContinuationOptions.OnlyOnSucceeded, Dictionary<string, object>? metadata = null)
        where T : class, IJob
    {
        return await BaseCreateBatch(batchJobMessages, State.Enqueued, null, name, options, metadata);
    }

    public async Task<Guid> ContinueBatchWith<T>(List<T> batchJobMessages, Guid parentId, string? name = null, ContinuationOptions options = ContinuationOptions.OnlyOnSucceeded, Dictionary<string, object>? metadata = null)
        where T : class, IJob
    {
        return await BaseCreateBatch(batchJobMessages, State.Awaiting, parentId, name, options, metadata);
    }

    private async Task<Guid> BaseCreateBatch<T>(List<T> batchJobMessages, State batchJobsState, Guid? parentId, string? name, ContinuationOptions options, Dictionary<string, object>? adHocMetadata = null)
        where T : class, IJob
    {
        if (batchJobMessages == null || batchJobMessages.Count == 0)
        {
            throw new ArgumentException("List cannot be empty", nameof(batchJobMessages));
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // Create the batch job (replaces both the old Batch entity and placeholder job)
        // StartNew (no parent) → Processing immediately; continuation → Awaiting until parent finishes
        var batchJob = new Job
        {
            Kind = JobKind.Batch,
            Type = name,
            CreateTime = now,
            CurrentState = parentId != null ? State.Awaiting : State.Processing,
            Queue = _warpConfiguration.DefaultQueue ?? "default",
            ParentJobId = parentId,
            JobCount = batchJobMessages.Count,
            ContinuationOptions = options,
            Application = _warpConfiguration.ApplicationName,
        };

        // Run publish pipeline once for the child type — all children get the same metadata
        var metadata = await RunPublishPipeline(batchJobMessages[0], adHocMetadata);
        var serializedMetadata = metadata.Count > 0 ? JsonSerializer.Serialize(metadata) : null;

        var batchChildJobs = batchJobMessages.ConvertAll(x => JobHelper.CreateJob(x, null, _warpConfiguration.DefaultQueue, batchJob.Id, batchJobsState, now, metadata: serializedMetadata, application: _warpConfiguration.ApplicationName));

        // Propagate trace: execution context > parent's trace > self
        var executionContext = JobExecutionContext.Current;
        Guid? traceId = null;
        Guid? spawnedBy = null;

        if (executionContext != null)
        {
            traceId = executionContext.TraceId;
            spawnedBy = executionContext.JobId;
        }
        else if (parentId != null)
        {
            // Inherit trace from parent — check change tracker first (parent may not be committed yet)
            var trackedParent = _context.ChangeTracker.Entries<Job>()
                .FirstOrDefault(e => e.Entity.Id == parentId);
            traceId = trackedParent?.Entity.TraceId
                ?? await _context.Set<Job>()
                    .Where(x => x.Id == parentId)
                    .Select(x => x.TraceId)
                    .FirstOrDefaultAsync();
        }

        // Snapshot caller's trace context before opening the producer span — the children's
        // ParentSpanId must be the caller's span, not the one-tick producer span.
        var callerTraceId = Activity.Current?.TraceId;
        var callerSpanId = Activity.Current?.SpanId;

        using var producerSpan = WarpTelemetry.StartProducerActivity(batchJob.Queue, WarpTelemetryAttributes.OperationSend);

        if (traceId == null && callerTraceId is { } batchActivityTrace)
        {
            traceId = new Guid(batchActivityTrace.ToHexString());
        }

        batchJob.TraceId = traceId ?? batchJob.Id;
        batchJob.SpawnedByJobId = spawnedBy;

        // Client session (OTel session.id, §8.27): from the spawning job, else the request baggage.
        batchJob.Session = JobExecutionContext.Current?.Session ?? Activity.Current?.GetBaggageItem(WarpTelemetryAttributes.SessionId);

        string? parentSpanId = null;
        if (callerSpanId is { } batchSpanId && batchSpanId != default)
        {
            parentSpanId = batchSpanId.ToHexString();
        }

        batchJob.ParentSpanId = parentSpanId;

        if (producerSpan != null)
        {
            producerSpan.SetTag(WarpTelemetryAttributes.MessagingMessageId, batchJob.Id.ToString());
            producerSpan.SetTag(WarpTelemetryAttributes.MessagingConversationId, batchJob.TraceId.ToString());
            producerSpan.SetTag(WarpTelemetryAttributes.MessagingBatchMessageCount, batchChildJobs.Count);
            producerSpan.SetTag(WarpTelemetryAttributes.WarpJobKind, JobKind.Batch.ToString());
            producerSpan.SetTag(WarpTelemetryAttributes.WarpJobType, WarpTelemetry.GetShortTypeName(batchJob.Type));
        }

        foreach (var childJob in batchChildJobs)
        {
            childJob.TraceId = batchJob.TraceId;
            childJob.Session = batchJob.Session;
            childJob.SpawnedByJobId = spawnedBy;
            childJob.ParentSpanId = parentSpanId;
        }

        var logs = new List<JobLog>();
        foreach (var job in batchChildJobs)
        {
            logs.Add(new JobLog
            {
                JobId = job.Id,
                EventType = "Created",
                Level = "Information",
                Timestamp = now,
                Message = $"Job created in queue \"{job.Queue}\"",
            });
        }

        logs.Add(new JobLog
        {
            JobId = batchJob.Id,
            EventType = "Created",
            Level = "Information",
            Timestamp = now,
            Message = $"Batch job created in queue \"{batchJob.Queue}\"",
        });

        WarpTelemetry.JobsEnqueued.Add(batchChildJobs.Count, new KeyValuePair<string, object?>("queue", batchJob.Queue), new KeyValuePair<string, object?>("kind", "job"));
        WarpTelemetry.JobsEnqueued.Add(1, new KeyValuePair<string, object?>("queue", batchJob.Queue), new KeyValuePair<string, object?>("kind", "batch"));

        _context.Set<Job>().AddRange(batchChildJobs);
        _context.Set<Job>().Add(batchJob);
        _context.Set<JobLog>().AddRange(logs);
        _staged = true;

        return batchJob.Id;
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var pending = NotificationDispatch.CapturePending(_context);
        await _context.SaveChangesAsync(cancellationToken);
        await NotificationDispatch.DispatchAsync(pending, _signals, _notificationTransport, cancellationToken);
    }

    public void Dispose()
    {
        try
        {
            WarnIfUnsavedStagedJobs();
        }
        catch (Exception ex)
        {
            // A diagnostic must never break scope disposal — swallow everything (including a
            // failed logger resolution) rather than let anything propagate out of Dispose.
            Debug.WriteLine($"Warp: unsaved-outbox diagnostic failed and was ignored: {ex.Message}");
        }
    }

    private async Task<Dictionary<string, object>> RunPublishPipeline<T>(T job, Dictionary<string, object>? seed = null, CancellationToken ct = default)
    {
        var metadata = new Dictionary<string, object>();

        // Metadata is not inherited from the parent job. User metadata is set per publish
        // (JobParameters) or via IPublishPipelineBehavior; addon policy is resolved per handler.
        // Trace correlation threads separately through the Job's TraceId / SpawnedByJobId columns.
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
            return metadata;
        }

        PublishDelegate chain = () => Task.CompletedTask;
        for (var i = behaviors.Length - 1; i >= 0; i--)
        {
            var behavior = behaviors[i];
            var next = chain;
            chain = () => behavior.PublishAsync(context, next, ct);
        }

        await chain();
        return context.Metadata;
    }

    // Development-time diagnostic for the silent outbox footgun: jobs staged via IBatchPublisher
    // are discarded without a trace if the scope ends before SaveChangesAsync. Reuses the
    // already-injected IServiceProvider to resolve the logger, deliberately avoiding a ctor
    // parameter (which would ripple to the test construction sites) for a dev-only check.
    private void WarnIfUnsavedStagedJobs()
    {
        if (!_warpConfiguration.WarnOnUnsavedStagedJobs || !_staged)
        {
            return;
        }

        // Inside a worker handler scope the worker owns the commit, not the caller — warning here
        // would be a false positive. JobExecutionContext.Current is set only while a job executes.
        if (JobExecutionContext.Current != null)
        {
            return;
        }

        var unsaved = _context.ChangeTracker.Entries<Job>().Count(x => x.State == EntityState.Added);
        if (unsaved == 0)
        {
            return;
        }

        var logger = _serviceProvider.GetService<ILogger<BatchPublisher<TContext>>>();
        if (logger == null)
        {
            return;
        }

        logger.LogWarning("Warp: {Count} job(s)/message(s) were staged via IBatchPublisher but the scope ended without SaveChangesAsync — they were discarded and will not run. Call 'await publisher.SaveChangesAsync(ct)'. (Disable this check with WarpConfiguration.WarnOnUnsavedStagedJobs = false.)", unsaved);
    }
}
