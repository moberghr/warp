using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.ErrorGrouping;
using Warp.Core.Events;
using Warp.Core.Handlers;
using Warp.Core.Logging;
using Warp.Core.Notifications;
using Warp.Core.Observability;
using Warp.Core.Services;
using Warp.Core.Timeout;
using Warp.Worker.Logging;
using Warp.Worker.Services;

namespace Warp.Worker;

public interface IWarpWorkerService
{
    Task<bool> GetAndProcessJob(CancellationToken cancellationToken);
}

public class WarpWorkerService<TContext> : IWarpWorkerService
    where TContext : DbContext
{
    private readonly Guid _workerId;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<WarpWorkerService<TContext>> _logger;
    private readonly WarpServerConfiguration _configuration;
    private readonly WorkerGroupConfiguration _groupConfiguration;
    private readonly TimeProvider _timeProvider;
    private readonly IWarpNotificationTransport _notificationTransport;
    private readonly IWarpSqlQueries<TContext> _sqlQueries;
    private readonly ServerTaskSignals<TContext> _signals;

    public WarpWorkerService(Guid workerId, IServiceScopeFactory serviceScopeFactory, ILogger<WarpWorkerService<TContext>> logger, IOptions<WarpServerConfiguration> configuration, WorkerGroupConfiguration groupConfiguration, TimeProvider timeProvider, IWarpSqlQueries<TContext> sqlQueries, IWarpNotificationTransport notificationTransport, ServerTaskSignals<TContext> signals)
    {
        _workerId = workerId;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
        _configuration = configuration.Value;
        _groupConfiguration = groupConfiguration;
        _timeProvider = timeProvider;
        _sqlQueries = sqlQueries;
        _notificationTransport = notificationTransport;
        _signals = signals;
    }

    public async Task<bool> GetAndProcessJob(CancellationToken cancellationToken)
    {
        PerfTrace.Begin();

        // Worker scope — owns Warp state (Job, JobLog, Counter). Isolated from handler's DbContext.
        using var workerScope = _serviceScopeFactory.CreateScope();
        var workerContext = workerScope.ServiceProvider.GetRequiredService<IWarpServerContext>().Context;

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // Atomic claim: UPDATE ... RETURNING (PG) / UPDATE ... OUTPUT (MSSQL) with
        // FOR UPDATE SKIP LOCKED / ROWLOCK+UPDLOCK+READPAST baked into the SQL. No SELECT→UPDATE
        // window, no dependency on a regex-rewriting interceptor. Concurrent workers across
        // servers get distinct rows or nothing at all — never the same row.
        PerfTrace.Mark(PerfTrace.FetchJob);
        var claimed = await _sqlQueries.ClaimEnqueuedJobsAsync(
            workerContext,
            _groupConfiguration.Queues,
            _workerId,
            now,
            limit: 1,
            cancellationToken);

        if (claimed.Count == 0)
        {
            return false;
        }

        var job = claimed[0];
        _logger.LogInformation("Worker {workerId} fetched job {id}", _workerId, job.Id);

        // OTel "receive" span — covers post-fetch / pre-handler bookkeeping (the Processing
        // log row + commit). Closes before the consumer span opens, so receive and process
        // are siblings under the caller's trace, not nested.
        using (var receiveSpan = WarpTelemetry.StartReceiveActivity(job.Queue))
        {
            receiveSpan?.SetTag(WarpTelemetryAttributes.MessagingMessageId, job.Id.ToString());
            receiveSpan?.SetTag(WarpTelemetryAttributes.WarpWorkerId, _workerId.ToString());

            // The claim itself is committed atomically via UPDATE RETURNING. Persist the Processing
            // log entry in a separate round-trip; failure here leaves the job Processing with
            // LastKeepAlive set, which is fine — the worker carries on and the log is cosmetic.
            workerContext.Set<JobLog>().Add(new JobLog
            {
                JobId = job.Id,
                EventType = "Processing",
                Timestamp = now,
                Level = "Information",
                Message = $"The job {job.Id} is being processed",
                WorkerId = _workerId,
            });

            // Queue-wait SLI (§8.26): time the job spent eligible-but-unclaimed. Always-on meter + (sink-gated)
            // Counter rows added to workerContext so they ride the SaveChanges below — no extra round-trip
            // (§0.2/§6.1), mirroring the jobstat finalization triad (§8.23/§8.24).
            var waitMs = Math.Max(0, (now - job.ScheduleTime).TotalMilliseconds);
            WarpTelemetry.RecordQueueWait(job.Queue, waitMs, _configuration.ApplicationName);
            if (_configuration.JobMetricsSink is RecordingSink.Database or RecordingSink.Both)
            {
                foreach (var counter in QueueWaitKeys.Build(job.Queue, waitMs, _configuration.ApplicationName, MetricTiers.Suffix(MetricTier.Fine, now, _configuration.FineResolutionMinutes)))
                {
                    workerContext.Set<Counter>().Add(counter);
                }
            }

            PerfTrace.Mark(PerfTrace.SaveProcessing);
            await workerContext.SaveChangesAsync(cancellationToken);
            PerfTrace.Mark(PerfTrace.CommitTransaction1);
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

        // Note: the worker-fetch SQL filters Kind = JobKind.Job (see PostgresWarpSqlQueries.cs /
        // SqlServerWarpSqlQueries.cs). Batch / Message jobs never reach this code path —
        // messaging.batch.message_count is set on the producer span in BatchPublisher and on the
        // batch's orchestration log, not here.
        WarpTelemetry.JobsActive.Add(1, new KeyValuePair<string, object?>("queue", job.Queue));
        Stopwatch? handlerStopwatch = null;
        IServiceScope? handlerScope = null;
        JobContext? jobContext = null;
        DateTime? totalDeadlineUtc = null;
        var incomingAttempts = 0L;
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
            };

            // Handler scope — isolated DbContext for handler + pipeline behaviors.
            // Handler's change tracker is disposed with this scope, never leaking into worker saves.
            handlerScope = _serviceScopeFactory.CreateScope();

            jobContext = handlerScope.ServiceProvider.GetRequiredService<JobContext>();
            jobContext.JobId = job.Id;
            jobContext.TraceId = job.TraceId ?? job.Id;
            jobContext.Metadata = MetadataSerializer.Deserialize(job.Metadata);
            jobContext.ProgressCollector = progressCollector;

            // Deadline attainment (§8.30): capture the Total-scope timeout deadline (§8.7 — stamped at publish,
            // immutable through execution) now, while the metadata dict is deserialized, so both the success and
            // failure finalization paths can emit the attainment counter without re-reading. Guarded by a cheap
            // key probe so a job without a timeout deadline pays nothing (no metadata proxy allocation).
            if (jobContext.Metadata.ContainsKey(nameof(ITimeoutMetadata.TimeoutDeadlineUtc)))
            {
                var timeoutMeta = jobContext.GetMetadata<ITimeoutMetadata>();
                if (timeoutMeta.TimeoutScope == TimeoutScope.Total && timeoutMeta.TimeoutDeadlineUtc is { } deadlineUtc)
                {
                    totalDeadlineUtc = deadlineUtc;
                }
            }

            // Tag the consumer span with the retry attempt (1-based). Read directly from the
            // metadata dict — Warp.Worker does not depend on Warp.Core.Retry. Numbers come
            // back from MetadataSerializer.Deserialize as long.
            if (jobContext.Metadata.TryGetValue(WarpTelemetryAttributes.RetryMetadataRetriedTimesKey, out var retriedTimesObj)
                && retriedTimesObj is long retriedTimes)
            {
                incomingAttempts = retriedTimes;
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
            var handlerContext = handlerScope.ServiceProvider.GetRequiredService<TContext>();
            var handlerPending = NotificationDispatch.CapturePending(handlerContext);
            await handlerContext.SaveChangesAsync(default);
            await NotificationDispatch.DispatchAsync(handlerPending, _signals, _notificationTransport, cancellationToken);

            // Read metadata and outcome from handler scope before disposing
            job.Metadata = JsonSerializer.Serialize(jobContext.Metadata);
            var successOutcome = jobContext.Outcome;
            jobContext.ProgressCollector = null;
            handlerScope.Dispose();
            handlerScope = null;

            var durationMs = handlerStopwatch.Elapsed.TotalMilliseconds;

            if (successOutcome != null)
            {
                // Pipeline behavior short-circuited (e.g. mutex held)
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

            PerfTrace.Mark(PerfTrace.BeginTransaction2);
            await using var endTransaction = await workerContext.Database.BeginTransactionAsync(default);

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

            FinalizeJobState(workerContext, job, null, handlerStopwatch.Elapsed.TotalMilliseconds, successOutcome, totalDeadlineUtc, incomingAttempts);
            if (_configuration.EnableHandlerLogging)
            {
                await SaveJobLogs(workerContext, logCollector);
            }

            await SaveProgressRows(workerContext, progressCollector);

            PerfTrace.Mark(PerfTrace.SaveCompleted);
            await workerContext.SaveChangesAsync(default);

            PerfTrace.Mark(PerfTrace.CommitTransaction2);
            await endTransaction.CommitAsync(default);
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
            await using var endTransaction = await workerContext.Database.BeginTransactionAsync(default);
            job.CurrentState = State.Deleted;
            job.ExpireAt = cancelNow.Add(_configuration.JobExpirationTimeout);
            job.CancellationMode = CancellationMode.None;
            job.CurrentWorkerId = null;
            job.LastKeepAlive = null;

            // Match FinalizeJobState (and the dispatcher's cancel arm): emit the hourly bucket alongside the
            // lifetime row, or a cancellation is invisible on the Counters chart and the lifetime total stops
            // reconciling with the sum of its own buckets.
            var cancelHourSuffix = cancelNow.ToString("yyyy-MM-dd-HH", CultureInfo.InvariantCulture);
            AddCounters(workerContext, "stats:deleted", $"stats:deleted:{cancelHourSuffix}");
            workerContext.Set<JobLog>().Add(new JobLog
            {
                JobId = job.Id,
                EventType = "Cancelled",
                Timestamp = cancelNow,
                Level = "Information",
                Message = "Job was cancelled by user",
                DurationMs = handlerStopwatch?.Elapsed.TotalMilliseconds,
                WorkerId = _workerId,
            });
            if (_configuration.EnableHandlerLogging)
            {
                await SaveJobLogs(workerContext, logCollector);
            }

            await SaveProgressRows(workerContext, progressCollector);

            await workerContext.SaveChangesAsync(default);
            await endTransaction.CommitAsync(default);
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

            // Scheduled counts as a retry: JobOutcome.RescheduledState returns Scheduled whenever the
            // target time is in the future, and RetryOptions.Delays defaults to [15,60,300] — so testing
            // Enqueued alone labelled every DEFAULT retry as "failed". Must stay in step with the
            // Enqueued-or-Scheduled branch in FinalizeJobState that writes stats:requeued.
            var willRetry = job.CurrentState is State.Enqueued or State.Scheduled;
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

            // Handler exceptions (including intentional test-case throws) are logged at the
            // user's chosen level — Information is enough because the job state transition
            // is recorded separately and the exception message is stored in the JobLog.
            // Full stack traces at Error level during dense multi-server test scenarios produce
            // many MB of log output per CI run without adding diagnostic value.
            _logger.LogInformation("Error executing job {id}: {exceptionType}: {message}", job.Id, e.GetType().Name, e.Message);
            await jobCts.CancelAsync();
            await monitorTask;

            await using var endTransaction = await workerContext.Database.BeginTransactionAsync(default);
            FinalizeJobState(workerContext, job, e, errorDurationMs, outcome, totalDeadlineUtc, incomingAttempts);
            if (_configuration.EnableHandlerLogging)
            {
                await SaveJobLogs(workerContext, logCollector);
            }

            await SaveProgressRows(workerContext, progressCollector);

            await workerContext.SaveChangesAsync(default);
            await endTransaction.CommitAsync(default);
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

        // SignalJobFinalized fires the in-process signal; the transport publish covers
        // cross-process subscribers (other servers' Orchestrator instances, dashboard hub).
        // DispatchAsync would double-fire the local signal — handled here separately.
        _signals.SignalJobFinalized();
        await NotificationDispatch.FireAsync(
            _notificationTransport,
            [new Notification(NotificationKind.JobFinalized, null)],
            cancellationToken);

        PerfTrace.Mark(PerfTrace.Done);
        PerfTrace.End();

        return true;
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

                using var scope = _serviceScopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<IWarpServerContext>().Context;

                if (doCancellationCheck)
                {
                    timeSinceLastCheck = TimeSpan.Zero;

                    var cancellationMode = await context.Set<Job>()
                        .Where(x => x.Id == jobId)
                        .Select(x => x.CancellationMode)
                        .FirstOrDefaultAsync(stoppingToken);

                    if (cancellationMode != CancellationMode.None)
                    {
                        _logger.LogInformation("Job {jobId} cancellation requested ({mode}), cancelling handler", jobId, cancellationMode);

                        // Flush any pending logs/progress before cancelling — they were already drained from the collectors
                        if (pendingLogs.Count > 0)
                        {
                            context.Set<JobLog>().AddRange(pendingLogs);
                        }

                        if (pendingProgress.Count > 0)
                        {
                            context.Set<JobLog>().AddRange(pendingProgress);
                        }

                        if (pendingLogs.Count > 0 || pendingProgress.Count > 0)
                        {
                            await context.SaveChangesAsync(stoppingToken);
                        }

                        await jobCts.CancelAsync();
                        return;
                    }

                    var now = _timeProvider.GetUtcNow().UtcDateTime;
                    await context.Set<Job>()
                        .Where(x => x.Id == jobId)
                        .ExecuteUpdateAsync(x => x.SetProperty(p => p.LastKeepAlive, now), stoppingToken);
                }

                if (pendingLogs.Count > 0)
                {
                    context.Set<JobLog>().AddRange(pendingLogs);
                }

                if (pendingProgress.Count > 0)
                {
                    context.Set<JobLog>().AddRange(pendingProgress);
                }

                if (pendingLogs.Count > 0 || pendingProgress.Count > 0)
                {
                    await context.SaveChangesAsync(stoppingToken);
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
    /// Finalizes job state: clears worker fields, adds counters and log entry.
    /// State must be set on the job before calling this method.
    /// </summary>
    private void FinalizeJobState(DbContext context, Job job, Exception? error, double? durationMs, JobOutcome? outcome = null, DateTime? totalDeadlineUtc = null, long incomingAttempts = 0)
    {
        var state = job.CurrentState;
        job.CancellationMode = CancellationMode.None;
        job.CurrentWorkerId = null;
        job.LastKeepAlive = null;

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var hourSuffix = now.ToString("yyyy-MM-dd-HH", CultureInfo.InvariantCulture);
        var tierSuffix = MetricTiers.Suffix(MetricTier.Fine, now, _configuration.FineResolutionMinutes);

        // Every caught exception (retry attempt or terminal) appends one row to the error-grouping inbox in
        // THIS existing save — no fingerprint computed here (that's the aggregator, off the hot path §8.29).
        if (error != null && _configuration.ErrorGroupingInterval != null)
        {
            var culprit = string.IsNullOrEmpty(job.Type) ? "job" : WarpTelemetry.GetShortTypeName(job.Type);
            context.Set<ErrorOccurrence>().Add(
                ErrorOccurrenceFactory.FromException(ErrorSource.Job, error, culprit, job.TraceId, _configuration.ApplicationName, now, _configuration.ApplicationVersion, _configuration.ApplicationEnvironment));
        }

        if (state == State.Completed)
        {
            job.ExpireAt = now.Add(_configuration.JobExpirationTimeout);
            AddCounters(context, "stats:succeeded", $"stats:succeeded:{hourSuffix}");

            // Always-on execution meters (null-listener, zero cost) — emitted regardless of JobMetricsSink.
            WarpTelemetry.RecordJobExecution(job.Type, job.HandlerType, JobStatsKeys.SucceededToken, durationMs, _configuration.ApplicationName);

            // jobstat Counter rows back the dashboard's per-type/per-handler aggregates; skipped under the
            // Otel sink (the meters carry the data) — the finalization-path perf win. Counter writes only.
            if (_configuration.JobMetricsSink is RecordingSink.Database or RecordingSink.Both)
            {
                AddJobStatsCounters(context, job, JobStatsKeys.SucceededToken, durationMs, tierSuffix);
            }
        }
        else if (state == State.Failed)
        {
            AddCounters(context, "stats:failed", $"stats:failed:{hourSuffix}");

            WarpTelemetry.RecordJobExecution(job.Type, job.HandlerType, JobStatsKeys.FailedToken, durationMs, _configuration.ApplicationName);

            if (_configuration.JobMetricsSink is RecordingSink.Database or RecordingSink.Both)
            {
                AddJobStatsCounters(context, job, JobStatsKeys.FailedToken, durationMs, tierSuffix);
            }
        }
        else if (state == State.Deleted)
        {
            job.ExpireAt = now.Add(_configuration.JobExpirationTimeout);
            AddCounters(context, "stats:deleted", $"stats:deleted:{hourSuffix}");
        }
        else if (state == State.Enqueued || state == State.Scheduled)
        {
            // Covers retry backoff and Mutex Wait — anything that puts the job back on the queue.
            AddCounters(context, "stats:requeued", $"stats:requeued:{hourSuffix}");

            // Always-on meter — the countable signal concurrency and rate limiting never had (they emit
            // spans, which are sampled). It sits HERE, beside the state total it must agree with, and NOT
            // in the reason block below: JobOutcome.Reason is nullable and JobOutcome is public API, so a
            // user-written pipeline behaviour can validly reschedule without one. Emitting only for
            // reason-bearing outcomes would let an "always-on" meter undercount requeues the state total
            // already recorded. Those fall under the same bounded "unknown" token the reason map uses —
            // reason only, never the concurrency or rate-limit key, which are unbounded and PII-adjacent
            // (§1.2) and stay on the span.
            WarpTelemetry.RecordJobRequeued(
                job.Type,
                job.Queue,
                outcome?.Reason is { } requeueReason ? OutcomeReasonTokens.For(requeueReason) : OutcomeReasonTokens.Unknown,
                _configuration.ApplicationName);
        }

        // Reason breakdown. Joins the SaveChanges the state total above already rides, so this adds no
        // round-trip to the hot path (§0.2/§6.1) — just a field read and a switch. A completed job carries
        // no reason, so the happy path writes exactly what it wrote before.
        //
        // Written INDEPENDENTLY of the state total, not derived from it: a reader never has to sum the
        // reasons to get a total, and an outcome with no attributable reason (a plain handler throw with no
        // addon involved) still lands in the total, showing up as the unattributed remainder.
        //
        // There is deliberately NO stats:unsuccessful row. "Not Completed" is exactly failed + deleted, and
        // ten sites write those two keys (worker cancellation, DeleteJob, BulkDelete, crash recovery, …).
        // A stored umbrella has to be maintained at every one of them or it silently under-reports, which is
        // what it did; the Counters page derives it on read instead, where it cannot drift.
        if (outcome?.Reason is { } reason)
        {
            var stateToken = state switch
            {
                State.Failed => "failed",
                State.Deleted => "deleted",
                State.Enqueued or State.Scheduled => "requeued",
                _ => null,
            };

            var token = OutcomeReasonTokens.For(reason);

            if (stateToken != null)
            {
                var key = $"stats:{stateToken}-{token}";
                AddCounters(context, key, $"{key}:{hourSuffix}");
            }

            // Distinct jobs that entered retry, as opposed to the retry EVENTS counted above. A job retried
            // 15 times is 15 events but one job, and "how many jobs are thrashing" was unanswerable.
            //
            // No schema column for this: the per-job counter already exists as RetriedTimes in Job.Metadata,
            // read above for the span tag BEFORE the handler ran — so an incoming count of 0 on a retry
            // outcome is exactly this job's first retry. Reusing that read also keeps the worker free of any
            // dependency on the Retry addon (the literal key is pinned to the property name by a test).
            if (reason == OutcomeReason.Retry && incomingAttempts == 0)
            {
                AddCounters(context, "stats:retried-jobs", $"stats:retried-jobs:{hourSuffix}");
            }
        }

        // Deadline attainment (§8.30): this job carried a Total-scope timeout deadline (§8.7). Emit the
        // attainment denominator on every terminal outcome and a miss whenever the wall clock is past the
        // deadline at finalization — a Total-scope deadline is a time bound, so completing LATE is a miss just
        // as failing/deleting late is (a handler that ignores its cancellation token can reach Completed past
        // the deadline, §8.5). A job that reached a terminal state before the deadline keeps now < deadline ⇒
        // not a miss. Meter is always-on; the DeadlineKeys Counter fold is sink-gated (§8.24), mirroring jobstat.
        if (totalDeadlineUtc is { } deadline && state is State.Completed or State.Failed or State.Deleted)
        {
            var missed = now >= deadline;
            if (missed)
            {
                WarpTelemetry.RecordDeadlineMiss(job.Type, job.Queue, _configuration.ApplicationName);
            }

            if (_configuration.JobMetricsSink is RecordingSink.Database or RecordingSink.Both)
            {
                var type = string.IsNullOrEmpty(job.Type) ? "job" : job.Type;
                foreach (var counter in DeadlineKeys.Build(type, missed, _configuration.ApplicationName, tierSuffix))
                {
                    context.Set<Counter>().Add(counter);
                }
            }
        }

        // Event-type, level, and split-on-retry are shared with WarpDispatcherWorker via
        // FinalizationLogs.Build — both worker paths emit identical log shapes.
        foreach (var log in FinalizationLogs.Build(job, error, durationMs, _workerId, now, outcome))
        {
            context.Set<JobLog>().Add(log);
        }
    }

    private static async Task SaveJobLogs(DbContext context, JobLogCollector collector)
    {
        var entries = collector.Drain();
        if (entries.Count == 0)
        {
            return;
        }

        await context.Set<JobLog>().AddRangeAsync(entries);
    }

    private static async Task SaveProgressRows(DbContext context, JobProgressCollector collector)
    {
        var entries = collector.Drain();
        if (entries.Count == 0)
        {
            return;
        }

        await context.Set<JobLog>().AddRangeAsync(entries);
    }

    private static void AddCounters(DbContext context, string totalKey, string hourlyKey)
    {
        context.Set<Counter>().Add(new Counter { Key = totalKey, Value = 1 });
        context.Set<Counter>().Add(new Counter { Key = hourlyKey, Value = 1 });
    }

    // Per-job-TYPE + per-HANDLER execution counters (§8.19 multi-app observability), sliced by this worker
    // process's executor ApplicationName. Counter writes only — no reads/orchestration — so the fetch/execute
    // hot path stays sacred (§0.2/§6.1). Rides the standard Counter→Statistic fold; the fine (5-min) tier is
    // downsampled to hourly then daily by StatisticRollup (§8.30).
    private void AddJobStatsCounters(DbContext context, Job job, string outcomeToken, double? durationMs, string tierSuffix)
    {
        foreach (var counter in JobStatsKeys.Build(job, outcomeToken, durationMs, _configuration.ApplicationName, tierSuffix))
        {
            context.Set<Counter>().Add(counter);
        }
    }
}
