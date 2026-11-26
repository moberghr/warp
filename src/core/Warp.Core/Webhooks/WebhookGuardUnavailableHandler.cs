using System.Net.Http;

namespace Warp.Core.Webhooks;

// Stands in front of a host-configured primary handler the public-address guard cannot attach to (anything but a
// SocketsHttpHandler, e.g. an HttpClientHandler carrying a proxy or client certificates). The host's handler is kept
// (never replaced: its proxy and certificates must not be dropped silently), and every attempt fails closed with an
// error naming the fix, so no request ever leaves unguarded.
internal sealed class WebhookGuardUnavailableHandler : DelegatingHandler
{
    private readonly string _message;

    public WebhookGuardUnavailableHandler(HttpMessageHandler hostHandler)
        : base(hostHandler)
        => _message = Describe(hostHandler.GetType().FullName ?? hostHandler.GetType().Name);

    internal static string Describe(string handlerType)
        => $"PublicAddressesOnly is on, but the primary handler of HttpClient \"{WebhookConstants.AdapterName}\" is {handlerType}, which cannot "
            + "enforce it, so every webhook attempt fails. Configure the client with UseSocketsHttpHandler(...) (set the proxy or "
            + "client certificates there), or set AddWebhooks(w => w.PublicAddressesOnly = false).";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => throw new InvalidOperationException(_message);

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        => throw new InvalidOperationException(_message);
}
