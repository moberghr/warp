namespace Warp.Core.Enums;

/// <summary>
/// Lifecycle events recorded on <c>ApplicationInstanceLog</c> for a Warp process (server or not).
/// Lifecycle is an application-instance concern, not a server one — this covers every instance,
/// unlike <c>ServerLog</c> which is server-task execution history. Values start at 1 (§8.11).
/// </summary>
public enum ApplicationInstanceEventType
{
    Registered = 1,
    HeartbeatLost = 2,
    Recovered = 3,
    Stopped = 4,
    StaleSwept = 5,
}
