using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Warp.Dashboard;

/// <summary>
/// The two authorization shapes Warp ships for hosts that have no identity system of their own. A host
/// that does have one needs none of this — apply its own policy:
/// <c>app.MapWarpDashboard("/warp").RequireAuthorization("YourPolicy")</c>.
/// </summary>
public static class WarpDashboardEndpointConventions
{
    /// <summary>
    /// Gates the dashboard on the built-in cookie login registered by
    /// <c>AddWarpDashboard().AddBuiltInLogin&lt;TValidator&gt;()</c>.
    /// </summary>
    /// <remarks>
    /// Applies to the REST API and the SignalR hub only. The SPA shell stays anonymous because it renders
    /// the login form itself — gating it would challenge the very page that collects the credentials.
    /// </remarks>
    public static WarpDashboardEndpointConventionBuilder RequireWarpDashboardLogin(this WarpDashboardEndpointConventionBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.Services.GetService<IWarpDashboardLoginMarker>() == null)
        {
            throw new InvalidOperationException(
                $"{nameof(RequireWarpDashboardLogin)}() requires the built-in login. Call "
                + "services.AddWarpDashboard().AddBuiltInLogin<TValidator>() during service registration.");
        }

        builder.Api.RequireAuthorization(WarpDashboardDefaults.LoginPolicy);

        return builder;
    }

    /// <summary>
    /// Restricts the whole dashboard — shell, API and hub — to loopback callers. A remote caller gets 403;
    /// signing in cannot change the answer, so there is nothing to challenge.
    /// </summary>
    public static WarpDashboardEndpointConventionBuilder RequireLocalRequests(this WarpDashboardEndpointConventionBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.Services.GetService<IWarpDashboardMarker>() == null)
        {
            throw new InvalidOperationException(
                $"{nameof(RequireLocalRequests)}() requires services.AddWarpDashboard() during service registration.");
        }

        builder.RequireAuthorization(WarpDashboardDefaults.LocalRequestsPolicy);

        return builder;
    }
}
