namespace Warp.Core.Concurrency;

/// <summary>
/// Serializes jobs sharing a key to a single concurrent execution. Declare on the request/job/message
/// type, on a job/message handler class, or on both — the handler wins, including over a contract
/// <c>[Semaphore]</c> (§8.8). Rejected at build time on stream and in-memory request handlers (#242).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class MutexAttribute : Attribute
{
    public MutexAttribute(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        Key = key;
    }

    public string Key { get; }

    public ConcurrencyMode Mode { get; init; } = ConcurrencyMode.Skip;
}
