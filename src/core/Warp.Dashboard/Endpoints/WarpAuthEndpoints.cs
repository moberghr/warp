using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Warp.Dashboard.Endpoints;

/// <summary>
/// Endpoints for the built-in cookie login. All three are <c>AllowAnonymous</c> — they are how a caller
/// becomes authenticated, so gating them would lock the door from the inside.
/// </summary>
internal static class WarpAuthEndpoints
{
    internal static void MapWarpAuthEndpoints(this RouteGroupBuilder apiGroup, IServiceProvider services)
    {
        // All three endpoints belong to the built-in login and are mapped only with it. In particular the
        // status probe is NOT mapped otherwise: being AllowAnonymous it would bypass every convention the
        // host applied — answering a constant "authenticated: true" to a remote caller of a dashboard that
        // is supposed to be loopback-only, and reporting the opposite of the truth under a host policy.
        if (services.GetService<IWarpDashboardLoginMarker>() == null)
        {
            return;
        }

        // Cookie-free status probe — lets the SPA decide whether to render the login page before firing
        // any other API call, so a fresh browser session doesn't log a 401 in the console on every boot.
        apiGroup
            .MapGet("auth/status", async (HttpContext context) =>
            {
                var result = await context.AuthenticateAsync(WarpDashboardDefaults.AuthenticationScheme);

                return Results.Json(new AuthStatusResponse(result.Succeeded));
            })
            .AllowAnonymous();

        apiGroup
            .MapPost("auth/login", async (HttpContext context, IWarpCredentialValidator validator) =>
            {
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                var username = form["username"].FirstOrDefault() ?? string.Empty;
                var password = form["password"].FirstOrDefault() ?? string.Empty;

                if (!await validator.ValidateAsync(username, password))
                {
                    return Results.Unauthorized();
                }

                var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, username)], WarpDashboardDefaults.AuthenticationScheme);
                await context.SignInAsync(WarpDashboardDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

                return Results.Ok();
            })
            .AllowAnonymous();

        apiGroup
            .MapPost("auth/logout", async (HttpContext context) =>
            {
                await context.SignOutAsync(WarpDashboardDefaults.AuthenticationScheme);

                return Results.Ok();
            })
            .AllowAnonymous();
    }

    // The property name is pinned rather than left to the host's JSON naming policy — the SPA reads
    // `authenticated` off this response before it has rendered anything.
    private sealed record AuthStatusResponse([property: JsonPropertyName("authenticated")] bool Authenticated);
}
