using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
    private readonly WarpCounterBuffer _counterBuffer;

    // Increments staged for the unit of work in flight. Pushed to the shared buffer only once that work
    // COMMITS, so a rollback drops them exactly as the old row-per-increment writes did. Safe as instance
    // state: one WarpWorkerService belongs to one worker and processes one job at a time.
    private readonly List<(string Key, int Value)> _stagedCounters = [];

    // The terminal log rows the current job's latest finalizing attempt added to the worker context, and
    // whether such an attempt was made. Both exist for one rare case: a finalizing write that fails after
    // the handler ran, where the failure arm finalizes the SAME tracked job again
    // (see BeginFinalizingTransactionAsync).
    private readonly List<JobLog> _finalizationLogs = [];

    // The diagnostic rows the current job's finalizing attempts added (the deferred "Processing" row, the
    // handler's log lines, progress rows): unlike the terminal rows they belong to every attempt, so a
    // re-finalization writes them again when the abandoned attempt's COMMIT failed after its save.
    private readonly List<JobLog> _diagnosticLogs = [];
    private readonly DeferredJobLog _processingLog = new();
    private readonly RunningJobMonitor _monitor;
    private bool _finalizationAttempted;

    // The run id the claim stamped on the current job (#347): what a re-finalization's guard compares with.
    private Guid? _claimedRunId;

    // Resolved from the first job's scope: the announcements are internal to Core, and this class's public
    // constructor cannot take them.
    private EnqueueAnnouncements? _announcements;
    private bool _announcementsResolved;

    public WarpWorkerService(Guid workerId, IServiceScopeFactory serviceScopeFactory, ILogger<WarpWorkerService<TContext>> logger, IOptions<WarpServerConfiguration> configuration, WorkerGroupConfiguration groupConfiguration, TimeProvider timeProvider, IWarpSqlQueries<TContext> sqlQueries, IWarpNotificationTransport notificationTransport, ServerTaskSignals<TContext> signals, WarpCounterBuffer counterBuffer)
        : this(workerId, serviceScopeFactory, logger, configuration, groupConfiguration, timeProvider, sqlQueries, notificationTransport, signals, counterBuffer, monitor: null)
    {
    }

    // The host passes the worker group's monitor (rule 6.14); built on its own, a worker watches its jobs alone.
    internal WarpWorkerService(Guid workerId, IServiceScopeFactory serviceScopeFactory, ILogger<WarpWorkerService<TContext>> logger, IOptions<WarpServerConfiguration> configuration, WorkerGroupConfiguration groupConfiguration, TimeProvider timeProvider, IWarpSqlQueries<TContext> sqlQueries, IWarpNotificationTransport notificationTransport, ServerTaskSignals<TContext> signals, WarpCounterBuffer counterBuffer, RunningJobMonitor? monitor)
    {
        _monitor = monitor ?? new RunningJobMonitor(serviceScopeFactory, timeProvider, configuration.Value, sqlQueries.RenewRunningJobsAsync, logger);
        _workerId = workerId;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
        _configuration = configuration.Value;
        _groupConfiguration = groupConfiguration;
        _timeProvider = timeProvider;
        _sqlQueries = sqlQueries;
        _notificationTransport = notificationTransport;
        _signals = signals;

        _counterBuffer = counterBuffer;
    }

    public async Task<bool> GetAndProcessJob(CancellationToken cancellationToken)
    {
        PerfTrace.Begin();

        // Anything still staged belongs to a previous job whose unit of work never committed; drop it.
        _stagedCounters.Clear();

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
            watermark: null,
            cancellationToken);

        if (claimed.Count == 0)
        {
            return false;
        }

        var job = claimed[0];
        _claimedRunId = job.RunId;

        WorkerLog.FetchedJob(_logger, _workerId, job.Id);

        // OTel "receive" span — covers post-fetch / pre-handler bookkeeping (the Processing
        // log row + commit). Closes before the consumer span opens, so receive and process
        // are siblings under the caller's trace, not nested.
        using (var receiveSpan = WarpTelemetry.StartReceiveActivity(job.Queue))
        {
            receiveSpan?.SetTag(WarpTelemetryAttributes.MessagingMessageId, job.Id.ToString());
            receiveSpan?.SetTag(WarpTelemetryAttributes.WarpWorkerId, _workerId.ToString());

            // The claim itself is committed atomically via UPDATE RETURNING. The "Processing" log row is
            // not written here on its own, which cost every job a statement and a commit before its
            // handler started: it goes with the monitor's first flush or the finalizing write (rule 6.12).
            _processingLog.Set(new JobLog
            {
                JobId = job.Id,
                EventType = "Processing",
                Timestamp = now,
                Level = "Information",
                Message = $"The job {job.Id} is being processed",
                WorkerId = _workerId,
            });

            // Queue-wait SLI (§8.26): time the job spent eligible-but-unclaimed. Always-on meter + (sink-gated)
            // increments summed into the process counter buffer — no rows on this SaveChanges and no extra
            // round-trip (§0.2/§6.1), mirroring the jobstat finalization triad (§8.23/§8.24).
            var waitMs = Math.Max(0, (now - job.ScheduleTime).TotalMilliseconds);
            WarpTelemetry.RecordQueueWait(job.Queue, waitMs, _configuration.ApplicationName);
            if (_configuration.JobMetricsSink is RecordingSink.Database or RecordingSink.Both)
            {
                foreach (var counter in QueueWaitKeys.Build(job.Queue, waitMs, _configuration.ApplicationName, MetricTiers.Suffix(MetricTier.Fine, now, _configuration.FineResolutionMinutes)))
                {
                    EmitCounter(counter.Key, counter.Value);
                }
            }

            // The claim is committed, so the queue-wait increments staged above are real.
            CommitStagedCounters();
        }

        // A new job: nothing of the previous job's finalization applies to it.
        _finalizationAttempted = false;
        _finalizationLogs.Clear();
        _diagnosticLogs.Clear();

        var logCollector = new JobLogCollector { JobId = job.Id, TimeProvider = _timeProvider, WorkerId = _workerId };
        var progressCollector = new JobProgressCollector { JobId = job.Id, TimeProvider = _timeProvider, WorkerId = _workerId };
        using var jobCts = new CancellationTokenSource();
        using var monitoring = _monitor.Register(JobRunOwner.Of(job, _workerId), logCollector, progressCollector, _processingLog, jobCts, cancellationToken);

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

        // False when the finalizing write found the row no longer this run's (#347): nothing to announce.
        var written = false;
        JobContext? jobContext = null;
        DateTime? totalDeadlineUtc = null;
        var incomingAttempts = 0L;
        try
        {
            PerfTrace.Mark(PerfTrace.ExecuteHandler);

            WorkerLog.ExecutingJob(_logger, _workerId, job.Id);

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

            handlerStopwatch = Stopwatch.StartNew();
            await ExecuteJob(job, handlerScope.ServiceProvider, jobCts.Token);
            handlerStopwatch.Stop();

            // The handler owns its DbContext and commits its own work (rule 6.17): the worker never
            // resolves or saves it.

            // Read metadata and outcome from handler scope before disposing
            JobSpanTags.SetMaxAttempts(activity, jobContext.Metadata);
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

            WorkerLog.CompletedJob(_logger, _workerId, job.Id);

            PerfTrace.Mark(PerfTrace.CancelKeepAlive);
            monitoring.Dispose();

            PerfTrace.Mark(PerfTrace.BeginTransaction2);
            await using var endTransaction = await BeginFinalizingTransactionAsync(workerContext, job);

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
            written = await CommitFinalizationAsync(workerContext, endTransaction, job);
        }
        catch (OperationCanceledException) when (jobCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            MarkHandlerFailed();

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
            monitoring.Dispose();

            var cancelNow = _timeProvider.GetUtcNow().UtcDateTime;
            await using var endTransaction = await BeginFinalizingTransactionAsync(workerContext, job);
            job.CurrentState = State.Deleted;
            job.ExpireAt = cancelNow.Add(_configuration.JobExpirationTimeout);
            ClearCancellationMode(workerContext, job);
            job.CurrentWorkerId = null;
            job.LastKeepAlive = null;
            job.RunId = null;

            // Same as the success and failure arms: the policy stamped for this attempt (§8.8) lives only
            // in the handler-scope context until a finalizing write persists it. Dropping it here would
            // make a cancelled-then-requeued job re-resolve — the one path that broke stamp-once.
            if (jobContext != null)
            {
                job.Metadata = JsonSerializer.Serialize(jobContext.Metadata);
            }

            // Match FinalizeJobState (and the dispatcher's cancel arm): emit the hourly bucket alongside the
            // lifetime row, or a cancellation is invisible on the Counters chart and the lifetime total stops
            // reconciling with the sum of its own buckets.
            var cancelHourSuffix = cancelNow.ToString("yyyy-MM-dd-HH", CultureInfo.InvariantCulture);
            AddCounters("stats:deleted", $"stats:deleted:{cancelHourSuffix}");
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

            written = await CommitFinalizationAsync(workerContext, endTransaction, job);
        }
        catch (Exception e)
        {
            MarkHandlerFailed();
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
            }
            else
            {
                job.CurrentState = State.Failed;
            }

            // Outside the outcome branch: a throwing handler sets no outcome, and losing the policy stamped
            // for this attempt (§8.8) would let the next one re-resolve to something else.
            if (jobContext != null)
            {
                JobSpanTags.SetMaxAttempts(activity, jobContext.Metadata);
                job.Metadata = JsonSerializer.Serialize(jobContext.Metadata);
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
            monitoring.Dispose();

            await using var endTransaction = await BeginFinalizingTransactionAsync(workerContext, job);
            FinalizeJobState(workerContext, job, e, errorDurationMs, outcome, totalDeadlineUtc, incomingAttempts);
            if (_configuration.EnableHandlerLogging)
            {
                await SaveJobLogs(workerContext, logCollector);
            }

            await SaveProgressRows(workerContext, progressCollector);

            written = await CommitFinalizationAsync(workerContext, endTransaction, job);
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

        if (!written)
        {
            PerfTrace.End();

            return true;
        }

        // SignalJobFinalized fires the in-process signal; the transport publish covers
        // cross-process subscribers (other servers' Orchestrator instances, dashboard hub).
        // DispatchAsync would double-fire the local signal — handled here separately.
        // Both carry the job's parent, so the Orchestrator checks that one parent and not every parent
        // in flight, and the job itself once it is at rest, since a continuation may wait on it (rule
        // 6.11). Both ids are on the claimed row: no query, no extra statement.
        Guid? settled = job.CurrentState is State.Completed or State.Failed or State.Deleted ? job.Id : null;
        _signals.SignalJobFinalized(job.ParentJobId, settled);

        // With push on, the cross-process half goes through the relay, which merges a batch of these into
        // one notification: published here, it was one round trip per job on this path, and on SQL Server
        // a fan-out to every listening process per job (~2 statements per job more than one per batch).
        var finalized = new Notification(NotificationKind.JobFinalized, FinalizedParentsPayload.Encode(job.ParentJobId, settled));
        if (!_announcementsResolved)
        {
            _announcements = workerScope.ServiceProvider.GetService<EnqueueAnnouncements>();
            _announcementsResolved = true;
        }

        if (_announcements is { IsActive: true })
        {
            _announcements.Announce([finalized]);
        }
        else
        {
            await NotificationDispatch.FireAsync(_notificationTransport, [finalized], cancellationToken);
        }

        PerfTrace.Mark(PerfTrace.Done);
        PerfTrace.End();

        return true;
    }

    /// <summary>
    /// Saves and commits a finalization; false when the row is no longer this run's (#347). The update is
    /// guarded by the run id the claim stamped (a concurrency token): a job taken back by StaleJobRecovery and
    /// claimed again carries another run id, so the save updates nothing and EF reports a concurrency
    /// conflict, as it does for a row deleted while the job ran. Nothing of this attempt is then written or
    /// counted: the row belongs to the new run, or to nobody.
    /// </summary>
    private async Task<bool> CommitFinalizationAsync(DbContext workerContext, IDbContextTransaction transaction, Job job)
    {
        try
        {
            await workerContext.SaveChangesAsync(default);
        }
        catch (DbUpdateConcurrencyException)
        {
            _stagedCounters.Clear();
            _logger.LogInformation(
                "Discarding the completion of job {jobId}: its row no longer exists, or was taken back and claimed again while this run held it",
                job.Id);

            return false;
        }

        PerfTrace.Mark(PerfTrace.CommitTransaction2);
        await transaction.CommitAsync(default);
        CommitStagedCounters();

        return true;
    }

    private static async Task ExecuteJob(Job job, IServiceProvider provider, CancellationToken cancellationToken)
    {
        var messageType = JobTypes.Resolve(job.Type!) ?? throw new WarpException($"Unknown type {job.Type}");
        var payload = JsonSerializer.Deserialize(job.Message!, messageType) ?? throw new WarpException($"Unable to deserialize message {job.Message} to type {job.Type}");

        var jobContext = provider.GetRequiredService<JobContext>();

        if (job.HandlerType != null)
        {
            var handlerType = JobTypes.Resolve(job.HandlerType) ?? throw new WarpException($"Unknown handler type {job.HandlerType}");
            jobContext.HandlerType = handlerType;
            await JobDispatcher.ExecuteHandler(payload, messageType, handlerType, provider, cancellationToken);

            return;
        }

        var jobHandlerType = JobDispatcher.DiscoverJobHandler(messageType, provider) ?? throw new WarpException($"No handler registered for {messageType.Name}");
        job.HandlerType = jobHandlerType.AssemblyQualifiedName;
        jobContext.HandlerType = jobHandlerType;
        await JobDispatcher.ExecuteJobHandler(payload, messageType, jobHandlerType, provider, cancellationToken);
    }

    // DeleteJob sets CancellationMode on the ROW while this worker holds the entity it claimed, which
    // still reads None. Assigning None is then no change to EF and the column would be left out of the
    // finalizing UPDATE, leaving a finished job marked Graceful ("Cancelling..." on the dashboard for
    // ever). Marking the property modified puts it in the UPDATE the worker issues anyway; the
    // dispatcher path already writes it (CompletionBatch.MarkFinalizedColumns).
    private static void ClearCancellationMode(DbContext context, Job job)
    {
        job.CancellationMode = CancellationMode.None;
        context.Entry(job).Property(x => x.CancellationMode).IsModified = true;
    }

    /// <summary>
    /// Finalizes job state: clears worker fields, adds counters and log entry.
    /// State must be set on the job before calling this method.
    /// </summary>
    private void FinalizeJobState(DbContext context, Job job, Exception? error, double? durationMs, JobOutcome? outcome = null, DateTime? totalDeadlineUtc = null, long incomingAttempts = 0)
    {
        var state = job.CurrentState;
        ClearCancellationMode(context, job);
        job.CurrentWorkerId = null;
        job.LastKeepAlive = null;
        job.RunId = null;

        // Cleared here and set again only by the states that expire (Completed, Deleted). A job that is
        // re-finalized after a failed completion write was already given an expiry by the Completed
        // branch; left in place, a Failed job would be deleted by ExpirationCleanup, which never happens
        // to a failed job (rule 8.2).
        job.ExpireAt = null;

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
            AddCounters("stats:succeeded", $"stats:succeeded:{hourSuffix}");

            // Always-on execution meters (null-listener, zero cost) — emitted regardless of JobMetricsSink.
            WarpTelemetry.RecordJobExecution(job.Type, job.HandlerType, JobStatsKeys.SucceededToken, durationMs, _configuration.ApplicationName);

            // jobstat Counter rows back the dashboard's per-type/per-handler aggregates; skipped under the
            // Otel sink (the meters carry the data) — the finalization-path perf win. Counter writes only.
            if (_configuration.JobMetricsSink is RecordingSink.Database or RecordingSink.Both)
            {
                AddJobStatsCounters(job, JobStatsKeys.SucceededToken, durationMs, tierSuffix);
            }
        }
        else if (state == State.Failed)
        {
            AddCounters("stats:failed", $"stats:failed:{hourSuffix}");

            WarpTelemetry.RecordJobExecution(job.Type, job.HandlerType, JobStatsKeys.FailedToken, durationMs, _configuration.ApplicationName);

            if (_configuration.JobMetricsSink is RecordingSink.Database or RecordingSink.Both)
            {
                AddJobStatsCounters(job, JobStatsKeys.FailedToken, durationMs, tierSuffix);
            }
        }
        else if (state == State.Deleted)
        {
            job.ExpireAt = now.Add(_configuration.JobExpirationTimeout);
            AddCounters("stats:deleted", $"stats:deleted:{hourSuffix}");
        }
        else if (state == State.Enqueued || state == State.Scheduled)
        {
            // Covers retry backoff and Mutex Wait — anything that puts the job back on the queue.
            AddCounters("stats:requeued", $"stats:requeued:{hourSuffix}");

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
                AddCounters(key, $"{key}:{hourSuffix}");
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
                AddCounters("stats:retried-jobs", $"stats:retried-jobs:{hourSuffix}");
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
            // A job the timeout itself ended is a miss whatever the clock reads here. The timeout runs on a
            // timer and this compares wall-clock instants, so the finalization of a job killed AT its
            // deadline can land a few milliseconds before it and would otherwise count as met.
            var missed = now >= deadline || outcome?.Reason == OutcomeReason.Timeout;
            if (missed)
            {
                WarpTelemetry.RecordDeadlineMiss(job.Type, job.Queue, _configuration.ApplicationName);
            }

            if (_configuration.JobMetricsSink is RecordingSink.Database or RecordingSink.Both)
            {
                var type = string.IsNullOrEmpty(job.Type) ? "job" : job.Type;
                foreach (var counter in DeadlineKeys.Build(type, missed, _configuration.ApplicationName, tierSuffix))
                {
                    EmitCounter(counter.Key, counter.Value);
                }
            }
        }

        // Event-type, level, and split-on-retry are shared with WarpDispatcherWorker via
        // FinalizationLogs.Build — both worker paths emit identical log shapes.
        foreach (var log in FinalizationLogs.Build(job, error, durationMs, _workerId, now, outcome))
        {
            context.Set<JobLog>().Add(log);
            _finalizationLogs.Add(log);
        }
    }

    private async Task SaveJobLogs(DbContext context, JobLogCollector collector)
    {
        var entries = collector.Drain();
        if (entries.Count == 0)
        {
            return;
        }

        await context.Set<JobLog>().AddRangeAsync(entries);
        _diagnosticLogs.AddRange(entries);
    }

    private async Task SaveProgressRows(DbContext context, JobProgressCollector collector)
    {
        var entries = collector.Drain();
        if (entries.Count == 0)
        {
            return;
        }

        await context.Set<JobLog>().AddRangeAsync(entries);
        _diagnosticLogs.AddRange(entries);
    }

    /// <summary>
    /// The single place a counter increment leaves this worker.
    /// <para>
    /// Normally it is summed into the process-wide <see cref="WarpCounterBuffer"/> and written out by
    /// <c>CounterBufferFlusher</c> as one row per distinct key per interval, rather than a row per
    /// increment riding the finalizing SaveChanges. A finalizing job emits ~20 increments against the
    /// same ~20 keys, so at 500k jobs that was ten million rows folding down to twenty.
    /// </para>
    /// <para>
    /// Staged, not buffered: the increment is held on the worker until the unit of work it belongs to
    /// COMMITS, then moved to the shared buffer by <see cref="CommitStagedCounters"/>. An attempt whose
    /// transaction rolls back therefore contributes nothing, exactly as the per-increment rows did when
    /// they rode the finalizing SaveChanges. The flush window is still lossy on an ungraceful exit
    /// (§6.2 — counters are diagnostics, not an audit trail), but a rollback is not a loss case.
    /// </para>
    /// </summary>
    private void EmitCounter(string key, int value)
    {
        _stagedCounters.Add((key, value));
    }

    /// <summary>
    /// Opens the transaction that finalizes a job, discarding anything still staged from an attempt
    /// that never committed.
    /// <para>
    /// Every finalization arm — success, graceful cancel and failure — stages its increments INSIDE
    /// this transaction, so starting one is exactly the moment the previous unit of work is known to
    /// be over. Clearing here rather than in each arm is the point: when the success arm's
    /// SaveChanges or COMMIT throws, control lands in one of the catch arms, which finalizes the job
    /// a second time and commits. Without this, that second commit hands the buffer BOTH sets and the
    /// job is counted as succeeded and failed — <c>stats:succeeded</c> and <c>stats:failed</c> both
    /// move for one job, breaking the reconciliation §8.33 requires and letting the attributed reason
    /// breakdown exceed its own state total.
    /// </para>
    /// <para>
    /// Clearing at the top of each arm would work today and rot tomorrow: a new arm is written by
    /// copying an existing one, and the clear is the line with no visible effect on the path being
    /// copied. Pairing it with the transaction start makes it impossible to open a finalizing
    /// transaction without it.
    /// </para>
    /// </summary>
    private async Task<IDbContextTransaction> BeginFinalizingTransactionAsync(DbContext workerContext, Job job)
    {
        _stagedCounters.Clear();

        if (_finalizationAttempted)
        {
            // An earlier finalizing attempt on this job did not commit, and this one finalizes the same
            // tracked job to a different outcome. Three things of the abandoned attempt would otherwise
            // leak into this write. First, its terminal log row, which is still Added when the save threw,
            // so the job would get a Completed log beside its Failed one. Second, what EF believes the
            // row holds: when the save went through and the COMMIT threw, the change tracker has accepted
            // the abandoned values while the database rolled them back, so a column this attempt sets to
            // the same value (the cleared worker id and keep-alive) would not be written and the Failed
            // row would keep them. Marking the whole row modified writes every column from the entity,
            // payload included, and that cost is paid only on this path.
            foreach (var log in _finalizationLogs)
            {
                var entry = workerContext.Entry(log);
                if (entry.State == EntityState.Added)
                {
                    entry.State = EntityState.Detached;
                }
            }

            // Third, the diagnostic rows. When the abandoned attempt's save went through and its COMMIT
            // threw, EF accepted them as saved (Unchanged) while the database rolled them back, so this
            // write would leave them out and the job would lose its "Processing" row, its handler's log
            // lines and its progress. A save that threw left them Added, which this write inserts anyway.
            foreach (var log in _diagnosticLogs)
            {
                var entry = workerContext.Entry(log);
                if (entry.State == EntityState.Unchanged)
                {
                    entry.State = EntityState.Added;
                }
            }

            workerContext.Entry(job).State = EntityState.Modified;

            // The run id is the concurrency token the update is guarded by (#347). When the abandoned
            // attempt's save went through and its COMMIT threw, EF accepted the cleared run id as the row's
            // value while the database rolled it back: guarded on null, this write would find no row and
            // be discarded, leaving the job Processing. The row still holds the claim's run id.
            workerContext.Entry(job).Property(x => x.RunId).OriginalValue = _claimedRunId;
        }

        _finalizationLogs.Clear();
        _finalizationAttempted = true;

        // Still here when the job finished before the monitor's first flush: it goes with this write.
        // Not one of _finalizationLogs, so an attempt that fails leaves it in place for the next.
        if (_processingLog.Take() is { } processing)
        {
            workerContext.Set<JobLog>().Add(processing);
            _diagnosticLogs.Add(processing);
        }

        // No savepoint around the finalizing save (rule 6.12): a save that fails abandons this transaction,
        // and the arm that finalizes again opens a new one. The SAVEPOINT / RELEASE pair EF adds by
        // default was two statements on every job.
        workerContext.Database.AutoSavepointsEnabled = false;

        return await workerContext.Database.BeginTransactionAsync(default);
    }

    /// <summary>
    /// Moves everything staged for the just-committed unit of work into the shared buffer. Called after
    /// each commit; staged increments left behind by a unit of work that threw are dropped by whichever
    /// comes first — the next finalizing transaction on this job
    /// (<see cref="BeginFinalizingTransactionAsync"/>) or the next job's iteration.
    /// </summary>
    private void CommitStagedCounters()
    {
        if (_stagedCounters.Count == 0)
        {
            return;
        }

        foreach (var (key, value) in _stagedCounters)
        {
            _counterBuffer.Add(key, value);
        }

        _stagedCounters.Clear();
    }

    private void AddCounters(string totalKey, string hourlyKey)
    {
        EmitCounter(totalKey, 1);
        EmitCounter(hourlyKey, 1);
    }

    // Per-job-TYPE + per-HANDLER execution counters (§8.19 multi-app observability), sliced by this worker
    // process's executor ApplicationName. Counter writes only — no reads/orchestration — so the fetch/execute
    // hot path stays sacred (§0.2/§6.1). Rides the standard Counter→Statistic fold; the fine (5-min) tier is
    // downsampled to hourly then daily by StatisticRollup (§8.30).
    private void AddJobStatsCounters(Job job, string outcomeToken, double? durationMs, string tierSuffix)
    {
        foreach (var counter in JobStatsKeys.Build(job, outcomeToken, durationMs, _configuration.ApplicationName, tierSuffix))
        {
            EmitCounter(counter.Key, counter.Value);
        }
    }

    // Before the handler scope is disposed: what a failed or cancelled attempt staged is discarded on
    // purpose, so the publishers' unsaved-staged-jobs warning stays quiet for it.
    private static void MarkHandlerFailed()
    {
        if (JobExecutionContext.Current is { } execution)
        {
            execution.HandlerFailed = true;
        }
    }
}
