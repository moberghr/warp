namespace Warp.Core.Concurrency;

/// <summary>
/// Serializes jobs sharing a key to a single concurrent execution. <b>Declare on the request/job type,
/// not the handler</b> — it is read from the request type at publish; on a handler it is a silent no-op
/// and <c>AddWarp</c> rejects it at startup (#242).
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
