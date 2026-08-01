using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Warp.Core.Logging;

public static class WarpTelemetry
{
    public const string ServiceName = "Warp";

    /// <summary>
    /// Process-wide origin stamp for cross-application traces. Set once from <c>AddWarp</c> when
    /// <c>WarpConfiguration.ApplicationName</c> is non-null. A process-wide static is acceptable here
    /// because ApplicationName is a deploy-time constant for the whole process — threading it through
    /// every activity-factory call would add plumbing for a value that never varies at runtime. Null
    /// (the default) ⇒ no <c>warp.application</c> tag is added to any Activity (feature off).
    /// </summary>
    internal static string? ApplicationName { get; set; }

    public static readonly ActivitySource ActivitySource = new(ServiceName);
    public static readonly Meter Meter = new(ServiceName);

    public static readonly Histogram<double> JobDuration = Meter.CreateHistogram<double>(
        "warp.job.duration",
        unit: "ms",
        description: "Duration of job handler execution");

    public static readonly UpDownCounter<long> JobsActive = Meter.CreateUpDownCounter<long>(
        "warp.job.active",
        unit: "{job}",
        description: "Number of jobs currently being processed");

    public static readonly Counter<long> JobsCompleted = Meter.CreateCounter<long>(
        "warp.job.completed",
        unit: "{job}",
        description: "Total jobs that finished processing");

    public static readonly Counter<long> JobsEnqueued = Meter.CreateCounter<long>(
        "warp.job.enqueued",
        unit: "{job}",
        description: "Total jobs enqueued for processing");

    public static readonly Counter<long> NotificationsPublished = Meter.CreateCounter<long>(
        "warp.notifications.published",
        unit: "{notification}",
        description: "Total DB-push notifications successfully emitted by the transport");

    public static readonly Counter<long> NotificationPublishFailures = Meter.CreateCounter<long>(
        "warp.notifications.publish_failures",
        unit: "{notification}",
        description: "Total DB-push notifications that failed to publish (transport error). Each failure is also logged at Warning.");

    public static readonly Histogram<double> MediatorDuration = Meter.CreateHistogram<double>(
        "warp.mediator.duration",
        unit: "ms",
        description: "Duration of in-memory IRequest/IStreamRequest execution through IMediator");

    public static readonly Counter<long> DashboardEventsBroadcast = Meter.CreateCounter<long>(
        "warp.dashboard.events.broadcast",
        unit: "{event}",
        description: "Total dashboard-push events broadcast to connected clients (post-coalesce)");

    public static readonly UpDownCounter<long> DashboardConnectionsActive = Meter.CreateUpDownCounter<long>(
        "warp.dashboard.connections.active",
        unit: "{connection}",
        description: "Number of dashboard SignalR connections currently active");

    public static readonly UpDownCounter<long> MediatorInFlight = Meter.CreateUpDownCounter<long>(
        "warp.mediator.in_flight",
        unit: "{request}",
        description: "Number of in-memory mediator requests currently executing");

    public static readonly Counter<long> SagasStarted = Meter.CreateCounter<long>(
        "warp.sagas.started",
        unit: "{saga}",
        description: "Total saga instances created (a [StartsSaga] message arrived for a new correlation key)");

    public static readonly Counter<long> SagasCompleted = Meter.CreateCounter<long>(
        "warp.sagas.completed",
        unit: "{saga}",
        description: "Total saga instances marked completed and removed");

    public static readonly Counter<long> SagasRequeued = Meter.CreateCounter<long>(
        "warp.sagas.requeued",
        unit: "{saga}",
        description: "Total saga messages requeued due to mutex contention or optimistic-concurrency conflict. Reason tag: busy | version | unique.");

    public static readonly UpDownCounter<long> SagasLive = Meter.CreateUpDownCounter<long>(
        "warp.sagas.live",
        unit: "{saga}",
        description: "Per-process net saga count (incremented on start, decremented on completion). Tag: saga_type. Same per-process semantics as warp.jobs.active and warp.mediator.in_flight: aggregate across worker replicas in your OTel backend (sum) to estimate cluster-wide live sagas. Note: a saga started by replica A and completed by replica B will show +1 on A and -1 on B; the per-replica gauge can therefore go negative under restart-heavy workloads where the start-increment was lost to a process restart. For an authoritative point-in-time count, query the dashboard's GET /api/sagas/stats endpoint (reads SagaState directly).");

    public static readonly Counter<long> BackgroundServicesStarted = Meter.CreateCounter<long>(
        "warp.background_services.started",
        unit: "{start}",
        description: "Total WarpBackgroundService ExecuteAsync invocations. Increments once per start attempt, including restarts after faults. Tag: service_name.");

    public static readonly Counter<long> BackgroundServicesFaulted = Meter.CreateCounter<long>(
        "warp.background_services.faulted",
        unit: "{fault}",
        description: "Total WarpBackgroundService faults (ExecuteAsync threw or returned without cancellation). Tags: service_name, exception_type.");

    public static readonly Counter<long> BackgroundServicesLeaseLost = Meter.CreateCounter<long>(
        "warp.background_services.lease_lost",
        unit: "{event}",
        description: "Total singleton WarpBackgroundService lease-loss events detected by Heartbeat. Tag: service_name.");

    public static readonly Counter<long> BackgroundServicesRestarts = Meter.CreateCounter<long>(
        "warp.background_services.restarts",
        unit: "{restart}",
        description: "Total WarpBackgroundService restart attempts (increments each time the supervisor enters the backoff-wait path after a fault). Tag: service_name.");

    public static readonly Counter<long> AdapterCalls = Meter.CreateCounter<long>(
        "warp.adapter.calls",
        unit: "{call}",
        description: "Total completed outbound adapter calls. Tags: adapter, operation, outcome (and group when IncludeGroupInMetrics).");

    public static readonly Histogram<double> AdapterDuration = Meter.CreateHistogram<double>(
        "warp.adapter.duration",
        unit: "ms",
        description: "Duration of a logical outbound adapter call (outermost handler timing). Tags: adapter, operation, outcome (and group when IncludeGroupInMetrics).");

    public static readonly Counter<long> AdapterRecordsDropped = Meter.CreateCounter<long>(
        "warp.adapter.records_dropped",
        unit: "{record}",
        description: "Total adapter call-log records dropped because the recording channel was full. Recording is lossy by design; user calls are never blocked. Tag: adapter.");

    public static readonly Counter<long> AdapterConfigConflicts = Meter.CreateCounter<long>(
        "warp.adapter.config_conflicts",
        unit: "{conflict}",
        description: "Total times a process's local shared-rate-limit policy differed from the persisted AdapterDefinition policy; the persisted policy is enforced. Tag: adapter.");

    public static readonly Counter<long> EndpointRecordsDropped = Meter.CreateCounter<long>(
        "warp.endpoint.records_dropped",
        unit: "{record}",
        description: "Total inbound endpoint call-log records dropped because the recording channel was full. Recording is lossy by design; requests are never blocked or failed.");

    public static readonly Counter<long> EndpointCalls = Meter.CreateCounter<long>(
        "warp.endpoint.calls",
        unit: "{call}",
        description: "Total observed inbound calls to Warp HTTP endpoints. Emitted unconditionally by the observability middleware (independent of the recording Sink). Tags: route, outcome (and application when ApplicationName is set).");

    public static readonly Histogram<double> EndpointDuration = Meter.CreateHistogram<double>(
        "warp.endpoint.duration",
        unit: "ms",
        description: "Duration of an observed inbound Warp HTTP endpoint call. Emitted unconditionally by the observability middleware (independent of the recording Sink). Tags: route, outcome (and application when ApplicationName is set).");

    public static readonly Counter<long> ClientEventsDropped = Meter.CreateCounter<long>(
        "warp.client.events.dropped",
        unit: "{event}",
        description: "Total client (browser) events dropped because the recording channel was full. Recording is lossy by design; the browser is never blocked or failed.");

    public static readonly Counter<long> ClientEvents = Meter.CreateCounter<long>(
        "warp.client.events",
        unit: "{event}",
        description: "Total client (browser) events ingested. Emitted unconditionally by the ingest endpoint (independent of the recording Sink). Tags: type (and application when set).");

    public static readonly Histogram<double> ClientVitals = Meter.CreateHistogram<double>(
        "warp.client.vitals",
        description: "A Core Web Vital sample reported by a browser (ms; CLS is unitless). Emitted unconditionally by the ingest endpoint. Tags: vital (and application when set).");

    public static readonly Histogram<double> JobExecutionDuration = Meter.CreateHistogram<double>(
        "warp.job.execution.duration",
        unit: "ms",
        description: "Duration of a terminal job execution, mirroring the jobstat DB aggregates. Emitted unconditionally at finalization (independent of JobMetricsSink). Tags: job.type, job.handler (routed messages only), outcome (succeeded | failed), application (executor app when set).");

    public static readonly Counter<long> JobExecutionTotal = Meter.CreateCounter<long>(
        "warp.job.execution.total",
        unit: "{execution}",
        description: "Total terminal job executions, mirroring the jobstat DB aggregates. Emitted unconditionally at finalization (independent of JobMetricsSink). Tags: job.type, job.handler (routed messages only), outcome (succeeded | failed), application (executor app when set).");

    public static readonly Counter<long> WebhookDeliveries = Meter.CreateCounter<long>(
        "warp.webhooks.deliveries",
        unit: "{delivery}",
        description: "Total webhook deliveries that reached a terminal outcome. Tag: outcome (delivered | exhausted).");

    public static readonly Counter<long> WebhookAttempts = Meter.CreateCounter<long>(
        "warp.webhooks.attempts",
        unit: "{attempt}",
        description: "Total webhook delivery attempts made by the executor. Tag: outcome (success | failed). The HTTP leg's spans/duration/error counters come from the adapter layer and are not duplicated here.");

    public static readonly Counter<long> WebhookRedeliveries = Meter.CreateCounter<long>(
        "warp.webhooks.redeliveries",
        unit: "{redelivery}",
        description: "Total manual redeliveries triggered on a settled (Delivered | Exhausted) delivery.");

    public static readonly Histogram<double> JobQueueWait = Meter.CreateHistogram<double>(
        "warp.job.queue.wait",
        unit: "ms",
        description: "Time a job spent eligible-but-unclaimed (claim time − ScheduleTime), recorded once per claim. Emitted unconditionally (independent of JobMetricsSink). Tags: queue, application (executor app when set).");

    public static readonly Counter<long> JobDeadlineMiss = Meter.CreateCounter<long>(
        "warp.job.deadline.miss",
        unit: "{miss}",
        description: "A Total-scope timeout deadline (§8.7) was missed by a terminated job. Emitted unconditionally at finalization (independent of JobMetricsSink); the deadline-attainment DB Counter fold is separate and sink-gated. Tags: job.type, queue, application (executor app when set).");

    // Backlog is a point-in-time gauge sampled periodically by the BacklogSampler server task, so it uses
    // ObservableGauges over a snapshot the sampler replaces each tick (SetBacklogSnapshot) — the callbacks
    // report the last sample on the exporter's collection schedule. Empty snapshot ⇒ no measurements.
    private static volatile IReadOnlyList<BacklogSample> _backlogSnapshot = [];

    public static readonly ObservableGauge<long> JobQueueDepth = Meter.CreateObservableGauge(
        "warp.job.queue.depth",
        static () => _backlogSnapshot.Select(x => new Measurement<long>(x.Depth, x.Tags())),
        unit: "{job}",
        description: "Count of Enqueued (eligible) jobs per queue, sampled by the BacklogSampler. Tags: queue, application (when set).");

    public static readonly ObservableGauge<double> JobQueueOldestAge = Meter.CreateObservableGauge(
        "warp.job.queue.oldest_age_seconds",
        static () => _backlogSnapshot.Select(x => new Measurement<double>(x.OldestAgeSeconds, x.Tags())),
        unit: "s",
        description: "Age (seconds) of the oldest Enqueued job per queue, sampled by the BacklogSampler. Tags: queue, application (when set).");

    /// <summary>Replaces the per-queue backlog snapshot the ObservableGauges report. Called each sample tick.</summary>
    public static void SetBacklogSnapshot(IReadOnlyList<BacklogSample> snapshot) => _backlogSnapshot = snapshot;

    /// <summary>
    /// Starts the consumer activity for handler execution when an <see cref="ActivityListener"/>
    /// is attached to the Warp source. Returns null when no listener is registered — workers
    /// must use the <c>?.</c> null-conditional operator on the result so non-OTel deployments
    /// pay zero allocation overhead. Span name follows the OpenTelemetry messaging-spans
    /// convention: <c>process &lt;queue&gt;</c>. Sets messaging.system / operation.name /
    /// operation.type / destination.name; the caller adds message-id, conversation-id, and
    /// warp.* tags.
    /// </summary>
    public static Activity? StartJobActivity(Guid traceId, string? parentSpanId, string queue)
    {
        var activityTraceId = ActivityTraceId.CreateFromString(traceId.ToString("N").AsSpan());
        var activityParentSpanId = IsValidSpanId(parentSpanId)
            ? ActivitySpanId.CreateFromString(parentSpanId.AsSpan())
            : default;
        var parentContext = new ActivityContext(activityTraceId, activityParentSpanId, ActivityTraceFlags.None);
        var spanName = $"{WarpTelemetryAttributes.OperationProcess} {queue}";

        var activity = ActivitySource.StartActivity(spanName, ActivityKind.Consumer, parentContext);
        if (activity == null)
        {
            return null;
        }

        activity.SetTag(WarpTelemetryAttributes.MessagingSystem, WarpTelemetryAttributes.MessagingSystemValue);
        activity.SetTag(WarpTelemetryAttributes.MessagingOperationName, WarpTelemetryAttributes.OperationProcess);
        activity.SetTag(WarpTelemetryAttributes.MessagingOperationType, WarpTelemetryAttributes.OperationProcess);
        activity.SetTag(WarpTelemetryAttributes.MessagingDestinationName, queue);

        if (ApplicationName is not null)
        {
            activity.SetTag(WarpTelemetryAttributes.WarpApplication, ApplicationName);
        }

        return activity;
    }

    /// <summary>
    /// Back-compat shim. New code should call the three-arg overload with the actual queue name.
    /// </summary>
    public static Activity? StartJobActivity(Guid traceId, string? parentSpanId)
        => StartJobActivity(traceId, parentSpanId, "default");

    /// <summary>
    /// Starts a Producer-kind span for a publish operation. Span name "&lt;operation&gt; &lt;queue&gt;"
    /// per OTel messaging convention. Returns null when no listener is attached. The caller sets
    /// messaging.message.id and any per-publish tags after the row id is known.
    /// </summary>
    public static Activity? StartProducerActivity(string queue, string operation)
    {
        var activity = ActivitySource.StartActivity($"{operation} {queue}", ActivityKind.Producer);
        if (activity == null)
        {
            return null;
        }

        activity.SetTag(WarpTelemetryAttributes.MessagingSystem, WarpTelemetryAttributes.MessagingSystemValue);
        activity.SetTag(WarpTelemetryAttributes.MessagingOperationName, operation);
        activity.SetTag(WarpTelemetryAttributes.MessagingOperationType, operation);
        activity.SetTag(WarpTelemetryAttributes.MessagingDestinationName, queue);

        if (ApplicationName is not null)
        {
            activity.SetTag(WarpTelemetryAttributes.WarpApplication, ApplicationName);
        }

        return activity;
    }

    /// <summary>
    /// Starts a Client-kind span for the worker's post-fetch / pre-handler bookkeeping.
    /// Span name "receive &lt;queue&gt;" per OTel messaging convention.
    /// </summary>
    public static Activity? StartReceiveActivity(string queue)
    {
        var activity = ActivitySource.StartActivity($"{WarpTelemetryAttributes.OperationReceive} {queue}", ActivityKind.Client);
        if (activity == null)
        {
            return null;
        }

        activity.SetTag(WarpTelemetryAttributes.MessagingSystem, WarpTelemetryAttributes.MessagingSystemValue);
        activity.SetTag(WarpTelemetryAttributes.MessagingOperationName, WarpTelemetryAttributes.OperationReceive);
        activity.SetTag(WarpTelemetryAttributes.MessagingOperationType, WarpTelemetryAttributes.OperationReceive);
        activity.SetTag(WarpTelemetryAttributes.MessagingDestinationName, queue);

        if (ApplicationName is not null)
        {
            activity.SetTag(WarpTelemetryAttributes.WarpApplication, ApplicationName);
        }

        return activity;
    }

    /// <summary>
    /// Starts an Internal-kind span for in-memory mediator execution. Span name
    /// "process &lt;requestType&gt;"; the request type is treated as the destination so OTel
    /// consumers can filter on <c>messaging.destination.name</c> for in-process routing.
    /// </summary>
    public static Activity? StartMediatorActivity(string requestType, string responseType, string mediatorKind)
    {
        var activity = ActivitySource.StartActivity($"{WarpTelemetryAttributes.OperationProcess} {requestType}", ActivityKind.Internal);
        if (activity == null)
        {
            return null;
        }

        activity.SetTag(WarpTelemetryAttributes.MessagingSystem, WarpTelemetryAttributes.MessagingSystemValue);
        activity.SetTag(WarpTelemetryAttributes.MessagingOperationName, WarpTelemetryAttributes.OperationProcess);
        activity.SetTag(WarpTelemetryAttributes.MessagingOperationType, WarpTelemetryAttributes.OperationProcess);
        activity.SetTag(WarpTelemetryAttributes.MessagingDestinationName, requestType);
        activity.SetTag(WarpTelemetryAttributes.WarpMediatorKind, mediatorKind);
        activity.SetTag(WarpTelemetryAttributes.WarpMediatorResponseType, responseType);

        return activity;
    }

    /// <summary>
    /// Starts an Internal-kind span around a single server-task iteration.
    /// Span name "warp.server_task &lt;taskName&gt;".
    /// </summary>
    public static Activity? StartServerTaskActivity(string taskName)
    {
        var activity = ActivitySource.StartActivity($"warp.server_task {taskName}", ActivityKind.Internal);
        activity?.SetTag(WarpTelemetryAttributes.WarpTaskName, taskName);

        return activity;
    }

    /// <summary>
    /// Starts an Internal-kind span around a concurrency-control acquire attempt (Mutex or
    /// Semaphore). Span name "warp.concurrency_acquire". Caller stamps warp.concurrency.key,
    /// warp.concurrency.limit, and warp.concurrency.acquired before disposing.
    /// </summary>
    public static Activity? StartConcurrencyActivity() => ActivitySource.StartActivity("warp.concurrency_acquire", ActivityKind.Internal);

    /// <summary>
    /// Starts an Internal-kind span around a single rate-limit check. Span name
    /// "warp.rate_limit_check". Caller stamps warp.rate_limit.key, warp.rate_limit.count,
    /// warp.rate_limit.window_seconds, warp.rate_limit.style, and warp.rate_limit.outcome
    /// (one of: acquired, skipped, throttled, lock_contention) before disposing.
    /// </summary>
    public static Activity? StartRateLimitActivity() => ActivitySource.StartActivity("warp.rate_limit_check", ActivityKind.Internal);

    /// <summary>
    /// Starts a Client-kind span around a single outbound adapter call. Span name
    /// <c>"{adapter}.{operation}"</c> (OTel client-span convention). Returns null when no
    /// <see cref="ActivityListener"/> is attached — callers must use <c>?.</c> so non-OTel
    /// deployments pay zero allocation overhead. Stamps warp.adapter.name / warp.adapter.operation;
    /// the caller adds warp.adapter.group / warp.adapter.outcome / error.type before disposing.
    /// </summary>
    public static Activity? StartAdapterActivity(string adapter, string operation)
    {
        var activity = ActivitySource.StartActivity($"{adapter}.{operation}", ActivityKind.Client);
        if (activity == null)
        {
            return null;
        }

        activity.SetTag(WarpTelemetryAttributes.WarpAdapterName, adapter);
        activity.SetTag(WarpTelemetryAttributes.WarpAdapterOperation, operation);

        if (ApplicationName is not null)
        {
            activity.SetTag(WarpTelemetryAttributes.WarpApplication, ApplicationName);
        }

        return activity;
    }

    /// <summary>
    /// Records the always-on <c>warp.job.execution.*</c> meters for one terminal job execution. Meter records
    /// are cheap and null-listener (zero cost with no exporter), so this is safe to call on the worker
    /// fetch/execute hot path (§0.2/§6.1) — it is instrument writes only, no reads/orchestration. Emits
    /// regardless of <c>WarpConfiguration.JobMetricsSink</c> (the sink gates the DB Counter writes only).
    /// The handler tag is added only for routed messages (<paramref name="handlerType"/> non-null); the
    /// application tag only when <paramref name="application"/> (the executor app) is set.
    /// </summary>
    public static void RecordJobExecution(string? jobType, string? handlerType, string outcome, double? durationMs, string? application)
    {
        var tags = new TagList
        {
            { WarpTelemetryAttributes.JobMeterType, jobType },
            { WarpTelemetryAttributes.JobMeterOutcome, outcome },
        };

        if (handlerType is not null)
        {
            tags.Add(WarpTelemetryAttributes.JobMeterHandler, handlerType);
        }

        if (application is not null)
        {
            tags.Add(WarpTelemetryAttributes.MeterApplication, application);
        }

        JobExecutionTotal.Add(1, tags);

        if (durationMs.HasValue)
        {
            JobExecutionDuration.Record(durationMs.Value, tags);
        }
    }

    /// <summary>
    /// Records the always-on <c>warp.job.queue.wait</c> histogram for one claim. Emitted unconditionally
    /// (independent of JobMetricsSink); the DB Counter fold is separate and sink-gated. Application tag only
    /// when the executor app is set. Negative waits (clock skew on a just-activated job) clamp to 0.
    /// </summary>
    public static void RecordQueueWait(string queue, double waitMs, string? application)
    {
        var tags = new TagList
        {
            { WarpTelemetryAttributes.QueueMeterQueue, queue },
        };

        if (application is not null)
        {
            tags.Add(WarpTelemetryAttributes.MeterApplication, application);
        }

        JobQueueWait.Record(Math.Max(0, waitMs), tags);
    }

    /// <summary>
    /// Records the always-on <c>warp.job.deadline.miss</c> counter for one Total-scope job that missed its
    /// deadline (§8.30). Emitted unconditionally at finalization (independent of JobMetricsSink); the
    /// deadline-attainment DB Counter fold is separate and sink-gated. Application tag only when the executor
    /// app is set.
    /// </summary>
    public static void RecordDeadlineMiss(string? jobType, string queue, string? application)
    {
        var tags = new TagList
        {
            { WarpTelemetryAttributes.JobMeterType, jobType },
            { WarpTelemetryAttributes.QueueMeterQueue, queue },
        };

        if (application is not null)
        {
            tags.Add(WarpTelemetryAttributes.MeterApplication, application);
        }

        JobDeadlineMiss.Add(1, tags);
    }

    /// <summary>
    /// Records the always-on <c>warp.endpoint.*</c> meters for one observed inbound endpoint call. Emitted by
    /// the observability middleware for every completed (non-client-aborted) Warp endpoint request, independent
    /// of the recording <c>Sink</c> — so an OTel-only endpoint user reconstructs count / error-rate / latency
    /// (and per-app) from the meters. <paramref name="route"/> is the bounded <c>{method} {template}</c>
    /// identity; the application tag is added only when <paramref name="application"/> is set.
    /// </summary>
    public static void RecordEndpointCall(string route, string outcome, double durationMs, string? application)
    {
        var tags = new TagList
        {
            { WarpTelemetryAttributes.EndpointMeterRoute, route },
            { WarpTelemetryAttributes.EndpointMeterOutcome, outcome },
        };

        if (application is not null)
        {
            tags.Add(WarpTelemetryAttributes.MeterApplication, application);
        }

        EndpointCalls.Add(1, tags);
        EndpointDuration.Record(durationMs, tags);
    }

    /// <summary>
    /// Records the always-on <c>warp.client.events</c> meter for one ingested browser event (§8.27), emitted by
    /// the ingest endpoint independent of the recording <c>Sink</c>. <paramref name="type"/> is the low-cardinality
    /// type token (error/vital/log/event); names/levels stay off the meter tags (§1.2). The application tag is
    /// added only when <paramref name="application"/> is set.
    /// </summary>
    public static void RecordClientEvent(Warp.Core.Enums.ClientEventType type, string? application)
    {
        var tags = new TagList
        {
            { WarpTelemetryAttributes.ClientMeterType, Warp.Core.ClientObservability.ClientEventKeys.TypeToken(type) },
        };

        if (application is not null)
        {
            tags.Add(WarpTelemetryAttributes.MeterApplication, application);
        }

        ClientEvents.Add(1, tags);
    }

    /// <summary>
    /// Records the always-on <c>warp.client.vitals</c> histogram for one Core Web Vital sample (§8.27). The
    /// vital name (LCP/CLS/…) is bounded so it is safe as a tag; the application tag is added only when set.
    /// </summary>
    public static void RecordClientVital(string vital, double value, string? application)
    {
        var tags = new TagList
        {
            { WarpTelemetryAttributes.ClientMeterVital, vital },
        };

        if (application is not null)
        {
            tags.Add(WarpTelemetryAttributes.MeterApplication, application);
        }

        ClientVitals.Record(value, tags);
    }

    /// <summary>
    /// Bound the length of a string used as an OTel span status description. Activity status
    /// descriptions go to OTel exporters and tracing backends; arbitrarily-long exception
    /// messages would bloat span payloads and make UIs unreadable. 256 chars is the convention
    /// used by all worker / mediator / server-task error paths.
    /// </summary>
    internal static string TruncateMessage(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    public static string GetShortTypeName(string? assemblyQualifiedName)
    {
        if (assemblyQualifiedName == null)
        {
            return "unknown";
        }

        var commaIndex = assemblyQualifiedName.IndexOf(',', StringComparison.Ordinal);

        return commaIndex > 0 ? assemblyQualifiedName[..commaIndex] : assemblyQualifiedName;
    }

    private static bool IsValidSpanId(string? value)
    {
        if (value == null || value.Length != 16)
        {
            return false;
        }

        return value.All(char.IsAsciiHexDigit);
    }
}
