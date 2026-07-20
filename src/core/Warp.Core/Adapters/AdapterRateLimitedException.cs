namespace Warp.Core.Adapters;

/// <summary>
/// Thrown when a shared-rate-limited adapter cannot acquire a token: immediately under
/// <c>AdapterRateLimitOverflow.FailFast</c>, or after the configured <c>maxWait</c> elapses under
/// <c>AdapterRateLimitOverflow.Wait</c>. The call is recorded with a <c>Throttled</c> outcome.
/// </summary>
public sealed class AdapterRateLimitedException : Exception
{
    public AdapterRateLimitedException()
    {
    }

    public AdapterRateLimitedException(string message)
        : base(message)
    {
    }

    public AdapterRateLimitedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
