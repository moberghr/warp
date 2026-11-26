using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Warp.Dashboard.Push;

// Refuses a hub request (negotiate or connect) whose Origin names another site. The API's request-header guard
// does not reach the hub, and a browser applies no CORS to a WebSocket: any page the user has open could
// otherwise connect straight to ws://localhost/.../api/hub (skipping negotiate) and read the live push stream of a
// loopback-only dashboard, whose Host and remote address both look local. A request without Origin is not a
// browser's cross-site request and passes; authorization still applies to it as before.
// Hosts are compared by name and ports by number; a Host without a port matches only an Origin on its scheme's
// default port.
internal static class WarpHubOriginGuard
{
    public static void RequireSameOrigin(this IEndpointConventionBuilder builder)
        => builder.Add(x =>
        {
            var inner = x.RequestDelegate;
            if (inner == null)
            {
                return;
            }

            x.RequestDelegate = context =>
            {
                if (IsSameOriginOrNone(context.Request))
                {
                    return inner(context);
                }

                context.Response.StatusCode = StatusCodes.Status403Forbidden;

                return Task.CompletedTask;
            };
        });

    private static bool IsSameOriginOrNone(HttpRequest request)
    {
        var origin = request.Headers[HeaderNames.Origin].ToString();
        if (string.IsNullOrEmpty(origin))
        {
            return true;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || !request.Host.HasValue)
        {
            return false;
        }

        if (!string.Equals(uri.Host.Trim('[', ']'), request.Host.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // A Host without a port means the scheme's default port, so only an Origin on its default port matches.
        // Comparing against the Origin's own scheme default (not the request's) keeps a TLS-terminating proxy, which
        // receives https://host as plain http, matching its public origin.
        return request.Host.Port is { } port ? port == uri.Port : uri.IsDefaultPort;
    }
}
