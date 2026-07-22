namespace Warp.Core.Timeout;

/// <summary>
/// Caps how long a job's handler may run. <b>Declare on the request/job type, not the handler</b> — it is
/// read from the request type at publish; on a handler it is a silent no-op and <c>AddWarp</c> rejects it
/// at startup (#242).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class TimeoutAttribute : Attribute
{
    public TimeoutAttribute(int seconds)
    {
        if (seconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "Timeout must be positive.");
        }

        Seconds = seconds;
    }

    public int Seconds { get; }

    public TimeoutMode Mode { get; init; } = TimeoutMode.Delete;

    public TimeoutScope Scope { get; init; } = TimeoutScope.PerAttempt;
}
