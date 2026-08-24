namespace Warp.Core.RateLimit;

/// <summary>
/// Throttles jobs sharing a key to N starts per window. Declare on the request/job/message type
/// (resolved at publish and copied to every routed handler's child) OR on a job/message handler class
/// (resolved at first execution). Declaring it on both the contract and its handler is a startup
/// error; on stream or in-memory request handlers it is rejected at startup (#242).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RateLimitAttribute : Attribute
{
    /// <summary>
    /// Upper bound on the window length, in seconds. Caps unrealistic inputs (e.g. int.MaxValue)
    /// that would overflow <see cref="DateTime"/> arithmetic in the pipeline. Seven days is well
    /// beyond any practical rate-limit window.
    /// </summary>
    public const int MaxWindowSeconds = 7 * 24 * 60 * 60;

    public RateLimitAttribute(string key, int count, int perSeconds)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Count must be at least 1.");
        }

        if (perSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(perSeconds), perSeconds, "PerSeconds must be at least 1.");
        }

        if (perSeconds > MaxWindowSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(perSeconds), perSeconds, $"PerSeconds must be at most {MaxWindowSeconds} (7 days).");
        }

        Key = key;
        Count = count;
        PerSeconds = perSeconds;
    }

    public string Key { get; }

    public int Count { get; }

    public int PerSeconds { get; }

    public RateLimitMode Mode { get; init; } = RateLimitMode.Skip;

    public RateLimitStyle Style { get; init; } = RateLimitStyle.Fixed;
}
