namespace Warp.Core.Enums;

/// <summary>
/// Health of a webhook endpoint — a delivery <c>GroupName</c> sent with endpoint health tracking (8.1). Values
/// start at 1 (§8.11).
/// </summary>
public enum WebhookEndpointState
{
    /// <summary>The endpoint's deliveries are going through; deliveries that run out of retries are <c>Exhausted</c>.</summary>
    Healthy = 1,

    /// <summary>
    /// A delivery to the endpoint exhausted its retries. New deliveries are still sent and retried; one that runs out
    /// of retries is <c>Parked</c> until the endpoint recovers (a probe or any delivery succeeding).
    /// </summary>
    Failing = 2,
}
