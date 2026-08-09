using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Warp.Core.Observability;

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
    /// Global default retention for <c>ClientEventLog</c> rows (client/browser observability, §8.27). The
    /// flusher stamps each row's <c>ExpireAt</c> at <c>ReceivedAt + retention</c>; <c>ExpirationCleanup</c>
    /// deletes rows past <c>ExpireAt</c>. Diagnostics, not an audit trail — trend data survives via the
    /// <c>clientevent:</c> Counter fold.
    /// </summary>
    public TimeSpan ClientEventLogRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Global <b>count</b> cap for <c>ClientEventLog</c> rows — keep at most this many rows per application,
    /// deleting the oldest beyond the cap. Complements the age cap (<see cref="ClientEventLogRetention"/>): a
    /// row is removed once it exceeds <b>either</b> limit. Default keeps the browser firehose bounded.
    /// </summary>
    public int? ClientEventLogRetentionCount { get; set; } = 100_000;

    /// <summary>
    /// How often the <c>ErrorGroupAggregator</c> server task drains the <c>ErrorOccurrence</c> inbox into
    /// <c>ErrorGroup</c> issues (§8.29). Set to <c>null</c> to disable error grouping entirely (no aggregator
    /// runs; the inbox is never written). Off the worker hot path (§0.2/§6.1).
    /// </summary>
    public TimeSpan? ErrorGroupingInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Age cap for <c>ErrorGroup</c> issues — removed once <c>LastSeenAt</c> exceeds this (§8.22). Trend aggregates persist.</summary>
    public TimeSpan ErrorGroupRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Optional count cap for <c>ErrorGroup</c> issues (keep the most-recently-seen N). Null ⇒ off. Complements the age cap.</summary>
    public int? ErrorGroupRetentionCount { get; set; }

    /// <summary>
    /// Cardinality guard (§8.29): the max distinct <c>ErrorGroup</c>s per source (per source+application when
    /// app-sliced). Beyond this, new fingerprints collapse into a per-source <c>{other}</c> group — critical for
    /// the client source, fed from the public ingest endpoint (§8.27).
    /// </summary>
    public int MaxDistinctErrorGroups { get; set; } = 2000;

    /// <summary>Store a raw, truncated sample (message + top frames) on each <c>ErrorGroup</c> for debugging (§1.2). Off ⇒ Title (normalized) only.</summary>
    public bool CaptureErrorSamples { get; set; } = true;

    /// <summary>
    /// Namespace prefixes treated as framework/plumbing when picking an error's top "in-app" stack frame (§8.29).
    /// Defaults to <c>ErrorFingerprint.DefaultInAppDenylist</c>; editable so a host's own framework layers can be skipped too.
    /// </summary>
    public IList<string> InAppNamespaceDenylist { get; set; } = [.. Warp.Core.ErrorGrouping.ErrorFingerprint.DefaultInAppDenylist];

    /// <summary>
    /// How often the <c>SloEvaluator</c> server task evaluates SLO objectives against the durable
    /// <c>Statistic</c>/<c>Counter</c> aggregates and upserts their rolling status (§8.30). Set to <c>null</c>
    /// to disable SLO evaluation entirely (no evaluator runs). Objectives also require <c>AddSlo(...)</c> to be
    /// registered. Off the worker hot path (§0.2/§6.1) — a periodic aggregate read, no per-job cost.
    /// </summary>
    public TimeSpan? SloEvaluationInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Where per-job-TYPE / per-HANDLER execution aggregate <b>metrics</b> are written. The OTel
    /// <c>warp.job.execution.*</c> meters (§2.15) emit <b>unconditionally</b> regardless of this setting
    /// (null-listener ⇒ zero cost); this knob only gates the write-optimised <c>jobstat</c> <c>Counter</c>
    /// rows that back the dashboard's per-type/per-handler aggregates.
    /// <list type="bullet">
    ///   <item><see cref="RecordingSink.Database"/> (default) / <see cref="RecordingSink.Both"/> — write the
    ///     <c>jobstat</c> Counter rows at finalization, exactly as before (byte-for-byte current behavior).</item>
    ///   <item><see cref="RecordingSink.Otel"/> — SKIP the <c>jobstat</c> Counter writes on the worker
    ///     finalization path (an OTel-only user reconstructs count / error-rate / latency / per-app from the
    ///     always-on meters instead). The app-agnostic lifecycle <c>stats:*</c> counters are unaffected.</item>
    /// </list>
    /// </summary>
    public RecordingSink JobMetricsSink { get; set; } = RecordingSink.Database;

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
    /// Bucket width (minutes) of the finest metrics-retention tier (§8.30). Time-series <c>Statistic</c> keys
    /// (jobstat / qwait hist + pcth; other families emit hourly and are rolled) are emitted at this resolution
    /// (marked <c>m5</c>) and rolled up to hourly then daily by <c>StatisticRollup</c>. Larger values coarsen the
    /// fine tier; the tier is always emitted and downsampled — there is no separate "off". <b>Must be &gt;= 1</b>
    /// (it is a divisor when bucketing on the hot path; validated at server startup). Default 5.
    /// </summary>
    public int FineResolutionMinutes { get; set; } = 5;

    /// <summary>
    /// How long the fine (<see cref="FineResolutionMinutes"/>-minute) tier is kept before <c>StatisticRollup</c>
    /// sums each complete bucket into its hourly (<c>h1</c>) parent and deletes the fine rows (§8.30). Only
    /// buckets strictly older than this are rolled, so an in-progress window is never half-rolled. Default 6 hours.
    /// </summary>
    public TimeSpan FineResolutionRetention { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// How long hourly-bucketed <c>Statistic</c> rows are kept before <c>StatisticRollup</c> sums each into its
    /// daily (<c>d1</c>) parent and deletes the hourly rows (§8.30). (Pre-3.10 this was a delete-only prune; it
    /// is now the hourly→daily rollup age.) Legacy unmarked <c>:yyyy-MM-dd-HH</c> keys migrate under the same age.
    /// Default 7 days.
    /// </summary>
    public TimeSpan HourlyStatisticsRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// How long the daily (<c>d1</c>) tier is kept before <c>StatisticRollup</c> deletes it — the end of the
    /// retention chain (§8.30). Set to <c>null</c> to keep daily buckets forever (coarse, cheap long history).
    /// Default 90 days.
    /// </summary>
    public TimeSpan? DailyStatisticsRetention { get; set; } = TimeSpan.FromDays(90);

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
