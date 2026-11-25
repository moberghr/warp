using Microsoft.Extensions.Logging;

namespace Warp.Core.Webhooks;

// Logs, once per process, that PublicAddressesOnly replaced a host-configured primary handler of the
// warp-webhooks client. The handler is rebuilt on every factory rotation, so without the flag the warning
// would repeat every couple of minutes.
internal sealed class WebhookHandlerReplacementWarning
{
    private readonly ILogger<WebhookHandlerReplacementWarning> _logger;
    private int _reported;

    public WebhookHandlerReplacementWarning(ILogger<WebhookHandlerReplacementWarning> logger) => _logger = logger;

    public void Report(string handlerType)
    {
        if (Interlocked.Exchange(ref _reported, 1) == 1)
        {
            return;
        }

        _logger.LogWarning(
            "PublicAddressesOnly replaced the primary handler of HttpClient \"{ClientName}\" ({HandlerType}) with a guarded SocketsHttpHandler. "
            + "That handler's settings (proxy, client certificates, redirects, timeouts) are not carried over. "
            + "Configure the client with UseSocketsHttpHandler(...) to keep them.",
            WebhookConstants.AdapterName,
            handlerType);
    }
}
