using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace Warp.Core.Webhooks;

// Registration of the "warp-webhooks" named HttpClient: Core's default primary handler (redirects off) and the
// public-address guard, on by default since 8.1 (WebhookAddressPolicy; AddWebhooks(w => w.PublicAddressesOnly = false)
// turns it off for local development).
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

        // The guard, once per service collection (AddWarp can run twice). PostConfigure runs after every Configure, so
        // it sees the final primary handler whatever order the host registered things in; it reads the policy when the
        // handler is built, so an AddWebhooks opt-out registered later still applies.
        if (services.Any(x => x.ServiceType == typeof(WebhookGuardUnavailableWarning)))
        {
            return;
        }

        services.TryAddSingleton<WebhookGuardUnavailableWarning>();
        services.PostConfigure<HttpClientFactoryOptions>(
            WebhookConstants.AdapterName,
            x => x.HttpMessageHandlerBuilderActions.Add(ApplyPublicAddressGuard));
    }

    // Webhook receivers rarely need redirects, a 301/302/303 turns the POST into a GET (the payload is
    // dropped) and the signature headers would be re-sent to the redirect target, so a 3xx is the response.
    private static SocketsHttpHandler CreateDefaultHandler() => new() { AllowAutoRedirect = false };

    // A SocketsHttpHandler (Warp's default or one the host configured) gets the guard and keeps every other
    // setting, the host's AllowAutoRedirect choice included. Any other primary handler cannot carry a
    // ConnectCallback: it is KEPT (its proxy or certificates are never dropped) behind a handler that fails every
    // attempt closed with the fix in the message, and that is logged once at Warning. Never send unguarded.
    private static void ApplyPublicAddressGuard(HttpMessageHandlerBuilder builder)
    {
        if (!builder.Services.GetRequiredService<IOptions<WebhookAddressPolicy>>().Value.PublicAddressesOnly)
        {
            return;
        }

        if (builder.PrimaryHandler is SocketsHttpHandler sockets)
        {
            sockets.ConnectCallback = WebhookAddressGuard.ConnectAsync;

            return;
        }

        builder.Services.GetRequiredService<WebhookGuardUnavailableWarning>()
            .Report(builder.PrimaryHandler?.GetType().FullName ?? "null");

        builder.PrimaryHandler = new WebhookGuardUnavailableHandler(builder.PrimaryHandler ?? CreateDefaultHandler());
    }
}
