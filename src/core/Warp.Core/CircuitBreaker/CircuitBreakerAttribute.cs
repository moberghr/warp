namespace Warp.Core.CircuitBreaker;

/// <summary>
/// Declares circuit-breaker policy. Can be applied to a job/message type or to a job/message handler
/// class — but not both for the same pair: <c>AddWarp</c> rejects the double declaration at startup.
/// Unset values (0) fall back to the global <see cref="CircuitBreakerOptions"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class CircuitBreakerAttribute : Attribute
{
    public string? Group { get; set; }

    public int Threshold { get; set; }

    public int DurationSeconds { get; set; }

    public int ResetJitterSeconds { get; set; }

    public int GetThreshold(CircuitBreakerOptions options)
    {
        return Threshold > 0 ? Threshold : options.Threshold;
    }

    public TimeSpan GetDuration(CircuitBreakerOptions options)
    {
        return DurationSeconds > 0 ? TimeSpan.FromSeconds(DurationSeconds) : options.Duration;
    }

    public TimeSpan GetResetJitter(CircuitBreakerOptions options)
    {
        return ResetJitterSeconds > 0 ? TimeSpan.FromSeconds(ResetJitterSeconds) : options.ResetJitter;
    }
}
