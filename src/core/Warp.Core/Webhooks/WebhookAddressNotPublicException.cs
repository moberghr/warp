namespace Warp.Core.Webhooks;

/// <summary>
/// Thrown from the <c>warp-webhooks</c> connection when <see cref="WarpWebhookOptions.PublicAddressesOnly"/>
/// is on and the destination host resolves to no public address. The attempt is recorded as a failed
/// attempt and retried per the delivery's schedule. The message names the host, never the resolved
/// addresses. <see cref="System.Net.Http.SocketsHttpHandler"/> wraps it in an
/// <see cref="System.Net.Http.HttpRequestException"/>, so look for it in the inner-exception chain.
/// </summary>
public sealed class WebhookAddressNotPublicException : Exception
{
    public WebhookAddressNotPublicException()
    {
    }

    public WebhookAddressNotPublicException(string message)
        : base(message)
    {
    }

    public WebhookAddressNotPublicException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
