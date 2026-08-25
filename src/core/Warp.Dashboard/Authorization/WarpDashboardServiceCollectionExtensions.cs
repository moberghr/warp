using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Warp.Dashboard;

public static class WarpDashboardServiceCollectionExtensions
{
    /// <summary>
    /// Registers the dashboard's built-in authorization helpers: the deny-only scheme
    /// (<see cref="WarpDashboardDefaults.DenyScheme"/>) and the localhost-only policy that
    /// <c>RequireLocalRequests()</c> applies. Chain <c>AddBuiltInLogin&lt;T&gt;()</c> for the cookie login.
    /// </summary>
    /// <remarks>
    /// Not needed for open access (<c>app.MapWarpDashboard("/warp")</c>) or when the host gates the dashboard
    /// on its own policy (<c>app.MapWarpDashboard("/warp").RequireAuthorization("YourPolicy")</c>).
    /// </remarks>
    public static WarpDashboardServiceBuilder AddWarpDashboard(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IWarpDashboardMarker, WarpDashboardMarker>();
        services.AddSingleton<WarpDashboardCookiePath>();

        services
            .AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, WarpDenyAuthenticationHandler>(WarpDashboardDefaults.DenyScheme, _ => { });

        services
            .AddAuthorizationBuilder()
            .AddPolicy(WarpDashboardDefaults.LocalRequestsPolicy, x => x
                .AddAuthenticationSchemes(WarpDashboardDefaults.DenyScheme)
                .RequireAssertion(IsLoopbackRequest));

        return new WarpDashboardServiceBuilder(services);
    }

    // Resource is the HttpContext for endpoint-routed requests. Anything else — and a null remote
    // address, which is how a non-TCP transport presents — fails closed.
    private static bool IsLoopbackRequest(AuthorizationHandlerContext context)
    {
        if (context.Resource is not HttpContext httpContext)
        {
            return false;
        }

        var remoteIp = httpContext.Connection.RemoteIpAddress;

        return remoteIp != null && IPAddress.IsLoopback(remoteIp);
    }
}
