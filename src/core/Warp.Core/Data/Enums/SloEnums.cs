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
