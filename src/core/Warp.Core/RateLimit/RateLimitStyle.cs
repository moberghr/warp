namespace Warp.Core.RateLimit;

public enum RateLimitStyle
{
    Fixed = 1,
    Sliding = 2,

    /// <summary>
    /// Token bucket — <b>paces</b> starts at a steady rate rather than capping a count per window.
    /// Tokens refill continuously at <c>count / window</c> per second, up to a burst capacity of
    /// <c>count</c>; each start consumes one token. When the bucket is empty a <c>Wait</c>-mode job is
    /// rescheduled to when the next token refills (not to a window boundary).
    /// <para>
    /// The reschedule target is exact, but <c>Wait</c> reschedules land in <c>State.Scheduled</c> and ride
    /// <c>ScheduledJobActivation</c> (§8.8), which is <b>not</b> accelerated by DB push. So the effective
    /// release cadence is floored at <c>ScheduledActivationInterval</c> (default 10s): a bucket whose refill
    /// interval is shorter than that tick (e.g. a high <c>count</c>/short window) releases in per-tick
    /// clumps rather than a truly smooth trickle. It never over-admits (burst stays ≤ <c>count</c>); for
    /// sub-second pacing precision lower <c>ScheduledActivationInterval</c>.
    /// </para>
    /// </summary>
    TokenBucket = 3,
}
