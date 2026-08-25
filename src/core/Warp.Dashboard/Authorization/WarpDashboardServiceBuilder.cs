using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Warp.Dashboard;

/// <summary>
/// Returned by <c>AddWarpDashboard()</c> so optional dashboard authorization features chain off it,
/// the way <c>AddAuthentication().AddCookie()</c> reads.
/// </summary>
public sealed class WarpDashboardServiceBuilder
{
    internal WarpDashboardServiceBuilder(IServiceCollection services) => Services = services;

    /// <summary>The service collection being configured.</summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// Enables the built-in login page: a real cookie authentication scheme
    /// (<see cref="WarpDashboardDefaults.AuthenticationScheme"/>) plus the
    /// <see cref="WarpDashboardDefaults.LoginPolicy"/> policy that <c>RequireWarpDashboardLogin()</c>
    /// applies. <typeparamref name="TValidator"/> is registered as scoped, so it can inject a
    /// <c>DbContext</c> and validate credentials against the database.
    /// </summary>
    /// <remarks>
    /// For a host that already has an identity system, skip this entirely and gate the dashboard on
    /// your own policy: <c>app.MapWarpDashboard("/warp").RequireAuthorization("YourPolicy")</c>.
    /// </remarks>
    public WarpDashboardServiceBuilder AddBuiltInLogin<TValidator>(Action<WarpDashboardLoginOptions>? configure = null)
        where TValidator : class, IWarpCredentialValidator
    {
        var loginOptions = new WarpDashboardLoginOptions();
        configure?.Invoke(loginOptions);

        Services.AddScoped<IWarpCredentialValidator, TValidator>();
        Services.AddSingleton<IWarpDashboardLoginMarker, WarpDashboardLoginMarker>();

        Services
            .AddAuthentication()
            .AddCookie(WarpDashboardDefaults.AuthenticationScheme, x =>
            {
                x.Cookie.Name = WarpDashboardDefaults.CookieName;
                x.Cookie.HttpOnly = true;
                x.Cookie.SameSite = SameSiteMode.Strict;
                x.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                x.ExpireTimeSpan = loginOptions.ExpireTimeSpan;
                x.SlidingExpiration = loginOptions.SlidingExpiration;

                // Under built-in login the SPA shell is anonymous and renders its own login form, so every
                // challenge here is an XHR from that shell. Answer with the status code the SPA reads —
                // a 302 to a login path is a redirect the fetch cannot usefully follow.
                x.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;

                    return Task.CompletedTask;
                };

                x.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;

                    return Task.CompletedTask;
                };
            });

        Services
            .AddOptions<CookieAuthenticationOptions>(WarpDashboardDefaults.AuthenticationScheme)
            .Configure<WarpDashboardCookiePath>((x, prefix) => x.Cookie.Path = loginOptions.CookiePath ?? prefix.Value ?? "/");

        Services
            .AddAuthorizationBuilder()
            .AddPolicy(WarpDashboardDefaults.LoginPolicy, x => x
                .AddAuthenticationSchemes(WarpDashboardDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser());

        return this;
    }
}
