namespace Warp.Core.Enums;

/// <summary>
/// Lifecycle of a <c>WebhookDelivery</c>. The delivery — not the executor job — is the state machine:
/// executor jobs always complete, and failure lives here. <c>Pending</c> has a live executor job (first
/// attempt or a scheduled retry); <c>Delivered</c>, <c>Exhausted</c> and <c>Cancelled</c> are settled
/// terminal states. <c>Parked</c> is waiting for its endpoint (group) to recover: it has no live job, and the
/// endpoint monitor releases it back to <c>Pending</c> once the group succeeds again (8.1).
/// </summary>
public enum WebhookDeliveryStatus
{
    Pending = 1,
    Delivered = 2,
    Exhausted = 3,

    /// <summary>
    /// The delivery ran out of retries while its endpoint was <c>Failing</c>: it waits, with no live job, for the
    /// endpoint to recover and is then released to <c>Pending</c> with a fresh attempt budget. Only deliveries
    /// sent with endpoint health tracking on park.
    /// </summary>
    Parked = 4,

    /// <summary>An operator cancelled the delivery while it was <c>Pending</c> or <c>Parked</c>. Settled; never attempted again unless redelivered.</summary>
    Cancelled = 5,
}
