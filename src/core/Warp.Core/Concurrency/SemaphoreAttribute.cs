namespace Warp.Core.Concurrency;

/// <summary>
/// Caps jobs sharing a key to N concurrent executions. Declare on the request/job/message type
/// (resolved at publish and copied to every routed handler's child) OR on a job/message handler class
/// (resolved at first execution). Declaring the concurrency family — <c>[Mutex]</c> or
/// <c>[Semaphore]</c> — on both the contract and its handler is a startup error; on stream or
/// in-memory request handlers it is rejected at startup (#242).
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
