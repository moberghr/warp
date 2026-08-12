namespace Warp.Core.Enums;

/// <summary>
/// Why a job reached the state it did, when the cause is known. Stamped on <c>JobOutcome</c> by the pipeline
/// behaviour that made the decision — the only component that knows the reason — and read by the worker to
/// compose the <c>stats:{state}-{reason}</c> breakdown key.
/// </summary>
/// <remarks>
/// <para>
/// <b>This must stay a closed enum.</b> <c>JobOutcome</c> is public API and user-written pipeline behaviours
/// set outcomes, so a free-form string here would let a caller mint an unbounded number of
/// <c>Statistic</c> rows (a reason per tenant, per key, per URL). The bounded set is what keeps the metric
/// family a fixed ~14 keys regardless of traffic.
/// </para>
/// <para>
/// Values start at 1 per §8.11 so <c>default(OutcomeReason)</c> is never a valid reason.
/// </para>
/// </remarks>
public enum OutcomeReason
{
    /// <summary>Retry backoff — the attempt failed and another is scheduled.</summary>
    Retry = 1,

    /// <summary>The retry budget ran out; this failure is terminal.</summary>
    RetryExhausted = 2,

    /// <summary>Mutex or semaphore. Wait mode requeues, Skip mode deletes.</summary>
    Concurrency = 3,

    /// <summary>Rate limit — throttled, skipped, or bounced off lock contention.</summary>
    RateLimit = 4,

    /// <summary>Timeout in Delete mode.</summary>
    Timeout = 5,

    /// <summary>Saga — busy, version/unique conflict, missing correlation, or a moot timeout.</summary>
    Saga = 6,

    /// <summary>Operator action from the dashboard. Not set via <c>JobOutcome</c>.</summary>
    Manual = 7,

    /// <summary>Crash recovery re-queued work whose worker stopped responding. Not set via <c>JobOutcome</c>.</summary>
    Recovery = 8,

    /// <summary>Circuit breaker — the group is open, so the attempt was rescheduled past the reset window.</summary>
    CircuitBreaker = 9,
}
