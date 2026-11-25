using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;

namespace Warp.Core.Webhooks;

// Registration of the "warp-webhooks" named HttpClient: Core's default primary handler (redirects off) and
// the opt-in public-address guard.
internal static class WebhookHttpClientSetup
{
    // Registered by AddWarp. The default handler is installed by the FIRST builder action (inserted at index
    // 0, whenever this runs), so a primary handler the host configures, before or after AddWarp, always
    // replaces it and keeps its own AllowAutoRedirect choice, while a host's UseSocketsHttpHandler(...)
    // configuration reuses this SocketsHttpHandler and so keeps redirects off unless it turns them on.
    internal static void AddDefaultClient(IServiceCollection services)
    {
        services.AddHttpClient(WebhookConstants.AdapterName);
        services.Configure<HttpClientFactoryOptions>(
            WebhookConstants.AdapterName,
            x => x.HttpMessageHandlerBuilderActions.Insert(0, y => y.PrimaryHandler = CreateDefaultHandler()));
    }

    // Registered by AddWebhooks(w => w.PublicAddressesOnly = true). PostConfigure runs after every
    // Configure, so the guard sees the final primary handler whatever order the host registered things in.
    internal static void AddPublicAddressGuard(IServiceCollection services)
    {
        services.TryAddSingleton<WebhookHandlerReplacementWarning>();
        services.AddHostedService<WebhookClientStartupCheck>();
        services.PostConfigure<HttpClientFactoryOptions>(
            WebhookConstants.AdapterName,
            x => x.HttpMessageHandlerBuilderActions.Add(ApplyPublicAddressGuard));
    }

    // Webhook receivers rarely need redirects, a 301/302/303 turns the POST into a GET (the payload is
    // dropped) and the signature headers would be re-sent to the redirect target, so a 3xx is the response.
    private static SocketsHttpHandler CreateDefaultHandler() => new() { AllowAutoRedirect = false };

    // A SocketsHttpHandler (Warp's default or one the host configured) gets the guard and keeps every other
    // setting, the host's AllowAutoRedirect choice included. Any other primary handler cannot carry a
    // ConnectCallback, so it is replaced by Warp's default handler and that is logged once: a guard the host
    // asked for must never be silently dropped, and lost proxy or certificate settings must not be silent.
    private static void ApplyPublicAddressGuard(HttpMessageHandlerBuilder builder)
    {
        if (builder.PrimaryHandler is not SocketsHttpHandler sockets)
        {
            builder.Services.GetRequiredService<WebhookHandlerReplacementWarning>()
                .Report(builder.PrimaryHandler?.GetType().FullName ?? "null");

            sockets = CreateDefaultHandler();
            builder.PrimaryHandler = sockets;
        }

        sockets.ConnectCallback = WebhookAddressGuard.ConnectAsync;
    }
}
