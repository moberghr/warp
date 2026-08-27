namespace Warp.Core.Timeout;

/// <summary>
/// Caps how long a job's handler may run. Declare on the request/job/message type, on a job/message
/// handler class, or on both — the handler wins (§8.8). <see cref="TimeoutScope.Total"/> is the
/// exception: its deadline is wall-clock from enqueue and must be stamped at publish, so it stays
/// contract-only, and a handler <c>[Timeout]</c> under a Total-scoped global default is inert.
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
