using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Warp.Dashboard.Endpoints;

// Cross-site request forgery guard for the dashboard API: a request that can change state (anything but
// GET, HEAD, OPTIONS and TRACE) must carry HeaderName, or it is answered 400 without reaching
// the handler.
// A cross-site page can send a form POST or a body-less fetch with the victim's ambient credentials (a host's
// SameSite=Lax cookie, Windows authentication, or no credentials at all against a loopback-only dashboard),
// but it cannot add a custom header without a CORS preflight, which the dashboard never grants. The SPA's
// HTTP client sets the header on every call.
// Applied by wrapping each endpoint's request delegate rather than as an endpoint filter: filters only run for
// delegate-based route handlers, and a dashboard extension may map its endpoints with a plain
// RequestDelegate.
internal static class WarpRequestHeaderGuard
{
    // Header the SPA sends on every API request.
    public const string HeaderName = "X-Warp-Request";

    public static void RequireRequestHeader(this IEndpointConventionBuilder builder)
        => builder.Add(x =>
        {
            var inner = x.RequestDelegate;
            if (inner == null)
            {
                return;
            }

            x.RequestDelegate = context =>
            {
                if (IsSafeMethod(context.Request.Method) || context.Request.Headers.ContainsKey(HeaderName))
                {
                    return inner(context);
                }

                context.Response.StatusCode = StatusCodes.Status400BadRequest;

                return Task.CompletedTask;
            };
        });

    private static bool IsSafeMethod(string method)
        => HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method);
}
