using System.Globalization;
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
            .MapPost("auth/login", async (HttpContext context, IWarpCredentialValidator validator, WarpDashboardLoginThrottle throttle) =>
            {
                var form = await ReadFormAsync(context.Request, context.RequestAborted);
                if (form == null)
                {
                    return Results.BadRequest();
                }

                var username = form["username"].FirstOrDefault() ?? string.Empty;
                var password = form["password"].FirstOrDefault() ?? string.Empty;
                var ip = context.Connection.RemoteIpAddress?.ToString() ?? string.Empty;

                var wait = throttle.TryBegin(ip, username);
                if (wait != null)
                {
                    context.Response.Headers.RetryAfter = Math.Ceiling(wait.Value.TotalSeconds).ToString(CultureInfo.InvariantCulture);

                    return Results.StatusCode(StatusCodes.Status429TooManyRequests);
                }

                if (!await validator.ValidateAsync(username, password))
                {
                    return Results.Unauthorized();
                }

                throttle.Succeeded(ip, username);

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

    // A body that is not a form, or a malformed one, is the caller's mistake: answer 400 rather than let
    // ReadFormAsync's exception surface as a 500.
    private static async Task<IFormCollection?> ReadFormAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            return null;
        }

        try
        {
            return await request.ReadFormAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
        {
            return null;
        }
    }

    // The property name is pinned rather than left to the host's JSON naming policy — the SPA reads
    // `authenticated` off this response before it has rendered anything.
    private sealed record AuthStatusResponse([property: JsonPropertyName("authenticated")] bool Authenticated);
}
