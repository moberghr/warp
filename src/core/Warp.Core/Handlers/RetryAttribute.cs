namespace Warp.Core.Handlers;

/// <summary>
/// Declares retry policy. Can be applied to a job/message type or to a job/message handler class —
/// but not both for the same pair: <c>AddWarp</c> rejects the double declaration at startup, which is
/// what makes the priority unambiguous. Priority: per-enqueue metadata override > the declared
/// attribute (either axis — the other is guaranteed empty) > global RetryOptions.
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
