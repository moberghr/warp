namespace Warp.Core.CircuitBreaker;

/// <summary>
/// Declares circuit-breaker policy. Applies to a job/message type, to a job/message handler class, or to
/// both — the handler wins. Unset values (0) fall back to the global <see cref="CircuitBreakerOptions"/>.
/// The one family resolved per attempt and never stamped: threshold and duration describe a shared
/// dependency group, so two jobs in one group must not disagree about when the circuit opens.
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
