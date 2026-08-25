namespace Warp.Core.Concurrency;

/// <summary>
/// Caps jobs sharing a key to N concurrent executions. Declare on the request/job/message type, on a
/// job/message handler class, or on both — the handler wins, including over a contract <c>[Mutex]</c>
/// (§8.8). Rejected at build time on stream and in-memory request handlers (#242).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SemaphoreAttribute : Attribute
{
    public SemaphoreAttribute(string key, int limit)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        Key = key;
        Limit = limit;
    }

    public string Key { get; }

    public int Limit { get; }

    public ConcurrencyMode Mode { get; init; } = ConcurrencyMode.Wait;
}
