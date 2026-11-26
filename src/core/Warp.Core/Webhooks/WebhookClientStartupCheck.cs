using System.Net.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Warp.Core.Webhooks;

// Builds the warp-webhooks handler once the host has STARTED, while the public-address guard is on, so the guard is
// applied (and a replaced host handler reported) before the first delivery attempt rather than during it. Registered
// only on servers that run a worker (they run the attempts). It runs in StartedAsync, after every hosted service has
// started, because a host's own handler factory may depend on something that only exists then (an in-process test
// server, say) and the client factory caches a failed build: building earlier would break every later attempt.
// A failure here is logged and never stops the host.
internal sealed class WebhookClientStartupCheck : IHostedLifecycleService
{
    private readonly IHttpMessageHandlerFactory _handlerFactory;
    private readonly WebhookAddressPolicy _policy;
    private readonly ILogger<WebhookClientStartupCheck> _logger;

    public WebhookClientStartupCheck(
        IHttpMessageHandlerFactory handlerFactory,
        IOptions<WebhookAddressPolicy> policy,
        ILogger<WebhookClientStartupCheck> logger)
    {
        _handlerFactory = handlerFactory;
        _policy = policy.Value;
        _logger = logger;
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        if (!_policy.PublicAddressesOnly)
        {
            return Task.CompletedTask;
        }

        try
        {
            // The factory owns the pooled handler; it is created here only for its side effects, never disposed.
            _handlerFactory.CreateHandler(WebhookConstants.AdapterName);
        }
#pragma warning disable CA1031 // a startup check must never stop the host.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "The warp-webhooks HTTP handler could not be built; webhook attempts will fail until its configuration is fixed.");
        }

        return Task.CompletedTask;
    }

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
