namespace Warp.Core.Adapters;

/// <summary>
/// Thrown when a shared-rate-limited adapter cannot acquire a token: immediately under
/// <c>AdapterRateLimitOverflow.FailFast</c>, or after the configured <c>maxWait</c> elapses under
/// <c>AdapterRateLimitOverflow.Wait</c>. The call is recorded with a <c>Throttled</c> outcome.
/// <para>
/// <b>Through Refit this exception arrives wrapped.</b> Refit wraps every exception escaping the
/// <see cref="HttpClient"/> pipeline in <c>ApiRequestException</c>, so <c>catch (AdapterRateLimitedException)</c>
/// never fires for a Refit-bound adapter. Use <see cref="AdapterRateLimitExtensions.IsAdapterRateLimited"/>
/// (which walks the inner-exception chain), or switch the adapter to
/// <c>AdapterRateLimitOverflow.Respond429</c> so the refusal arrives as an ordinary <c>429</c> response.
/// </para>
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

    public AdapterRateLimitedException(string message, TimeSpan retryAfter)
        : base(message)
        => RetryAfter = retryAfter;

    /// <summary>
    /// How long the limiter computed the caller should wait before the next token frees up, when it is
    /// known — the remainder of the current window at the moment of refusal. Null when the refusal
    /// carries no timing (an exception constructed outside the limiter). The limiter already knows this
    /// value, so a caller need not retry blind.
    /// </summary>
    public TimeSpan? RetryAfter { get; }
}
