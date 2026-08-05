namespace Warp.Core.Metrics;

/// <summary>
/// The backend-neutral vocabulary of Warp's own logical metrics — the <see cref="MetricRef.Name"/> values and tag
/// keys every <see cref="IMetricSource"/> speaks. It mirrors the OTel meter model (<c>WarpTelemetry</c>): a call
/// <em>count</em> and its <em>duration</em> are distinct logical metrics, and <c>outcome</c>/<c>route</c>/
/// <c>queue</c>/<c>type</c>/… are <em>tags</em> — never positional key tokens. Each backend owns its own translation
/// FROM these names: <c>LocalMetricSource</c> → the colon <c>Statistic</c>/<c>Counter</c> keys (§8.6/§8.19), a
/// Prometheus backend → an OTel metric name + label matchers. This type is only the shared name/tag vocabulary, not
/// a mapping table — so a new backend is a genuine drop-in, no persisted-format or lookup-table coupling.
/// </summary>
public static class WarpMetricCatalog
{
    /// <summary>Logical metric names. A <see cref="MetricRef.Name"/> is always one of these.</summary>
    public static class Names
    {
        // Dashboard lifetime summary counters (global, tagless).
        public const string LifecycleSucceeded = "lifecycle.succeeded";
        public const string LifecycleFailed = "lifecycle.failed";
        public const string LifecycleDeleted = "lifecycle.deleted";

        // Lossy-recording drop counters, tagged by pipeline (adapter | endpoint | client).
        public const string RecordsDropped = "records.dropped";

        // Job execution (mirrors warp.job.execution.*): count + latency, by type/handler/outcome.
        public const string JobExecution = "job.execution";
        public const string JobExecutionDuration = "job.execution.duration";

        // Queue metrics (mirrors warp.job.queue.*).
        public const string QueueWait = "job.queue.wait";
        public const string QueueDepth = "job.queue.depth";
        public const string QueueOldestAge = "job.queue.oldest_age";

        // Deadline attainment (mirrors warp.job.deadline.*): total denominator + miss numerator.
        public const string Deadline = "job.deadline";
        public const string DeadlineMiss = "job.deadline.miss";

        // Outbound adapters (mirrors warp.adapter.*).
        public const string AdapterCalls = "adapter.calls";
        public const string AdapterDuration = "adapter.duration";

        // Inbound endpoints (mirrors warp.endpoint.*).
        public const string EndpointCalls = "endpoint.calls";
        public const string EndpointDuration = "endpoint.duration";

        // Client (browser) observability (mirrors warp.client.*).
        public const string ClientEvents = "client.events";
        public const string ClientEventsNamed = "client.events.named";
        public const string ClientVitals = "client.vitals";

        // Error grouping occurrences, tagged by fingerprint.
        public const string ErrorGroupOccurrences = "errorgroup.occurrences";

        // SLO evaluation state, tagged by objective.
        public const string SloAttainment = "slo.attainment";
        public const string SloBudget = "slo.budget";
    }

    /// <summary>Tag keys a <see cref="MetricRef"/> may carry, and a series may be broken down by.</summary>
    public static class Tags
    {
        public const string Application = "application";
        public const string Type = "type";
        public const string Handler = "handler";
        public const string Outcome = "outcome";
        public const string Queue = "queue";
        public const string Adapter = "adapter";
        public const string Operation = "operation";
        public const string Group = "group";
        public const string Route = "route";
        public const string Vital = "vital";
        public const string Name = "name";
        public const string Pipeline = "pipeline";
        public const string Fingerprint = "fingerprint";
        public const string Slo = "slo";
        public const string Kind = "kind";
        public const string Dimension = "dimension";
    }
}
