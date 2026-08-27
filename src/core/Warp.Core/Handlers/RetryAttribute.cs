namespace Warp.Core.Handlers;

/// <summary>
/// Declares retry policy. Applies to a job/message type, to a job/message handler class, or to both.
/// Priority: per-enqueue metadata > handler attribute > contract attribute > global RetryOptions (§8.8).
/// The global default is never stamped, so an absent budget on the row means RetryOptions applies.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RetryAttribute : Attribute
{
    public RetryAttribute(int maxRetries)
    {
        MaxRetries = maxRetries;
    }

    public int MaxRetries { get; }

    public int[]? Delays { get; set; }
}
