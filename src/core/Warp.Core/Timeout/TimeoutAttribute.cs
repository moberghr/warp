namespace Warp.Core.Timeout;

/// <summary>
/// Caps how long a job's handler may run. Declare on the request/job/message type OR — for
/// <see cref="TimeoutScope.PerAttempt"/> only — on a job/message handler class (resolved at first
/// execution). <see cref="TimeoutScope.Total"/> stays contract-only: its deadline is a wall-clock
/// budget measured from enqueue and must be stamped at publish, so <c>Scope = Total</c> on a handler
/// (or any handler <c>[Timeout]</c> under a Total-scoped global default) is a startup error, as is
/// declaring Timeout on both the contract and its handler.
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
