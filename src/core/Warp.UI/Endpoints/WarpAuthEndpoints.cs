using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Warp.UI.Endpoints;

/// <summary>
/// Endpoints for the built-in cookie login. All three are <c>AllowAnonymous</c> — they are how a caller
/// becomes authenticated, so gating them would lock the door from the inside.
/// </summary>
internal static class WarpAuthEndpoints
{
    internal static void MapWarpAuthEndpoints(this RouteGroupBuilder apiGroup, IServiceProvider services)
    {
        var hasLogin = services.GetService<IWarpDashboardLoginMarker>() != null;

        // Cookie-free status probe — lets the SPA decide whether to render the login page before firing
        // any other API call, so a fresh browser session doesn't log a 401 in the console on every boot.
        apiGroup
            .MapGet("auth/status", async (HttpContext context) =>
            {
                var authenticated = !hasLogin
                    || (await context.AuthenticateAsync(WarpDashboardDefaults.AuthenticationScheme)).Succeeded;

                return Results.Json(new AuthStatusResponse(authenticated));
            })
            .AllowAnonymous();

        if (!hasLogin)
        {
            return;
        }

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
