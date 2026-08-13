using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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

        options.RoutePrefix = NormalizeRoutePrefix(options.RoutePrefix);

        var extensions = app.Services.GetServices<IWarpUIExtension>().ToList();

        ClaimDashboard(app.Services, options.RoutePrefix);

        var gate = new WarpDashboardGate();
        var shell = app.MapWarpSpa(options, extensions, gate);

        var apiGroup = app.MapWarpApiEndpoints(options, extensions);
        apiGroup.MapWarpAuthEndpoints(app.Services);

        // Every API response carries a marker header. The SPA reads it to distinguish "the Warp API
        // answered" from "something intercepted this call" — a sign-in redirect it transparently followed
        // and resolved with someone else's 200. A content-type sniff would misfire on the extension
        // endpoints below, which are free to return HTML.
        apiGroup.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers[WarpApiMarkerHeader] = "1";

            return await next(context);
        });

        // Claims every unmatched path under {prefix}/api so it inherits this group's authorization. Without
        // it those paths fell through to the SPA's catch-all and answered an anonymous 404, letting an
        // unauthenticated prober tell registered API routes (401) from unregistered ones and enumerate
        // which addons are live.
        apiGroup.Map("{**path}", () => Results.NotFound());

        List<IEndpointConventionBuilder> api = [apiGroup];

        // The group's routes all sit under "{prefix}/api/", so the bare "{prefix}/api" is not one of them.
        // Mapped separately and folded into the API builders so the host's conventions still cover it.
        api.Add(app.Map($"{options.RoutePrefix}/api", () => Results.NotFound()));

        if (app.Services.GetService<IDashboardPushMarker>() is not null)
        {
            api.Add(app.MapHub<WarpDashboardHub>($"{options.RoutePrefix}/api/hub"));
        }

        var mapped = new WarpUIEndpointConventionBuilder(app.Services, shell, new CompositeEndpointConventionBuilder(api));

        if (app.Services.GetService<IWarpDashboardLoginMarker>() != null)
        {
            ApplyBuiltInLoginGate(mapped, gate);
        }

        return mapped;
    }

    /// <summary>Name of the header stamped on every dashboard API response.</summary>
    internal const string WarpApiMarkerHeader = "X-Warp-Api";

    // Registering the built-in login gates the dashboard on its own — the way UseBuiltInLogin did before the
    // endpoints existed. Requiring a separate RequireWarpDashboardLogin() call would mean the half-migrated
    // shape (services registered, convention forgotten) compiles, renders a login page, and serves every API
    // route anonymously.
    //
    // Applied through Finally, which runs after every convention the host added, for two reasons: it can see
    // whether the host already gated these endpoints, and it can therefore step aside rather than stack on
    // top. A caller who satisfies the host's policy must not then be turned away for lacking a Warp cookie
    // that the host's setup gives them no way to obtain.
    private static void ApplyBuiltInLoginGate(WarpUIEndpointConventionBuilder mapped, WarpDashboardGate gate)
    {
        mapped.Api.Finally(builder =>
        {
            var authorization = builder.Metadata.OfType<IAuthorizeData>().ToList();

            var hostGated = authorization.Exists(x => !string.Equals(x.Policy, WarpDashboardDefaults.LoginPolicy, StringComparison.Ordinal))
                || builder.Metadata.OfType<AuthorizationPolicy>().Any();

            if (hostGated)
            {
                gate.ReplacedByHostPolicy = true;

                return;
            }

            // Nothing else claimed these endpoints. RequireWarpDashboardLogin() may already have added the
            // same policy explicitly, in which case adding it twice would only evaluate it twice.
            if (authorization.Count == 0)
            {
                builder.Metadata.Add(new AuthorizeAttribute(WarpDashboardDefaults.LoginPolicy));
            }
        });
    }

    // A missing leading slash silently breaks asset resolution (the prefix is sliced off request paths by
    // length) and makes the injected apiPath document-relative; a trailing slash builds "{prefix}//{**path}"
    // and dies inside the route parser with no hint at the cause. Both are worth correcting rather than
    // debugging.
    private static string NormalizeRoutePrefix(string routePrefix)
    {
        var trimmed = routePrefix?.Trim().Trim('/') ?? string.Empty;

        if (string.IsNullOrEmpty(trimmed))
        {
            throw new ArgumentException(
                "WarpUIOptions.RoutePrefix must name a path segment, e.g. \"/warp\". Mounting the dashboard at "
                + "the application root is not supported — its catch-all route would swallow every request.",
                nameof(routePrefix));
        }

        return $"/{trimmed}";
    }

    // The built-in login is one authentication scheme with one cookie name and one cookie path, so it
    // cannot serve two dashboards: whichever prefix is mapped last wins the path, and logging in to the
    // other silently never sticks because the browser won't send the cookie back.
    private static void ClaimDashboard(IServiceProvider services, string routePrefix)
    {
        var cookiePath = services.GetService<WarpDashboardCookiePath>();
        if (cookiePath == null)
        {
            return;
        }

        if (cookiePath.Value != null && services.GetService<IWarpDashboardLoginMarker>() != null)
        {
            throw new InvalidOperationException(
                $"MapWarpUI was called more than once (already mapped at \"{cookiePath.Value}\", now \"{routePrefix}\") "
                + "while the built-in login is registered. One cookie scheme cannot serve two dashboards — sign-in "
                + "would silently fail on all but the last one mapped. Map a single dashboard, or gate the others "
                + "with your own authorization policy instead of AddBuiltInLogin.");
        }

        // The prefix is only known here, and cookie options resolve lazily on the first request, so this
        // lands before any reader (see WarpDashboardCookiePath).
        cookiePath.Value = routePrefix;
    }
}
