namespace Warp.Core.Concurrency;

/// <summary>
/// Caps jobs sharing a key to N concurrent executions. <b>Declare on the request/job type, not the
/// handler</b> — it is read from the request type at publish; on a handler it is a silent no-op and
/// <c>AddWarp</c> rejects it at startup (#242).
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
