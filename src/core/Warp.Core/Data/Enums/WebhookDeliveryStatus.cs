namespace Warp.Core.Enums;

/// <summary>
/// Lifecycle of a <c>WebhookDelivery</c>. The delivery — not the executor job — is the state machine:
/// executor jobs always complete, and failure lives here. <c>Pending</c> has a live executor job (first
/// attempt or a scheduled retry); <c>Delivered</c> and <c>Exhausted</c> are settled terminal states.
/// </summary>
public enum WebhookDeliveryStatus
{
    Pending = 1,
    Delivered = 2,
    Exhausted = 3,
}
