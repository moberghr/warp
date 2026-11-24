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

/// <summary>
/// Worker that receives pre-fetched jobs from a dispatcher channel.
/// Pure executor — handles execution and completion only. Orchestration handled by Orchestrator.
/// Completions go to the group's <see cref="CompletionFlusher{TContext}"/>, which writes them; the worker
/// never waits on that write (rule 6.13).
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
    private readonly CompletionFlusher<TContext> _flusher;
    private readonly RunningJobMonitor _monitor;
    private readonly DispatcherWorkerAvailability _availability;
    private readonly WarpCounterBuffer _counterBuffer;

    internal WarpDispatcherWorker(
        Guid workerId,
        ChannelReader<Job> jobReader,
        IServiceScopeFactory scopeFactory,
        ILogger<WarpDispatcherWorker<TContext>> logger,
        IOptions<WarpServerConfiguration> configuration,
        TimeProvider timeProvider,
        DispatcherWorkerAvailability availability,
        WarpCounterBuffer counterBuffer,
        CompletionFlusher<TContext> flusher,
        RunningJobMonitor monitor)
    {
        _monitor = monitor;
        _counterBuffer = counterBuffer;
        _availability = availability;
        _workerId = workerId;
        _jobReader = jobReader;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _configuration = configuration.Value;
        _timeProvider = timeProvider;
        _flusher = flusher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Channel-based pull: WaitToReadAsync blocks until the dispatcher produces a job.
        // No idle polling loop here — polling backoff lives in WarpDispatcher.ExecuteAsync.
        // WaitToReadAsync does NOT observe stoppingToken: if we exited on cancellation while the
        // channel still had buffered items, those jobs would be DB-orphaned as Processing (the
        // dispatcher wrote them but nobody consumed them). Instead we drain the channel fully
        // and exit only when the dispatcher completes its writer on its own shutdown. The host's
        // shutdown timeout (30s default) still bounds this — a stuck handler eventually gets
        // killed — and stoppingToken is still propagated into ProcessJob so individual
        // handler-path awaits can react if they want to.
        // When this worker last handed a slot back, for the opt-in idle timing; 0 until its first job.
        var idleSince = 0L;
        while (await _jobReader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            while (_jobReader.TryRead(out var job))
            {
                var busySince = DispatcherDiagnostics.WorkerBusy.Enabled ? RecordPickup(job, idleSince) : 0L;
                var remaining = 1;
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
                    remaining = _availability.Release();
                }

                // The group's flusher writes the completion (rule 6.13); this worker only tells it when
                // the group had nothing left claimed as this slot came back, and goes straight back to the
                // channel. From the count Release returned, not a later read: the dispatcher, woken by the
                // freed slot, may already have claimed the next job.
                _flusher.SlotReleased(remaining);

                if (busySince != 0)
                {
                    DispatcherDiagnostics.WorkerBusy.Add(DispatcherDiagnostics.Since(busySince));
                    idleSince = Stopwatch.GetTimestamp();
                }
            }
        }
    }

    // Opt-in timings (DispatcherDiagnostics): how long this worker waited since its last job, and how
    // long this job waited in the channel after its claim. Returns when the job's busy time starts.
    private static long RecordPickup(Job job, long idleSince)
    {
        if (idleSince != 0)
        {
            DispatcherDiagnostics.WorkerIdle.Add(DispatcherDiagnostics.Since(idleSince));
        }

        if (DispatcherDiagnostics.ClaimedAt.TryRemove(job.Id, out var claimedAt))
        {
            DispatcherDiagnostics.ChannelWait.Record(DispatcherDiagnostics.Since(claimedAt));
        }

        return Stopwatch.GetTimestamp();
    }

    private void AddCompletion(PendingCompletion completion) => _flusher.Add(completion);

    private async Task ProcessJob(Job job, CancellationToken cancellationToken)
    {
        PerfTrace.Begin();
        var processingLog = new DeferredJobLog();

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
            if (!await ConfirmOwnership(job, processingLog, CancellationToken.None))
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
        using var monitoring = _monitor.Register(job.Id, _workerId, logCollector, progressCollector, processingLog, jobCts, cancellationToken);

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
            var logs = CollectLogs(finalLogs, logCollector, progressCollector, processingLog).ToArray();
            AddCompletion(new PendingCompletion(job, counters, logs));
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
            monitoring.Dispose();

            var cancelNow = _timeProvider.GetUtcNow().UtcDateTime;
            job.CurrentState = State.Deleted;
            job.ExpireAt = cancelNow.Add(_configuration.JobExpirationTimeout);
            job.CancellationMode = CancellationMode.None;
            job.CurrentWorkerId = null;
            job.LastKeepAlive = null;

            // Same as the success and failure arms: the policy stamped for this attempt (§8.8) lives only
            // in the handler-scope context until a finalizing write persists it. Dropping it here would
            // make a cancelled-then-requeued job re-resolve — the one path that broke stamp-once.
            if (jobContext != null)
            {
                job.Metadata = JsonSerializer.Serialize(jobContext.Metadata);
            }

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
            var logs = CollectLogs([cancelLog], logCollector, progressCollector, processingLog).ToArray();
            AddCompletion(new PendingCompletion(job, cancelCounters, logs));
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
            monitoring.Dispose();

            var (counters, finalLogs) = BuildFinalization(job, e, errorDurationMs, outcome, totalDeadlineUtc, incomingAttempts);
            var logs = CollectLogs(finalLogs, logCollector, progressCollector, processingLog).ToArray();

            // Every caught exception (retry or terminal) feeds the error-grouping inbox — no fingerprint on the
            // hot path (§8.29). Persisted in the same batch completion as the counters/logs.
            var culprit = string.IsNullOrEmpty(job.Type) ? "job" : WarpTelemetry.GetShortTypeName(job.Type);
            var occurrence = _configuration.ErrorGroupingInterval != null
                ? ErrorOccurrenceFactory.FromException(ErrorSource.Job, e, culprit, job.TraceId, _configuration.ApplicationName, _timeProvider.GetUtcNow().UtcDateTime, _configuration.ApplicationVersion, _configuration.ApplicationEnvironment)
                : null;

            AddCompletion(new PendingCompletion(job, counters, logs, occurrence));
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
    /// InvisibilityTimeout, by which point the group's RunningJobMonitor is renewing it.
    /// </para>
    /// </summary>
    private async Task<bool> ConfirmOwnership(Job job, DeferredJobLog processingLog, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // A job claimed a moment ago cannot have been reclaimed: StaleJobRecovery takes a job back only
        // once its LastKeepAlive is a whole InvisibilityTimeout old, and the claim stamped it just now.
        // So the guarded update below, a statement and a commit for every job before its handler runs,
        // is spent only on a job that sat in the channel long enough for a reclaim to be possible
        // (rule 6.12). The window is half the timeout, less the time the monitor takes to send the first
        // keep-alive: the other half is margin for two servers' clocks disagreeing.
        var waited = job.LastKeepAlive is { } claimedAt ? now - claimedAt : TimeSpan.MaxValue;
        var freshFor = (_configuration.InvisibilityTimeout / 2) - _configuration.CancellationCheckInterval;
        if ((waited < TimeSpan.Zero || waited >= freshFor) && !await RenewClaim(job, now, cancellationToken))
        {
            return false;
        }

        // Written with the monitor's first flush or the completion, not here on its own (DeferredJobLog).
        // It is still the receiving worker's row, stamped with the instant it took the job: a job handed
        // back at shutdown before any worker received it has no such row.
        processingLog.Set(new JobLog
        {
            JobId = job.Id,
            EventType = "Processing",
            Timestamp = now,
            Level = "Information",
            Message = $"The job {job.Id} is being processed",
            WorkerId = _workerId,
        });

        // Queue-wait SLI (§8.26): always-on meter + (sink-gated) increments. The claim that made this job
        // ours is already committed, so they go straight to the buffer.
        var waitMs = Math.Max(0, (now - job.ScheduleTime).TotalMilliseconds);
        WarpTelemetry.RecordQueueWait(job.Queue, waitMs, _configuration.ApplicationName);
        if (_configuration.JobMetricsSink is RecordingSink.Database or RecordingSink.Both)
        {
            foreach (var counter in QueueWaitKeys.Build(job.Queue, waitMs, _configuration.ApplicationName, MetricTiers.Suffix(MetricTier.Fine, now, _configuration.FineResolutionMinutes)))
            {
                _counterBuffer.Add(counter.Key, counter.Value);
            }
        }

        return true;
    }

    // The guard for a job that waited: still Processing, still stamped by the claim that handed it to
    // this worker. The same statement renews the stamp, under the row lock the check holds, which buys
    // a whole InvisibilityTimeout, by when the group's RunningJobMonitor is renewing it.
    private async Task<bool> RenewClaim(Job job, DateTime now, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IWarpServerContext>().Context;
        var claimedWorkerId = job.CurrentWorkerId;
        var claimedKeepAlive = job.LastKeepAlive;
        var stillOurs = await context.Set<Job>()
            .Where(x => x.Id == job.Id)
            .Where(x => x.CurrentState == State.Processing)
            .Where(x => x.CurrentWorkerId == claimedWorkerId)
            .Where(x => x.LastKeepAlive == claimedKeepAlive)
            .ExecuteUpdateAsync(
                x => x
                    .SetProperty(p => p.CurrentWorkerId, _workerId)
                    .SetProperty(p => p.LastKeepAlive, now),
                cancellationToken);

        return stillOurs > 0;
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

        // The state pair, the job's stats keys and a few outcome keys: sized so the job's counters do not
        // grow the list by doubling.
        var counters = new List<Counter>((2 * JobStatsKeys.MaxPerDimension) + 8);
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
                counters.AddRange(DeadlineKeys.Build(type, missed, _configuration.ApplicationName, tierSuffix));
            }
        }

        // Event-type, level, and split-on-retry are shared with WarpWorkerService via
        // FinalizationLogs.Build — both worker paths emit identical log shapes.
        var logs = FinalizationLogs.Build(job, error, durationMs, _workerId, now, outcome);

        return (counters, logs.ToList());
    }

    private IEnumerable<JobLog> CollectLogs(IReadOnlyList<JobLog> finalLogs, JobLogCollector collector, JobProgressCollector progressCollector, DeferredJobLog processingLog)
    {
        // Still here when the job finished before the monitor's first flush: it goes with the completion.
        if (processingLog.Take() is { } processing)
        {
            yield return processing;
        }

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
