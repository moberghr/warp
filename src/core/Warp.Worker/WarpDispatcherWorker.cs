using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Data;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Events;
using Warp.Core.Handlers;
using Warp.Core.Logging;
using Warp.Core.Notifications;
using Warp.Worker.Logging;
using Warp.Worker.Services;

namespace Warp.Worker;

/// <summary>
/// Worker that receives pre-fetched jobs from a dispatcher channel.
/// Pure executor — handles execution and completion only. Orchestration handled by Orchestrator.
/// Completions are buffered in a per-worker <see cref="CompletionBatch{TContext}"/> and flushed
/// as a single multi-row transaction when any of: size threshold, time threshold, idle, or shutdown fires.
/// </summary>
public class WarpDispatcherWorker<TContext> : BackgroundService
    where TContext : DbContext
{
    private readonly Guid _workerId;
    private readonly ChannelReader<Job> _jobReader;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WarpDispatcherWorker<TContext>> _logger;
    private readonly WarpWorkerConfiguration _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly CompletionBatch<TContext> _batch;
    private readonly IWarpNotificationTransport _notificationTransport;
    private readonly ServerTaskSignals<TContext> _signals;

    public WarpDispatcherWorker(
        Guid workerId,
        ChannelReader<Job> jobReader,
        IServiceScopeFactory scopeFactory,
        ILogger<WarpDispatcherWorker<TContext>> logger,
        IOptions<WarpWorkerConfiguration> configuration,
        TimeProvider timeProvider,
        IWarpNotificationTransport notificationTransport,
        ServerTaskSignals<TContext> signals,
        IDatabaseExceptionClassifier exceptionClassifier)
    {
        _workerId = workerId;
        _jobReader = jobReader;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _configuration = configuration.Value;
        _timeProvider = timeProvider;
        _notificationTransport = notificationTransport;
        _signals = signals;
        _batch = new CompletionBatch<TContext>(
            scopeFactory,
            timeProvider,
            logger,
            exceptionClassifier,
            _configuration.CompletionBatchSize,
            _configuration.CompletionFlushInterval);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Channel-based pull: WaitToReadAsync blocks until the dispatcher produces a job.
        // No idle polling loop here — polling backoff lives in WarpDispatcher.ExecuteAsync.
        // The hand-rolled WaitToRead/TryRead loop (vs await foreach ReadAllAsync) exists so we
        // can flush any buffered completions BEFORE suspending on the next WaitToReadAsync —
        // otherwise a small batch (below CompletionBatchSize) would wait for the time trigger
        // or forever if no more jobs arrive.
        //
        // WaitToReadAsync does NOT observe stoppingToken: if we exited on cancellation while the
        // channel still had buffered items, those jobs would be DB-orphaned as Processing (the
        // dispatcher wrote them but nobody consumed them). Instead we drain the channel fully
        // and exit only when the dispatcher completes its writer on its own shutdown. The host's
        // shutdown timeout (30s default) still bounds this — a stuck handler eventually gets
        // killed — and stoppingToken is still propagated into ProcessJob so individual
        // handler-path awaits can react if they want to.
        while (await _jobReader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            while (_jobReader.TryRead(out var job))
            {
                try
                {
                    await ProcessJob(job, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Handler (or a pipeline await point) observed shutdown. Keep draining the
                    // channel — returning would orphan every remaining buffered job.
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Dispatcher worker failed on job {id}", job.Id);
                }

                if (_batch.IsFull || _batch.IsTimeElapsed)
                {
                    await FlushBatchSafely();
                }
            }

            // Idle — drain any buffered completions before suspending on WaitToReadAsync
            await FlushBatchSafely();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        try
        {
            await _batch.FlushAsync();
            _signals.SignalJobFinalized();
            await NotificationDispatch.FireAsync(
                _notificationTransport,
                [new Notification(NotificationKind.JobFinalized, null)],
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Final batch flush on shutdown failed");
        }
    }

    private async Task FlushBatchSafely()
    {
        if (_batch.Count == 0)
        {
            return;
        }

        try
        {
            await _batch.FlushAsync();
            _signals.SignalJobFinalized();
            await NotificationDispatch.FireAsync(
                _notificationTransport,
                [new Notification(NotificationKind.JobFinalized, null)]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush completion batch");
        }
    }

    private async Task ProcessJob(Job job, CancellationToken cancellationToken)
    {
        PerfTrace.Begin();

        // OTel "receive" span — covers post-fetch / pre-handler bookkeeping (mark worker ownership).
        // Closes before the consumer span opens, so receive and process are siblings under the
        // caller's trace, not nested.
        using (var receiveSpan = WarpTelemetry.StartReceiveActivity(job.Queue))
        {
            receiveSpan?.SetTag(WarpTelemetryAttributes.MessagingMessageId, job.Id.ToString());
            receiveSpan?.SetTag(WarpTelemetryAttributes.WarpWorkerId, _workerId.ToString());

            // Operational observability — Dashboard/incident response needs to see "worker X holds job Y"
            // while it runs. Single UPDATE (no SELECT, no change tracker). Scope disposes when the helper returns.
            // CancellationToken.None: the claim already committed State=Processing, so aborting this
            // UPDATE on shutdown would orphan the row without clearing the worker stamp. Fast UPDATE,
            // uncancellable is cheap insurance.
            await MarkWorkerOwnership(job, CancellationToken.None);
            job.CurrentWorkerId = _workerId;
        }

        var logCollector = new JobLogCollector { JobId = job.Id, TimeProvider = _timeProvider, WorkerId = _workerId };
        var progressCollector = new JobProgressCollector { JobId = job.Id, TimeProvider = _timeProvider, WorkerId = _workerId };

        using var jobCts = new CancellationTokenSource();
        var monitorTask = RunJobMonitor(job.Id, logCollector, progressCollector, jobCts, cancellationToken);

        var activity = WarpTelemetry.StartJobActivity(job.TraceId ?? job.Id, job.ParentSpanId, job.Queue);
        var jobTypeName = WarpTelemetry.GetShortTypeName(job.Type);
        activity?.SetTag(WarpTelemetryAttributes.MessagingMessageId, job.Id.ToString());
        activity?.SetTag(WarpTelemetryAttributes.MessagingConversationId, (job.TraceId ?? job.Id).ToString());
        activity?.SetTag(WarpTelemetryAttributes.WarpJobType, jobTypeName);
        activity?.SetTag(WarpTelemetryAttributes.WarpJobKind, job.Kind.ToString());
        activity?.SetTag(WarpTelemetryAttributes.WarpWorkerId, _workerId.ToString());

        // Note: dispatcher fetches only Kind=Job rows (see PostgresWarpSqlQueries.cs /
        // SqlServerWarpSqlQueries.cs); the messaging.batch.message_count tag belongs on the
        // producer span emitted by BatchPublisher, not here.
        WarpTelemetry.JobsActive.Add(1, new KeyValuePair<string, object?>("queue", job.Queue));
        Stopwatch? handlerStopwatch = null;
        IServiceScope? handlerScope = null;
        JobContext? jobContext = null;
        try
        {
            PerfTrace.Mark(PerfTrace.ExecuteHandler);
            _logger.LogInformation("Worker {workerId} executing job {id}", _workerId, job.Id);

            if (_configuration.EnableHandlerLogging)
            {
                JobLogContext.Current = logCollector;
            }

            JobExecutionContext.Current = new JobExecutionInfo
            {
                JobId = job.Id,
                TraceId = job.TraceId ?? job.Id,
                MetadataJson = job.Metadata,
            };

            // Handler scope — isolated DbContext for handler + pipeline behaviors.
            handlerScope = _scopeFactory.CreateScope();

            jobContext = handlerScope.ServiceProvider.GetRequiredService<JobContext>();
            jobContext.JobId = job.Id;
            jobContext.TraceId = job.TraceId ?? job.Id;
            jobContext.Metadata = MetadataSerializer.Deserialize(job.Metadata);
            jobContext.ProgressCollector = progressCollector;

            // Tag the consumer span with the retry attempt (1-based). Read directly from the
            // metadata dict — Warp.Worker does not depend on Warp.Core.Retry. Numbers come
            // back from MetadataSerializer.Deserialize as long.
            if (jobContext.Metadata.TryGetValue(WarpTelemetryAttributes.RetryMetadataRetriedTimesKey, out var retriedTimesObj)
                && retriedTimesObj is long retriedTimes)
            {
                activity?.SetTag(WarpTelemetryAttributes.WarpJobAttempt, retriedTimes + 1);
            }
            else
            {
                activity?.SetTag(WarpTelemetryAttributes.WarpJobAttempt, 1);
            }

            if (jobContext.Metadata.TryGetValue(WarpTelemetryAttributes.RetryMetadataMaxRetriesKey, out var maxRetriesObj)
                && maxRetriesObj is long maxRetries)
            {
                activity?.SetTag(WarpTelemetryAttributes.WarpJobMaxAttempts, maxRetries + 1);
            }

            handlerStopwatch = Stopwatch.StartNew();
            await ExecuteJob(job, handlerScope.ServiceProvider, jobCts.Token);
            handlerStopwatch.Stop();

            // Commit handler's work (outbox: published jobs + business entities) before disposing.
            // Capture pending push notifications for any child jobs the handler added, fire post-commit.
            // CancellationToken.None on FireAsync: the handler already committed; cancelling the
            // notification throw on shutdown would skip _batch.Add below and orphan this job as
            // Processing. Notifications are fast (in-DB LISTEN/NOTIFY or Service Broker) and
            // idempotent — uncancellable is safer than losing the completion.
            var handlerContext = handlerScope.ServiceProvider.GetRequiredService<TContext>();
            var handlerPending = NotificationDispatch.CapturePending(handlerContext);
            await handlerContext.SaveChangesAsync(default);
            await NotificationDispatch.DispatchAsync(handlerPending, _signals, _notificationTransport, CancellationToken.None);

            // Read metadata and outcome from handler scope before disposing
            job.Metadata = JsonSerializer.Serialize(jobContext.Metadata);
            var successOutcome = jobContext.Outcome;
            jobContext.ProgressCollector = null;
            handlerScope.Dispose();
            handlerScope = null;

            var durationMs = handlerStopwatch.Elapsed.TotalMilliseconds;

            if (successOutcome != null)
            {
                var outcomeStatus = successOutcome.State.ToString().ToLowerInvariant();
                activity?.SetTag(WarpTelemetryAttributes.WarpJobStatus, outcomeStatus);
                activity?.SetTag(WarpTelemetryAttributes.WarpJobDurationMs, durationMs);
                activity?.AddEvent(new ActivityEvent($"warp.job.{outcomeStatus}"));
                WarpTelemetry.JobDuration.Record(durationMs, new KeyValuePair<string, object?>("queue", job.Queue), new KeyValuePair<string, object?>("type", jobTypeName), new KeyValuePair<string, object?>("status", outcomeStatus));
                WarpTelemetry.JobsCompleted.Add(1, new KeyValuePair<string, object?>("queue", job.Queue), new KeyValuePair<string, object?>("type", jobTypeName), new KeyValuePair<string, object?>("status", outcomeStatus));
            }
            else
            {
                activity?.SetTag(WarpTelemetryAttributes.WarpJobStatus, "succeeded");
                activity?.SetTag(WarpTelemetryAttributes.WarpJobDurationMs, durationMs);
                activity?.AddEvent(new ActivityEvent("warp.job.completed", tags: new ActivityTagsCollection
                {
                    { "duration_ms", durationMs },
                }));
                WarpTelemetry.JobDuration.Record(durationMs, new KeyValuePair<string, object?>("queue", job.Queue), new KeyValuePair<string, object?>("type", jobTypeName), new KeyValuePair<string, object?>("status", "succeeded"));
                WarpTelemetry.JobsCompleted.Add(1, new KeyValuePair<string, object?>("queue", job.Queue), new KeyValuePair<string, object?>("type", jobTypeName), new KeyValuePair<string, object?>("status", "succeeded"));
            }

            JobLogContext.Current = null;
            JobExecutionContext.Current = null;

            _logger.LogInformation("Worker {workerId} completed job {id}", _workerId, job.Id);

            PerfTrace.Mark(PerfTrace.CancelKeepAlive);
            await jobCts.CancelAsync();
            await monitorTask;

            if (successOutcome != null)
            {
                job.CurrentState = successOutcome.State;
                if (successOutcome.ClearHandlerType)
                {
                    job.HandlerType = null;
                }

                if (successOutcome.ScheduleTime != null)
                {
                    job.ScheduleTime = successOutcome.ScheduleTime.Value;
                }
            }
            else
            {
                job.CurrentState = State.Completed;
            }

            var (counters, finalLogs) = BuildFinalization(job, null, durationMs, successOutcome);
            var logs = CollectLogs(finalLogs, logCollector, progressCollector).ToArray();
            _batch.Add(new PendingCompletion(job, counters, logs));
        }
        catch (OperationCanceledException) when (jobCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Job was cancelled (deleted while running) — dispose handler scope first
            handlerScope?.Dispose();
            handlerScope = null;

            handlerStopwatch?.Stop();
            activity?.SetTag(WarpTelemetryAttributes.WarpJobStatus, "cancelled");
            activity?.AddEvent(new ActivityEvent("warp.job.cancelled"));
            WarpTelemetry.JobsCompleted.Add(1, new KeyValuePair<string, object?>("queue", job.Queue), new KeyValuePair<string, object?>("type", jobTypeName), new KeyValuePair<string, object?>("status", "cancelled"));
            JobLogContext.Current = null;
            JobExecutionContext.Current = null;
            _logger.LogInformation("Job {id} was cancelled", job.Id);
            await monitorTask;

            var cancelNow = _timeProvider.GetUtcNow().UtcDateTime;
            job.CurrentState = State.Deleted;
            job.ExpireAt = cancelNow.Add(_configuration.JobExpirationTimeout);
            job.CancellationMode = CancellationMode.None;
            job.CurrentWorkerId = null;
            job.LastKeepAlive = null;

            // Match BuildFinalization: emit both the aggregate and per-hour counters so cancellations
            // show up in the dashboard's hourly graph alongside other terminal states.
            var hourSuffix = cancelNow.ToString("yyyy-MM-dd-HH", CultureInfo.InvariantCulture);
            IReadOnlyList<Counter> cancelCounters =
            [
                new() { Key = "stats:deleted", Value = 1 },
                new() { Key = $"stats:deleted:{hourSuffix}", Value = 1 },
            ];
            var cancelLog = new JobLog
            {
                JobId = job.Id,
                EventType = "Cancelled",
                Timestamp = cancelNow,
                Level = "Information",
                Message = "Job was cancelled by user",
                DurationMs = handlerStopwatch?.Elapsed.TotalMilliseconds,
                WorkerId = _workerId,
            };
            var logs = CollectLogs([cancelLog], logCollector, progressCollector).ToArray();
            _batch.Add(new PendingCompletion(job, cancelCounters, logs));
        }
        catch (Exception e)
        {
            handlerStopwatch?.Stop();
            var errorDurationMs = handlerStopwatch?.Elapsed.TotalMilliseconds;

            // Read pipeline outcome from handler scope before disposing
            var outcome = jobContext?.Outcome;
            if (outcome != null)
            {
                job.CurrentState = outcome.State;
                if (outcome.ClearHandlerType)
                {
                    job.HandlerType = null;
                }

                if (outcome.ScheduleTime != null)
                {
                    job.ScheduleTime = outcome.ScheduleTime.Value;
                }

                job.Metadata = JsonSerializer.Serialize(jobContext!.Metadata);
            }
            else
            {
                job.CurrentState = State.Failed;
            }

            handlerScope?.Dispose();
            handlerScope = null;

            var willRetry = job.CurrentState == State.Enqueued;
            var errorStatus = willRetry ? "retried" : "failed";
            activity?.SetStatus(ActivityStatusCode.Error, WarpTelemetry.TruncateMessage(e.Message, 256));
            activity?.SetTag(WarpTelemetryAttributes.WarpJobStatus, errorStatus);
            activity?.SetTag(WarpTelemetryAttributes.ErrorType, e.GetType().FullName);

            if (willRetry)
            {
                activity?.AddEvent(new ActivityEvent("warp.job.retried"));
            }
            else
            {
                activity?.AddEvent(new ActivityEvent("warp.job.failed", tags: new ActivityTagsCollection
                {
                    { "exception.type", e.GetType().FullName },
                    { "exception.message", e.Message },
                }));
            }

            WarpTelemetry.JobDuration.Record(errorDurationMs ?? 0, new KeyValuePair<string, object?>("queue", job.Queue), new KeyValuePair<string, object?>("type", jobTypeName), new KeyValuePair<string, object?>("status", errorStatus));
            WarpTelemetry.JobsCompleted.Add(1, new KeyValuePair<string, object?>("queue", job.Queue), new KeyValuePair<string, object?>("type", jobTypeName), new KeyValuePair<string, object?>("status", errorStatus));
            JobLogContext.Current = null;
            JobExecutionContext.Current = null;
            _logger.LogError(e, "Error executing job {id}", job.Id);
            await jobCts.CancelAsync();
            await monitorTask;

            var (counters, finalLogs) = BuildFinalization(job, e, errorDurationMs, outcome);
            var logs = CollectLogs(finalLogs, logCollector, progressCollector).ToArray();
            _batch.Add(new PendingCompletion(job, counters, logs));
        }
        finally
        {
            handlerScope?.Dispose();
            WarpTelemetry.JobsActive.Add(-1, new KeyValuePair<string, object?>("queue", job.Queue));
            activity?.Stop();
            activity?.Dispose();
            JobLogContext.Current = null;
            JobExecutionContext.Current = null;
        }

        PerfTrace.Mark(PerfTrace.Done);
        PerfTrace.End();
    }

    private async Task MarkWorkerOwnership(Job job, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        var handlerTypeToSet = job.HandlerType;
        await context.Set<Job>()
            .Where(x => x.Id == job.Id)
            .ExecuteUpdateAsync(
                x => x
                    .SetProperty(p => p.CurrentWorkerId, _workerId)
                    .SetProperty(p => p.HandlerType, handlerTypeToSet),
                cancellationToken);

        // The "Processing" JobLog is written here, not in WarpDispatcher.FetchAndDistribute.
        // Writing it dispatcher-side would orphan log rows for jobs whose channel-write got
        // cancelled at shutdown (UnclaimUndelivered reverts the row to Enqueued, but the log
        // entry would remain). Writing it on receipt by the actual worker keeps the audit
        // trail truthful and lets us tag the entry with the specific WorkerId, matching
        // single-worker-mode semantics.
        context.Set<JobLog>().Add(new JobLog
        {
            JobId = job.Id,
            EventType = "Processing",
            Timestamp = _timeProvider.GetUtcNow().UtcDateTime,
            Level = "Information",
            Message = $"The job {job.Id} is being processed",
            WorkerId = _workerId,
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    private static async Task ExecuteJob(Job job, IServiceProvider provider, CancellationToken cancellationToken)
    {
        var messageType = Type.GetType(job.Type!) ?? throw new WarpException($"Unknown type {job.Type}");
        var payload = JsonSerializer.Deserialize(job.Message!, messageType) ?? throw new WarpException($"Unable to deserialize message {job.Message} to type {job.Type}");

        var jobContext = provider.GetRequiredService<JobContext>();

        if (job.HandlerType != null)
        {
            var handlerType = Type.GetType(job.HandlerType) ?? throw new WarpException($"Unknown handler type {job.HandlerType}");
            jobContext.HandlerType = handlerType;
            await JobDispatcher.ExecuteHandler(payload, messageType, handlerType, provider, cancellationToken);

            return;
        }

        var jobHandlerType = JobDispatcher.DiscoverJobHandler(messageType, provider) ?? throw new WarpException($"No handler registered for {messageType.Name}");
        job.HandlerType = jobHandlerType.AssemblyQualifiedName;
        jobContext.HandlerType = jobHandlerType;
        await JobDispatcher.ExecuteJobHandler(payload, messageType, jobHandlerType, provider, cancellationToken);
    }

    private async Task RunJobMonitor(Guid jobId, JobLogCollector logCollector, JobProgressCollector progressCollector, CancellationTokenSource jobCts, CancellationToken stoppingToken)
    {
        var logFlushInterval = _configuration.LogFlushInterval;
        var cancellationCheckInterval = _configuration.CancellationCheckInterval;
        var tickInterval = logFlushInterval < cancellationCheckInterval ? logFlushInterval : cancellationCheckInterval;
        var timeSinceLastCheck = TimeSpan.Zero;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, jobCts.Token);
        while (!linked.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(tickInterval, linked.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            timeSinceLastCheck += tickInterval;

            try
            {
                var pendingLogs = logCollector.Drain();
                var pendingProgress = progressCollector.Drain();
                var doCancellationCheck = timeSinceLastCheck >= cancellationCheckInterval;

                if (pendingLogs.Count == 0 && pendingProgress.Count == 0 && !doCancellationCheck)
                {
                    continue;
                }

                using var s = _scopeFactory.CreateScope();
                var ctx = s.ServiceProvider.GetRequiredService<TContext>();

                if (doCancellationCheck)
                {
                    timeSinceLastCheck = TimeSpan.Zero;

                    var cancellationMode = await ctx.Set<Job>()
                        .Where(x => x.Id == jobId)
                        .Select(x => x.CancellationMode)
                        .FirstOrDefaultAsync(stoppingToken);

                    if (cancellationMode != CancellationMode.None)
                    {
                        _logger.LogInformation("Job {jobId} cancellation requested ({mode}), cancelling handler", jobId, cancellationMode);

                        // Flush any pending logs/progress before cancelling — they were already drained from the collectors
                        if (pendingLogs.Count > 0)
                        {
                            ctx.Set<JobLog>().AddRange(pendingLogs);
                        }

                        if (pendingProgress.Count > 0)
                        {
                            ctx.Set<JobLog>().AddRange(pendingProgress);
                        }

                        if (pendingLogs.Count > 0 || pendingProgress.Count > 0)
                        {
                            await ctx.SaveChangesAsync(stoppingToken);
                        }

                        await jobCts.CancelAsync();
                        return;
                    }

                    var now = _timeProvider.GetUtcNow().UtcDateTime;
                    await ctx.Set<Job>()
                        .Where(x => x.Id == jobId)
                        .ExecuteUpdateAsync(x => x.SetProperty(p => p.LastKeepAlive, now), stoppingToken);
                }

                if (pendingLogs.Count > 0)
                {
                    ctx.Set<JobLog>().AddRange(pendingLogs);
                }

                if (pendingProgress.Count > 0)
                {
                    ctx.Set<JobLog>().AddRange(pendingProgress);
                }

                if (pendingLogs.Count > 0 || pendingProgress.Count > 0)
                {
                    await ctx.SaveChangesAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Failed job monitor for {jobId}", jobId);
            }
        }
    }

    /// <summary>
    /// Clears worker-owned fields on the job and produces the completion counters + final-state logs.
    /// Returns one OR two logs: a retry-due-to-error emits both a Failed log (with the exception)
    /// and a Scheduled/Enqueued log (with the next attempt time). All other transitions emit one.
    /// State must be set on the job before calling this method.
    /// </summary>
    private (List<Counter> Counters, List<JobLog> FinalLogs) BuildFinalization(Job job, Exception? error, double? durationMs, JobOutcome? outcome)
    {
        var state = job.CurrentState;
        job.CancellationMode = CancellationMode.None;
        job.CurrentWorkerId = null;
        job.LastKeepAlive = null;

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var hourSuffix = now.ToString("yyyy-MM-dd-HH", CultureInfo.InvariantCulture);
        var counters = new List<Counter>();
        if (state == State.Completed)
        {
            job.ExpireAt = now.Add(_configuration.JobExpirationTimeout);
            counters.Add(new Counter { Key = "stats:succeeded", Value = 1 });
            counters.Add(new Counter { Key = $"stats:succeeded:{hourSuffix}", Value = 1 });
        }
        else if (state == State.Failed)
        {
            counters.Add(new Counter { Key = "stats:failed", Value = 1 });
            counters.Add(new Counter { Key = $"stats:failed:{hourSuffix}", Value = 1 });
        }
        else if (state == State.Deleted)
        {
            job.ExpireAt = now.Add(_configuration.JobExpirationTimeout);
            counters.Add(new Counter { Key = "stats:deleted", Value = 1 });
            counters.Add(new Counter { Key = $"stats:deleted:{hourSuffix}", Value = 1 });
        }
        else if (state == State.Enqueued || state == State.Scheduled)
        {
            // Covers retry backoff and Mutex Wait — anything that puts the job back on the queue.
            counters.Add(new Counter { Key = "stats:requeued", Value = 1 });
            counters.Add(new Counter { Key = $"stats:requeued:{hourSuffix}", Value = 1 });
        }

        // Event-type, level, and split-on-retry are shared with WarpWorkerService via
        // FinalizationLogs.Build — both worker paths emit identical log shapes.
        var logs = FinalizationLogs.Build(job, error, durationMs, _workerId, now, outcome);

        return (counters, logs.ToList());
    }

    private IEnumerable<JobLog> CollectLogs(IReadOnlyList<JobLog> finalLogs, JobLogCollector collector, JobProgressCollector progressCollector)
    {
        foreach (var log in finalLogs)
        {
            yield return log;
        }

        if (_configuration.EnableHandlerLogging)
        {
            foreach (var drained in collector.Drain())
            {
                yield return drained;
            }
        }

        // Progress flows regardless of EnableHandlerLogging — it is not ILogger output.
        foreach (var drained in progressCollector.Drain())
        {
            yield return drained;
        }
    }
}
