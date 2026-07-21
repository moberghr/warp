namespace Warp.Core.RateLimit;

public enum RateLimitStyle
{
    Fixed = 1,
    Sliding = 2,

    /// <summary>
    /// Token bucket — <b>paces</b> starts at a steady rate rather than capping a count per window.
    /// Tokens refill continuously at <c>count / window</c> per second, up to a burst capacity of
    /// <c>count</c>; each start consumes one token. When the bucket is empty a <c>Wait</c>-mode job is
    /// rescheduled to when the next token refills (not to a window boundary), so bursts trickle out at
    /// the refill rate instead of releasing all at once.
    /// </summary>
    TokenBucket = 3,
}
