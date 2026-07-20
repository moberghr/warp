namespace Warp.Core.Enums;

/// <summary>
/// Final outcome of an outbound adapter call. <see cref="CircuitOpen"/> is reserved for the
/// shared circuit-breaker fast-follow and is not produced by v1.
/// </summary>
public enum AdapterCallOutcome
{
    Success = 1,
    Failed = 2,
    Throttled = 3,
    CircuitOpen = 4,
}
