using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Warp.UI.DashboardPush;
using Warp.UI.Endpoints;
using Warp.UI.Extensions;

namespace Warp.UI;

public static class WarpUIBuilder
{
    /// <summary>
    /// Maps the Warp dashboard at <paramref name="routePrefix"/> and returns a builder for the endpoints,
    /// so the host gates it with ASP.NET's own authorization:
    /// <c>app.MapWarpUI("/warp").RequireAuthorization("YourPolicy")</c>.
    /// </summary>
    public static WarpUIEndpointConventionBuilder MapWarpUI(this WebApplication app, string routePrefix)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.MapWarpUI(x => x.RoutePrefix = routePrefix);
    }

    /// <summary>
    /// Maps the Warp dashboard, taking options from DI and applying <paramref name="setupAction"/> on top.
    /// </summary>
    public static WarpUIEndpointConventionBuilder MapWarpUI(this WebApplication app, Action<WarpUIOptions>? setupAction = null)
    {
        ArgumentNullException.ThrowIfNull(app);

        WarpUIOptions options;
        using (var scope = app.Services.CreateScope())
        {
            options = scope.ServiceProvider.GetRequiredService<IOptionsSnapshot<WarpUIOptions>>().Value;
            setupAction?.Invoke(options);
        }

        return app.MapWarpUI(options);
    }

    /// <summary>
    /// Maps the Warp dashboard with explicit options.
    /// </summary>
    public static WarpUIEndpointConventionBuilder MapWarpUI(this WebApplication app, WarpUIOptions options)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);

        var extensions = app.Services.GetServices<IWarpUIExtension>().ToList();

        // The login cookie is scoped to the dashboard, and the prefix is only known here (see
        // WarpDashboardCookiePath). Set before the first request resolves the cookie options.
        var cookiePath = app.Services.GetService<WarpDashboardCookiePath>();
        if (cookiePath != null)
        {
            cookiePath.Value = options.RoutePrefix;
        }

        var shell = app.MapWarpSpa(options, extensions);

        var apiGroup = app.MapWarpApiEndpoints(options, extensions);
        apiGroup.MapWarpAuthEndpoints(app.Services);

        var api = new List<IEndpointConventionBuilder> { apiGroup };
        if (app.Services.GetService<IDashboardPushMarker>() is not null)
        {
            api.Add(app.MapHub<WarpDashboardHub>($"{options.RoutePrefix}/api/hub"));
        }

        return new WarpUIEndpointConventionBuilder(app.Services, shell, new CompositeEndpointConventionBuilder(api));
    }
}
