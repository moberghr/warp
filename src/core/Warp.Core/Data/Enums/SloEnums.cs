namespace Warp.Core.Enums;

/// <summary>
/// The kind of objective an <c>SloDefinition</c> measures (§8.31). Each maps to an existing durable aggregate
/// namespace the <c>SloEvaluator</c> reads (no new per-objective storage): success-rate/execution-latency read
/// <c>jobstat</c>, wait-latency reads <c>qwait</c>, depth reads <c>qbacklog</c>, deadline-attainment reads
/// <c>deadline</c>. Values start at 1 (§8.11).
/// </summary>
public enum SloKind
{
    /// <summary>Fraction of terminations that succeeded (retried-then-succeeded counts as success). Target is a ratio, e.g. 0.995.</summary>
    SuccessRate = 1,

    /// <summary>Time-in-queue latency at <c>Percentile</c> must stay under <c>TargetValue</c> ms.</summary>
    QueueWaitLatency = 2,

    /// <summary>Handler execution latency at <c>Percentile</c> must stay under <c>TargetValue</c> ms.</summary>
    ExecutionLatency = 3,

    /// <summary>Queue backlog depth must stay under <c>TargetValue</c> jobs. Breach emits <c>BacklogBreached</c>.</summary>
    BacklogDepth = 4,

    /// <summary>Fraction of <c>Total</c>-scope jobs that met their deadline (§8.7). Target is a ratio, e.g. 0.99.</summary>
    DeadlineAttainment = 5,

    /// <summary>
    /// Good-event ratio over a custom application metric: events whose <c>RatioTag</c> matches <c>GoodValues</c>
    /// divided by good + bad (<c>BadValues</c>). Target is a ratio in (0, 1). With <c>BaselineDays</c> the target
    /// is instead the maximum drop in ratio points against the same window that many days earlier.
    /// </summary>
    MetricRatio = 6,

    /// <summary>Matching events of a custom metric in the window must not exceed <c>TargetValue</c> (0 allowed, e.g. "5xx = 0").</summary>
    MetricThreshold = 7,

    /// <summary>Matching events of a custom metric must stay at or above <c>TargetValue</c> times the same window <c>BaselineDays</c> earlier.</summary>
    MetricVolumeFloor = 8,

    /// <summary>Primary minus secondary event count (<c>SecondaryMetric</c>) must not exceed <c>TargetValue</c> (e.g. started minus finished).</summary>
    MetricGap = 9,

    /// <summary>
    /// Latency over a custom histogram: at least <c>Percentile</c>% of matching events must land in buckets whose upper
    /// bound is at most <c>TargetValue</c> (in the instrument's unit), i.e. the bucket-resolution p<c>Percentile</c> stays
    /// at or under the threshold. Evaluated as a good-event ratio, so the allowed slow fraction is
    /// <c>1 − Percentile/100</c>. Histograms only.
    /// </summary>
    MetricLatency = 10,

    /// <summary>
    /// A custom gauge's per-bucket aggregate (<c>GaugeAggregate</c>: average, min or max) must stay at most / at least
    /// (<c>Comparison</c>) <c>TargetValue</c> in at least <c>Percentile</c>% of the window's observed time. The bad
    /// fraction is the time covered by buckets in violation over the time covered by buckets with samples, fed to the
    /// rate budget with an allowed bad fraction of <c>1 − Percentile/100</c>. Gauges only.
    /// </summary>
    MetricGaugeThreshold = 11,

    /// <summary>
    /// Webhook delivery latency per endpoint (8.1): at least <c>Percentile</c>% of the deliveries created in the window
    /// must be delivered within <c>TargetValue</c> minutes of being sent. <c>Dimension</c> is the delivery group
    /// (<c>GroupName</c>) or <c>*</c> for every delivery. A delivery still undelivered past the threshold, exhausted,
    /// parked or delivered late is a bad event; cancelled deliveries and those younger than the threshold are not
    /// counted. Read from the <c>WebhookDelivery</c> rows (no aggregate), so the window is bounded by the delivery retention.
    /// </summary>
    WebhookDeliveryLatency = 12,
}

/// <summary>Which side of <c>TargetValue</c> a <see cref="SloKind.MetricGaugeThreshold"/> objective must stay on. Values start at 1 (§8.11).</summary>
public enum SloComparison
{
    /// <summary>The aggregate must be at most the target (a bucket above it is a violation).</summary>
    AtMost = 1,

    /// <summary>The aggregate must be at least the target (a bucket below it is a violation).</summary>
    AtLeast = 2,
}

/// <summary>
/// The per-bucket gauge value a <see cref="SloKind.MetricGaugeThreshold"/> objective compares. A bucket holds every sample
/// of the matching series from every process (count, sum, min, max), so <see cref="Average"/> is the sample-weighted
/// mean across processes and series, never their total. Values start at 1 (§8.11).
/// </summary>
public enum SloGaugeAggregate
{
    /// <summary>Sum ÷ count of every sample in the bucket.</summary>
    Average = 1,

    /// <summary>The smallest sample in the bucket.</summary>
    Min = 2,

    /// <summary>The largest sample in the bucket.</summary>
    Max = 3,
}

/// <summary>
/// The current health of an <c>SloEvaluation</c> against its objective (§8.31). Values start at 1 (§8.11).
/// The evaluator transitions between these each tick and fires a notifier event only on the healthy→breaching
/// edge; <see cref="Acknowledged"/> suppresses further alerts until the ack window elapses.
/// </summary>
public enum SloState
{
    /// <summary>Within budget — attainment meets or exceeds the target.</summary>
    Healthy = 1,

    /// <summary>Budget burning (slow burn) — attainment slipping but not yet breaching.</summary>
    Warning = 2,

    /// <summary>Budget exhausted / fast burn — objective is being missed.</summary>
    Breaching = 3,

    /// <summary>Breaching but silenced by an operator until <c>AcknowledgedUntil</c>.</summary>
    Acknowledged = 4,

    /// <summary>
    /// No observations matched the objective's dimension in the window — the evaluator ran but found nothing to
    /// measure. Distinct from <see cref="Healthy"/> so a typo'd dimension (or a job type that never ran) reads as
    /// "no data" instead of a false green; never alerts.
    /// </summary>
    NoData = 5,
}
