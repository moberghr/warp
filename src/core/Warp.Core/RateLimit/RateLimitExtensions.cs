using Warp.Core.Helper;

namespace Warp.Core.RateLimit;

public static class RateLimitExtensions
{
    public static JobParameters WithRateLimit(
        this JobParameters parameters,
        string key,
        int count,
        TimeSpan window,
        RateLimitMode mode = RateLimitMode.Skip,
        RateLimitStyle style = RateLimitStyle.Fixed)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);

        // The same cap as [RateLimit(perSeconds)]. ExpirationCleanup relies on it: a bucket idle for longer
        // than the longest possible window is deleted as equivalent to a fresh key.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(window, TimeSpan.FromSeconds(RateLimitAttribute.MaxWindowSeconds));

        parameters.Configure<IRateLimitMetadata>(x =>
        {
            x.RateLimitKey = key;
            x.RateLimitCount = count;
            x.RateLimitWindowSeconds = (int)Math.Ceiling(window.TotalSeconds);
            x.RateLimitMode = mode;
            x.RateLimitStyle = style;
        });

        return parameters;
    }
}
