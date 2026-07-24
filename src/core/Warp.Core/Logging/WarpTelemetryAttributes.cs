namespace Warp.Core.Logging;

/// <summary>
/// Constants for OpenTelemetry messaging-convention attribute keys plus warp.* extension keys.
/// Centralised here so call sites never typo a key like <c>messaging.operation.name</c>.
/// </summary>
public static class WarpTelemetryAttributes
{
    // Cross-cutting: stamped on every Warp-created Activity when WarpConfiguration.ApplicationName is
    // set, so cross-application traces carry their origin process. Absent when ApplicationName is null.
    public const string WarpApplication = "warp.application";

    public const string MessagingSystem = "messaging.system";
    public const string MessagingOperationName = "messaging.operation.name";
    public const string MessagingOperationType = "messaging.operation.type";
    public const string MessagingDestinationName = "messaging.destination.name";
    public const string MessagingMessageId = "messaging.message.id";
    public const string MessagingConversationId = "messaging.message.conversation_id";
    public const string MessagingBatchMessageCount = "messaging.batch.message_count";
    public const string ErrorType = "error.type";

    public const string MessagingSystemValue = "warp";
    public const string OperationSend = "send";
    public const string OperationReceive = "receive";
    public const string OperationProcess = "process";

    public const string WarpJobKind = "warp.job.kind";
    public const string WarpJobType = "warp.job.type";
    public const string WarpJobScheduled = "warp.job.scheduled";
    public const string WarpJobAttempt = "warp.job.attempt";
    public const string WarpJobMaxAttempts = "warp.job.max_attempts";
    public const string WarpJobStatus = "warp.job.status";
    public const string WarpJobDurationMs = "warp.job.duration_ms";

    public const string WarpWorkerId = "warp.worker.id";
    public const string WarpWorkerGroup = "warp.worker.group";

    public const string WarpMediatorKind = "warp.mediator.kind";
    public const string WarpMediatorResponseType = "warp.mediator.response_type";
    public const string MediatorKindRequest = "request";
    public const string MediatorKindStream = "stream";

    public const string WarpTaskName = "warp.task.name";
    public const string WarpTaskLockHeld = "warp.task.lock_held";
    public const string WarpTaskMessage = "warp.task.message";

    public const string WarpConcurrencyKey = "warp.concurrency.key";
    public const string WarpConcurrencyLimit = "warp.concurrency.limit";
    public const string WarpConcurrencyAcquired = "warp.concurrency.acquired";
    public const string WarpConcurrencyHeldByOtherEvent = "warp.concurrency.held_by_other";

    public const string WarpRateLimitKey = "warp.rate_limit.key";
    public const string WarpRateLimitCount = "warp.rate_limit.count";
    public const string WarpRateLimitWindowSeconds = "warp.rate_limit.window_seconds";
    public const string WarpRateLimitStyle = "warp.rate_limit.style";
    public const string WarpRateLimitOutcome = "warp.rate_limit.outcome";
    public const string WarpRateLimitOutcomeAcquired = "acquired";
    public const string WarpRateLimitOutcomeSkipped = "skipped";
    public const string WarpRateLimitOutcomeThrottled = "throttled";
    public const string WarpRateLimitOutcomeLockContention = "lock_contention";

    /// <summary>
    /// Metadata dictionary keys read by the worker to enrich consumer-span tags. Mirrors property
    /// names on Warp.Core.Retry.IRetryMetadata so the worker can set retry tags without taking a
    /// project-level dependency on the addon. The unit test
    /// <c>WarpTelemetryTests.RetryMetadataKeys_MatchIRetryMetadataPropertyNames</c> pins these
    /// strings to the IRetryMetadata property names — a rename of either side breaks loudly.
    /// </summary>
    public const string RetryMetadataRetriedTimesKey = "RetriedTimes";

    public const string RetryMetadataMaxRetriesKey = "MaxRetries";

    // Span attribute keys for outbound adapter Client-kind spans.
    public const string WarpAdapterName = "warp.adapter.name";
    public const string WarpAdapterOperation = "warp.adapter.operation";
    public const string WarpAdapterGroup = "warp.adapter.group";
    public const string WarpAdapterOutcome = "warp.adapter.outcome";

    // Meter tag keys for warp.adapter.* instruments. Bounded dimensions only (adapter/operation/
    // outcome); group is excluded unless the adapter opts in via IncludeGroupInMetrics.
    public const string AdapterMeterAdapter = "adapter";
    public const string AdapterMeterOperation = "operation";
    public const string AdapterMeterOutcome = "outcome";
    public const string AdapterMeterGroup = "group";

    // Shared low-cardinality meter tag for the origin application (WarpConfiguration.ApplicationName).
    // Added to adapter / endpoint / job-execution meter instruments only when ApplicationName is set.
    public const string MeterApplication = "application";

    // Meter tag keys for warp.endpoint.* instruments. Bounded dimensions only: route (method + template)
    // and outcome. Endpoints carry no operation axis (the route IS the operation, §8.21) and no group tag
    // (group is a diagnostics dimension, not a meter tag here).
    public const string EndpointMeterRoute = "route";
    public const string EndpointMeterOutcome = "outcome";

    // Meter tag keys for warp.job.execution.* instruments. Mirror the jobstat DB dimensions: job type,
    // routed-message handler (omitted when absent), and terminal outcome (succeeded | failed).
    public const string JobMeterType = "job.type";
    public const string JobMeterHandler = "job.handler";
    public const string JobMeterOutcome = "outcome";
}
