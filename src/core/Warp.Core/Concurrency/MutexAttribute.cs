namespace Warp.Core.Concurrency;

/// <summary>
/// Serializes jobs sharing a key to a single concurrent execution. Declare on the request/job/message
/// type (resolved at publish and copied to every routed handler's child) OR on a job/message handler
/// class (resolved at first execution — the handler is the code touching the resource). Declaring the
/// concurrency family on both the contract and its handler is a startup error; on stream or in-memory
/// request handlers it is rejected at startup (#242 — no execution path can honour it there).
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
