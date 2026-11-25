using System.Net.Http;
using Microsoft.Extensions.Hosting;

namespace Warp.Core.Webhooks;

// Builds the warp-webhooks handler once at host start when PublicAddressesOnly is on, so the guard is
// applied (and a replaced host handler reported) at startup rather than on the first delivery attempt.
internal sealed class WebhookClientStartupCheck : IHostedService
{
    private readonly IHttpMessageHandlerFactory _handlerFactory;

    public WebhookClientStartupCheck(IHttpMessageHandlerFactory handlerFactory) => _handlerFactory = handlerFactory;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The factory owns the pooled handler; it is created here only for its side effects, never disposed.
        _handlerFactory.CreateHandler(WebhookConstants.AdapterName);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
