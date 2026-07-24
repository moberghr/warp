using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace Warp.Core;

public class WarpConfiguration
{
    public string DefaultQueue { get; set; } = "default";

    /// <summary>
    /// Assemblies whose IRequestHandler / IJobHandler / IMessageHandler /
    /// IStreamRequestHandler registrations should be removed after the source generator
    /// applies them. The generator unconditionally registers every handler it discovers
    /// across the current project and its references; this list lets the host opt
    /// specific assemblies out — typical use is a multi-host solution where one host
    /// references a sibling host's handlers transitively and doesn't want them in DI.
    /// Populated via <c>opt.ExcludeHandlersFromAssembly(...)</c>.
    /// </summary>
    internal HashSet<Assembly> ExcludedHandlerAssemblies { get; } = [];

    public string? Schema { get; set; } = "warp";

    /// <summary>
    /// Development diagnostic: when a scope that staged jobs/messages via <c>IPublisher</c> ends
    /// without <c>SaveChangesAsync</c> (they would be silently discarded and never run), log a
    /// Warning. Cheap change-tracker check on publisher dispose; set <c>false</c> to disable.
    /// Never throws, never blocks.
    /// </summary>
    public bool WarnOnUnsavedStagedJobs { get; set; } = true;

    /// <summary>
    /// How long completed and deleted jobs are retained before cleanup.
    /// Failed jobs are never auto-expired.
    /// </summary>
    public TimeSpan JobExpirationTimeout { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    /// Extension point for external/third-party addons (e.g. provider packages) to
    /// contribute entities to the Warp DbContext model. Invoked by WarpModelCustomizer
    /// after the core and addon entities are registered.
    /// <para>
    /// In-tree addons (CircuitBreaker, Concurrency, RateLimit, Sagas) do NOT use this
    /// list — their entities are registered unconditionally by WarpModelCustomizer so a
    /// single migration covers every deployment shape regardless of which hosts opt in
    /// to the runtime behavior.
    /// </para>
    /// </summary>
    public List<Action<ModelBuilder, string?>> EntityConfigurators { get; } = [];

    /// <summary>
    /// How long the host waits for each <c>WarpBackgroundService.ExecuteAsync</c> to return
    /// after the cancellation token is signalled during graceful shutdown. Services that do not
    /// observe cancellation are abandoned at process exit — same semantics as plain
    /// <c>BackgroundService.StopAsync</c> with a timeout.
    /// </summary>
    public TimeSpan BackgroundServiceShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Global default for the maximum number of captured log rows retained per
    /// <c>WarpBackgroundService</c> instance. Oldest rows are deleted by
    /// <c>ExpirationCleanup</c> when the count exceeds this value. Per-service overrides
    /// via <c>WarpBackgroundService.LogRetentionCountOverride</c> take precedence.
    /// </summary>
    public int BackgroundServiceLogRetentionCount { get; set; } = 1000;

    /// <summary>
    /// Global default for the maximum age of captured log rows. Rows older than this value
    /// are deleted by <c>ExpirationCleanup</c>. Per-service overrides via
    /// <c>WarpBackgroundService.LogRetentionAgeOverride</c> take precedence.
    /// </summary>
    public TimeSpan BackgroundServiceLogRetentionAge { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Grace window before an orphaned <c>BackgroundServiceDefinition</c> row is deleted by
    /// <c>ExpirationCleanup</c>. A Definition is considered orphaned when no live
    /// <c>BackgroundServiceInstance</c> references its name AND its <c>LastSeenAt</c> is
    /// older than this value. The grace exists solely to absorb the rolling-deploy gap
    /// between server A's exit (its Instance is cleaned) and server B's startup
    /// registration — without it the Definition would be deleted and immediately recreated,
    /// losing <c>FirstSeenAt</c> history. Increase for environments with longer deploys.
    /// </summary>
    public TimeSpan BackgroundServiceDefinitionOrphanGrace { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Global default retention for <c>AdapterCallLog</c> rows. The adapter flusher stamps each
    /// row's <c>ExpireAt</c> at <c>Timestamp + retention</c>; <c>ExpirationCleanup</c> deletes rows
    /// past <c>ExpireAt</c>. Per-adapter overrides via <c>WarpAdapterOptions.CallLogRetention</c>
    /// take precedence. Call logs are diagnostics, not an audit trail — same lossy, bounded stance
    /// as <c>JobLog</c>/<c>ServerLog</c>.
    /// </summary>
    public TimeSpan AdapterCallLogRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Global default <b>count</b> cap for <c>AdapterCallLog</c> rows — keep at most this many rows per
    /// adapter, deleting the oldest beyond the cap (by <c>Timestamp</c>). Complements the age cap
    /// (<see cref="AdapterCallLogRetention"/>): a row is removed once it exceeds <b>either</b> limit,
    /// so a hot adapter is bounded by row count between age sweeps. <c>null</c> (default) disables the
    /// count cap. Per-adapter override via <c>WarpAdapterOptions.CallLogRetentionCount</c> takes precedence.
    /// </summary>
    public int? AdapterCallLogRetentionCount { get; set; }

    /// <summary>
    /// Grace window before an orphaned <c>AdapterDefinition</c> row is deleted by
    /// <c>ExpirationCleanup</c>. A definition is considered orphaned when its <c>LastSeenAt</c> is
    /// older than this value (adapters run in non-server processes, so there is no live-instance
    /// signal — staleness alone drives removal). Mirrors
    /// <see cref="BackgroundServiceDefinitionOrphanGrace"/>.
    /// <para>
    /// <b>INVARIANT:</b> this grace MUST stay comfortably larger than
    /// <c>AdapterFlush.LastSeenStaleThreshold</c> (the flusher's lazy-refresh cadence, 5 min). Between
    /// refreshes an actively-used adapter's <c>LastSeenAt</c> is up to <c>LastSeenStaleThreshold</c>
    /// stale; if the grace were ≤ that window, cleanup would delete a live adapter's definition (wiping
    /// <c>SharedPolicyJson</c>/<c>Hash</c>/<c>HasPolicyConflict</c>/<c>FirstSeenAt</c>) and re-insert it
    /// on the next flush. Default 30 min = 6× the refresh cadence — do not lower below it without also
    /// lowering the threshold. If you change one, change the other.
    /// </para>
    /// </summary>
    public TimeSpan AdapterDefinitionOrphanGrace { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Global default retention for <c>WebhookDelivery</c> rows. <c>SendAsync</c> stamps each row's
    /// <c>ExpireAt</c> at <c>CreatedAt + retention</c>; <c>ExpirationCleanup</c> deletes rows past
    /// <c>ExpireAt</c>. Delivery rows are operational history, not an audit trail — same lossy, bounded
    /// stance as <c>AdapterCallLog</c>. Aligned by default with the <c>warp-webhooks</c> adapter's
    /// <c>CallLogRetention</c> so a delivery and its attempt rows expire together.
    /// </summary>
    public TimeSpan WebhookDeliveryRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Global <b>count</b> cap for settled (<c>Delivered</c>/<c>Exhausted</c>) <c>WebhookDelivery</c> rows —
    /// keep at most this many, deleting the oldest beyond the cap (by <c>CreatedAt</c>). Complements the age
    /// cap (<see cref="WebhookDeliveryRetention"/>): a settled row is removed once it exceeds <b>either</b>
    /// limit. <c>Pending</c> deliveries are never count-trimmed (they still own live work). <c>null</c>
    /// (default) disables the count cap.
    /// </summary>
    public int? WebhookDeliveryRetentionCount { get; set; }

    /// <summary>
    /// Global default retention for <c>EndpointCallLog</c> rows (inbound endpoint observability). The
    /// inbound middleware stamps each row's <c>ExpireAt</c> at <c>Timestamp + retention</c>;
    /// <c>ExpirationCleanup</c> deletes rows past <c>ExpireAt</c>. Same lossy, bounded stance as
    /// <c>AdapterCallLog</c> — diagnostics, not an audit trail.
    /// </summary>
    public TimeSpan EndpointCallLogRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Global <b>count</b> cap for <c>EndpointCallLog</c> rows — keep at most this many rows per endpoint
    /// (method + route template), deleting the oldest beyond the cap. Complements the age cap
    /// (<see cref="EndpointCallLogRetention"/>): a row is removed once it exceeds <b>either</b> limit.
    /// <c>null</c> (default) disables the count cap.
    /// </summary>
    public int? EndpointCallLogRetentionCount { get; set; }

    /// <summary>
    /// Opt-in logical application name for multi-application observability on a shared database. When set,
    /// this process registers/heartbeats an instance (server processes stamp their <c>Server</c> row;
    /// non-server processes write an <c>ApplicationInstance</c> row), stamps provenance on jobs it publishes
    /// and adapter/endpoint/webhook rows it produces, and appears in the dashboard Applications view.
    /// <c>null</c> (default) ⇒ the feature is entirely off and behavior is byte-for-byte unchanged. Same name
    /// across processes ⇒ same application (stats/instances group by it); different apps ⇒ different names.
    /// </summary>
    public string? ApplicationName { get; set; }

    /// <summary>Opt-in self-reported build/assembly version, stamped on this process's instance row. Per-instance — replicas may report different values mid rolling-deploy. Ignored when <see cref="ApplicationName"/> is null.</summary>
    public string? ApplicationVersion { get; set; }

    /// <summary>Opt-in self-reported environment (prod/staging/…), stamped on this process's instance row. Ignored when <see cref="ApplicationName"/> is null.</summary>
    public string? ApplicationEnvironment { get; set; }

    /// <summary>
    /// Heartbeat cadence for the NON-server application heartbeat host (the loop that refreshes a
    /// publisher/API/dashboard-only process's <c>ApplicationInstance.LastHeartbeatAt</c> + CPU/RAM). Server
    /// processes ride the existing <c>Heartbeat</c> server task instead. Default 15s — non-server liveness is
    /// less time-critical than the 3s server heartbeat. Only runs when <see cref="ApplicationName"/> is set.
    /// </summary>
    public TimeSpan ApplicationHeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Grace window before a non-heartbeating <c>ApplicationInstance</c> row is swept by
    /// <c>ExpirationCleanup</c> (its process died without a graceful deregister). Must stay comfortably
    /// larger than <see cref="ApplicationHeartbeatInterval"/> so a merely-slow instance isn't reaped.
    /// Default 2 min (8× the default heartbeat).
    /// </summary>
    public TimeSpan ApplicationInstanceStaleGrace { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Global default retention (age) for <c>ApplicationInstanceLog</c> lifecycle rows. <c>ExpirationCleanup</c> deletes rows past <c>ExpireAt</c> (stamped <c>Timestamp + retention</c>). Default 7 days.</summary>
    public TimeSpan ApplicationInstanceLogRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Global <b>count</b> cap for <c>ApplicationInstanceLog</c> rows (keep newest N per instance, by <c>Timestamp</c>). Complements the age cap — a row is removed once it exceeds <b>either</b>. <c>null</c> (default) disables the count cap.</summary>
    public int? ApplicationInstanceLogRetentionCount { get; set; }

    /// <summary>
    /// How long hourly-bucketed <c>Statistic</c> rows (keys ending in <c>:yyyy-MM-dd-HH</c> — the job/saga
    /// dashboard history and the adapter/endpoint performance-chart series) are retained before
    /// <c>ExpirationCleanup</c>'s generic hourly sweep prunes them. Raise it to keep deeper chart history
    /// (e.g. to match a longer <c>*CallLogRetention</c>); the buckets are small. Default 7 days.
    /// </summary>
    public TimeSpan HourlyStatisticsRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Bounded in-memory buffer capacity for the outbound adapter and inbound endpoint call-log recorders
    /// (each owns its own channel of this size). Records are enqueued non-blocking; once the buffer is full
    /// further records are dropped (counted, never blocking or failing a call — recording is lossy by
    /// design). Raise for bursty, high-volume observability; lower to cap memory. Default 10,000.
    /// </summary>
    public int CallLogBufferCapacity { get; set; } = 10_000;

    /// <summary>
    /// Max call-log records the adapter/endpoint flushers fold into a single DI scope + <c>SaveChanges</c>
    /// batch when draining their buffer. Larger batches amortise the round-trip; smaller batches bound the
    /// work per transaction. Does not affect the shutdown-drain budget. Default 500.
    /// </summary>
    public int CallLogFlushBatchSize { get; set; } = 500;

    /// <summary>
    /// How far past its <c>NextAttemptAt</c> a <c>Pending</c> <c>WebhookDelivery</c> must be before the
    /// stuck-delivery sweep (part of <c>StaleJobRecovery</c>) re-enqueues an executor job for it. A row
    /// only reaches this state when the executor's outcome commit faulted after the attempt claim — the
    /// scheduled retry job was staged in the same failed transaction, so nothing else will ever revisit
    /// the row. Must stay well above the worst-case scheduled-activation + worker-pickup delay: a sweep
    /// that fires while the delivery's real executor job is merely delayed enqueues a duplicate, which the
    /// attempt claim serialises but which still costs an extra (at-least-once) attempt.
    /// </summary>
    public TimeSpan WebhookStuckDeliveryGrace { get; set; } = TimeSpan.FromMinutes(10);
}
