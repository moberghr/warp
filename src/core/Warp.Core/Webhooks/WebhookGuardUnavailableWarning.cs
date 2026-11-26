using Microsoft.Extensions.Logging;

namespace Warp.Core.Webhooks;

// Logs, once per process, that PublicAddressesOnly is on but the warp-webhooks client's primary handler is not a
// SocketsHttpHandler, so the guard cannot be applied and every attempt fails closed. The handler is rebuilt on every
// factory rotation, so without the flag the warning would repeat every couple of minutes.
internal sealed class WebhookGuardUnavailableWarning
{
    private readonly ILogger<WebhookGuardUnavailableWarning> _logger;
    private int _reported;

    public WebhookGuardUnavailableWarning(ILogger<WebhookGuardUnavailableWarning> logger) => _logger = logger;

    public void Report(string handlerType)
    {
        if (Interlocked.Exchange(ref _reported, 1) == 1)
        {
            return;
        }

        _logger.LogWarning("{Message}", WebhookGuardUnavailableHandler.Describe(handlerType));
    }
}
