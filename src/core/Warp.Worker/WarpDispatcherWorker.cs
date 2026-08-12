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

/// <summary>
/// Worker that receives pre-fetched jobs from a dispatcher channel.
/// Pure executor — handles execution and completion only. Orchestration handled by Orchestrator.
/// Completions are buffered in a per-worker <see cref="CompletionBatch"/> and flushed
/// as a single multi-row transaction when any of: size threshold, time threshold, idle, or shutdown fires.
/// </summary>
public class WarpDispatcherWorker<TContext> : BackgroundService
    where TContext : DbContext
{
    private readonly Guid _workerId;
    private readonly ChannelReader<Job> _jobReader;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WarpDispatcherWorker<TContext>> _logger;
    private readonly WarpServerConfiguration _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly CompletionBatch _batch;
    private readonly DispatcherWorkerAvailability _availability;
    private readonly IWarpNotificationTransport _notificationTransport;
    private readonly ServerTaskSignals<TContext> _signals;

    public WarpDispatcherWorker(
        Guid workerId,
        ChannelReader<Job> jobReader,
        IServiceScopeFactory scopeFactory,
        ILogger<WarpDispatcherWorker<TContext>> logger,
        IOptions<WarpServerConfiguration> configuration,
        TimeProvider timeProvider,
        IWarpNotificationTransport notificationTransport,
        ServerTaskSignals<TContext> signals,
        IDatabaseExceptionClassifier exceptionClassifier,
        DispatcherWorkerAvailability availability)
    {
        _availability = availability;
        _workerId = workerId;
        _jobReader = jobReader;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _configuration = configuration.Value;
        _timeProvider = timeProvider;
        _notificationTransport = notificationTransport;
        _signals = signals;
        _batch = new CompletionBatch(
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
                finally
                {
                    // Hand the slot back. The dispatcher reserved it at claim time, so this is a
                    // release-only site — and it must run for every path out of ProcessJob,
                    // including the ownership guard dropping the job, or the group leaks capacity
                    // and eventually stops claiming.
                    _availability.Release();
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
            if (!await MarkWorkerOwnership(job, CancellationToken.None))
            {
                // Reclaimed while it waited in the channel — another claim owns it now, and may
                // already have run it. Drop it: no execution, no completion, no log rows.
                _logger.LogInformation(
                    "Skipping job {id}: it was recovered or re-claimed while buffered",
                    job.Id);

                return;
            }

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
            handlerScope = _scopeFactory.CreateScope();

            jobContext = handlerScope.ServiceProvider.GetRequiredService<JobContext>();
            jobContext.JobId = job.Id;
            jobContext.TraceId = job.TraceId ?? job.Id;
            jobContext.Metadata = MetadataSerializer.Deserialize(job.Metadata);
            jobContext.ProgressCollector = progressCollector;

            // Deadline attainment (§8.30): capture the Total-scope timeout deadline (§8.7) while the metadata
            // dict is deserialized (see WarpWorkerService for the rationale) so both the success and failure
            // finalization paths can emit the attainment counter. Cheap key probe ⇒ non-timeout jobs pay nothing.
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

            var (counters, finalLogs) = BuildFinalization(job, null, durationMs, successOutcome, totalDeadlineUtc, incomingAttempts);
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

            // Scheduled counts as a retry: JobOutcome.RescheduledState returns Scheduled whenever the
            // target time is in the future, and RetryOptions.Delays defaults to [15,60,300] — so testing
            // Enqueued alone labelled every DEFAULT retry as "failed". Must stay in step with the
            // Enqueued-or-Scheduled branch in BuildFinalization that writes stats:requeued.
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
            _logger.LogError(e, "Error executing job {id}", job.Id);
            await jobCts.CancelAsync();
            await monitorTask;

            var (counters, finalLogs) = BuildFinalization(job, e, errorDurationMs, outcome, totalDeadlineUtc, incomingAttempts);
            var logs = CollectLogs(finalLogs, logCollector, progressCollector).ToArray();

            // Every caught exception (retry or terminal) feeds the error-grouping inbox — no fingerprint on the
            // hot path (§8.29). Persisted in the same batch completion as the counters/logs.
            var culprit = string.IsNullOrEmpty(job.Type) ? "job" : WarpTelemetry.GetShortTypeName(job.Type);
            var occurrence = _configuration.ErrorGroupingInterval != null
                ? ErrorOccurrenceFactory.FromException(ErrorSource.Job, e, culprit, job.TraceId, _configuration.ApplicationName, _timeProvider.GetUtcNow().UtcDateTime, _configuration.ApplicationVersion, _configuration.ApplicationEnvironment)
                : null;

            _batch.Add(new PendingCompletion(job, counters, logs, occurrence));
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

    /// <summary>
    /// Takes individual ownership of a job the dispatcher claimed for the group, and verifies the
    /// claim is still ours. Returns false when it is not — the caller must then drop the job.
    /// <para>
    /// A prefetched job waits in the channel as Processing with the LastKeepAlive stamped at claim
    /// time; nothing refreshes it until execution starts, so StaleJobRecovery can return it to
    /// Enqueued and someone (including this same group) can re-claim it. LastKeepAlive is the claim
    /// token: a re-claim writes a fresh one, so a stale copy fails this guard instead of executing
    /// a job that is already running elsewhere. The guard rides the update we already issue per
    /// job, so it costs no extra round trip (§0.2/§6.1).
    /// </para>
    /// <para>
    /// The same statement also RENEWS the token, which is what makes the guard hold rather than just
    /// narrow the window. Checking alone only rules out a reclaim that already happened: a job that
    /// waited in the channel past InvisibilityTimeout is still stale the instant the check passes, so
    /// recovery could requeue it — and a second worker start it — while this one walks into the
    /// handler. Stamping a fresh LastKeepAlive under the same row lock as the check buys the full
    /// InvisibilityTimeout, by which point RunJobMonitor is renewing on its own cadence.
    /// </para>
    /// </summary>
    private async Task<bool> MarkWorkerOwnership(Job job, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IWarpServerContext>().Context;
        var handlerTypeToSet = job.HandlerType;
        var claimedWorkerId = job.CurrentWorkerId;
        var claimedKeepAlive = job.LastKeepAlive;
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var stillOurs = await context.Set<Job>()
            .Where(x => x.Id == job.Id)
            .Where(x => x.CurrentState == State.Processing)
            .Where(x => x.CurrentWorkerId == claimedWorkerId)
            .Where(x => x.LastKeepAlive == claimedKeepAlive)
            .ExecuteUpdateAsync(
                x => x
                    .SetProperty(p => p.CurrentWorkerId, _workerId)
                    .SetProperty(p => p.LastKeepAlive, now)
                    .SetProperty(p => p.HandlerType, handlerTypeToSet),
                cancellationToken);

        if (stillOurs == 0)
        {
            return false;
        }

        // The "Processing" JobLog is written here, not in WarpDispatcher.FetchAndDistribute.
        // Writing it dispatcher-side would orphan log rows for jobs whose channel-write got
        // cancelled at shutdown (UnclaimUndelivered reverts the row to Enqueued, but the log
        // entry would remain). Writing it on receipt by the actual worker keeps the audit
        // trail truthful and lets us tag the entry with the specific WorkerId, matching
        // single-worker-mode semantics. Reuses the ownership instant so the log timestamp and the
        // renewed keep-alive agree.
        context.Set<JobLog>().Add(new JobLog
        {
            JobId = job.Id,
            EventType = "Processing",
            Timestamp = now,
            Level = "Information",
            Message = $"The job {job.Id} is being processed",
            WorkerId = _workerId,
        });

        // Queue-wait SLI (§8.26): always-on meter + (sink-gated) Counter rows batched into the ownership-mark
        // SaveChanges below — no extra round-trip (§0.2/§6.1). Measured at ownership (the per-worker receipt
        // point); a sub-second skew vs the group claim is acceptable for a wait SLI.
        var waitMs = Math.Max(0, (now - job.ScheduleTime).TotalMilliseconds);
        WarpTelemetry.RecordQueueWait(job.Queue, waitMs, _configuration.ApplicationName);
        if (_configuration.JobMetricsSink is RecordingSink.Database or RecordingSink.Both)
        {
            foreach (var counter in QueueWaitKeys.Build(job.Queue, waitMs, _configuration.ApplicationName, MetricTiers.Suffix(MetricTier.Fine, now, _configuration.FineResolutionMinutes)))
            {
                context.Set<Counter>().Add(counter);
            }
        }

        await context.SaveChangesAsync(cancellationToken);

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

                using var s = _scopeFactory.CreateScope();
                var ctx = s.ServiceProvider.GetRequiredService<IWarpServerContext>().Context;

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
    private (List<Counter> Counters, List<JobLog> FinalLogs) BuildFinalization(Job job, Exception? error, double? durationMs, JobOutcome? outcome, DateTime? totalDeadlineUtc = null, long incomingAttempts = 0)
    {
        var state = job.CurrentState;
        job.CancellationMode = CancellationMode.None;
        job.CurrentWorkerId = null;
        job.LastKeepAlive = null;

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var hourSuffix = now.ToString("yyyy-MM-dd-HH", CultureInfo.InvariantCulture);
        var tierSuffix = MetricTiers.Suffix(MetricTier.Fine, now, _configuration.FineResolutionMinutes);
        var counters = new List<Counter>();
        if (state == State.Completed)
        {
            job.ExpireAt = now.Add(_configuration.JobExpirationTimeout);
            counters.Add(new Counter { Key = "stats:succeeded", Value = 1 });
            counters.Add(new Counter { Key = $"stats:succeeded:{hourSuffix}", Value = 1 });

            // Always-on execution meters (null-listener, zero cost) — emitted regardless of JobMetricsSink.
            WarpTelemetry.RecordJobExecution(job.Type, job.HandlerType, JobStatsKeys.SucceededToken, durationMs, _configuration.ApplicationName);

            // jobstat Counter rows back the dashboard's per-type/per-handler aggregates; skipped under the
            // Otel sink (the meters carry the data) — the finalization-path perf win. Counter writes only.
            if (_configuration.JobMetricsSink is RecordingSink.Database or RecordingSink.Both)
            {
                counters.AddRange(JobStatsKeys.Build(job, JobStatsKeys.SucceededToken, durationMs, _configuration.ApplicationName, tierSuffix));
            }
        }
        else if (state == State.Failed)
        {
            counters.Add(new Counter { Key = "stats:failed", Value = 1 });
            counters.Add(new Counter { Key = $"stats:failed:{hourSuffix}", Value = 1 });

            WarpTelemetry.RecordJobExecution(job.Type, job.HandlerType, JobStatsKeys.FailedToken, durationMs, _configuration.ApplicationName);

            if (_configuration.JobMetricsSink is RecordingSink.Database or RecordingSink.Both)
            {
                counters.AddRange(JobStatsKeys.Build(job, JobStatsKeys.FailedToken, durationMs, _configuration.ApplicationName, tierSuffix));
            }
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

            // Beside the state total it must agree with, NOT in the reason block below — a reasonless
            // reschedule from a user-written pipeline behaviour is still a real requeue, and falls under
            // the bounded "unknown" token. Mirrors WarpWorkerService (§0.2 lockstep).
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
        // There is deliberately NO stats:unsuccessful row — see the matching comment in
        // WarpWorkerService.FinalizeJobState. Both paths must stay identical (§0.2 lockstep).
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
                counters.Add(new Counter { Key = key, Value = 1 });
                counters.Add(new Counter { Key = $"{key}:{hourSuffix}", Value = 1 });
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
                counters.Add(new Counter { Key = "stats:retried-jobs", Value = 1 });
                counters.Add(new Counter { Key = $"stats:retried-jobs:{hourSuffix}", Value = 1 });
            }
        }

        // Deadline attainment (§8.30): mirror FinalizeJobState — attainment denominator on every terminal
        // outcome, a miss whenever the wall clock is past the Total-scope deadline (§8.7) at finalization,
        // including a late Completed (a token-ignoring handler, §8.5). Meter always-on; DB fold sink-gated.
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
                counters.AddRange(DeadlineKeys.Build(type, missed, _configuration.ApplicationName, tierSuffix));
            }
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
