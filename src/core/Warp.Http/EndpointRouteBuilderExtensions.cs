using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Warp.Core.Handlers;
using Warp.Http.Discovery;

namespace Warp.Http;

public static class EndpointRouteBuilderExtensions
{
    /// <summary>
    /// Registers all <see cref="HttpEndpointDescriptor"/> entries in the global
    /// <see cref="WarpGeneratedHttpRegistry"/> whose <see cref="HttpEndpointDescriptor.Group"/>
    /// strictly equals <paramref name="group"/> (null matches null).
    /// Throws <see cref="InvalidOperationException"/> if called twice with the same
    /// <paramref name="group"/> on the same <paramref name="endpoints"/> instance.
    /// </summary>
    public static IEndpointRouteBuilder MapWarpHttp(this IEndpointRouteBuilder endpoints, string? group = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var marker = MarkerStorage.GetMarker(endpoints);
        var key = group ?? string.Empty;
        if (!marker.TryAdd(key, true))
        {
            throw new InvalidOperationException(
                $"MapWarpHttp(group: {(group is null ? "null" : "\"" + group + "\"")}) was already called on this endpoint route builder. " +
                "Each (builder, group) pair may be mapped only once.");
        }

        foreach (var descriptor in WarpGeneratedHttpRegistry.Snapshot())
        {
            if (!string.Equals(descriptor.Group, group, StringComparison.Ordinal))
            {
                continue;
            }

            // The descriptor's HandlerDelegate is the source-generated Minimal API delegate —
            // ASP.NET binds its parameters (route / query / header / body) before invoking
            // our dispatch trampoline.
            var builder = endpoints.MapMethods(descriptor.Route, [descriptor.Method], descriptor.HandlerDelegate);
            ApplyMetadata(builder, descriptor);
        }

        return endpoints;
    }

    private static void ApplyMetadata(RouteHandlerBuilder builder, HttpEndpointDescriptor descriptor)
    {
        if (!string.IsNullOrEmpty(descriptor.Name))
        {
            builder.WithName(descriptor.Name!);
        }

        var firstSegment = ExtractFirstRouteSegment(descriptor.Route);
        if (!string.IsNullOrEmpty(firstSegment))
        {
            builder.WithTags(firstSegment);
        }

        if (descriptor.RequiresFormBinding)
        {
            // Form binding reads multipart/form-data, not JSON. Antiforgery validation would
            // reject programmatic / non-browser uploads (no token) — suppress it on these
            // endpoints, matching how a hand-written MapPost with IFormFile is configured.
            builder.Accepts(descriptor.RequestType, "multipart/form-data");
            builder.DisableAntiforgery();
        }
        else if (IsBodyVerb(descriptor.Method))
        {
            builder.Accepts(descriptor.RequestType, "application/json");
        }

        if (descriptor.ResponseType == typeof(Unit))
        {
            builder.Produces(StatusCodes.Status204NoContent);
        }
        else if (typeof(IResult).IsAssignableFrom(descriptor.ResponseType))
        {
            // The handler returns an IResult and owns the response (status, content type, body),
            // so a fixed "200 application/json" Produces would be misleading in OpenAPI. The
            // concrete shape is only known at runtime — leave it undeclared.
        }
        else
        {
            builder.Produces(StatusCodes.Status200OK, descriptor.ResponseType, "application/json");
        }

        // Surface attributes declared on the handler class as standard ASP.NET endpoint
        // metadata so the matching middleware picks them up — [Authorize] / [AllowAnonymous]
        // (RequireAuthorization composes naturally), [EnableRateLimiting] / [DisableRateLimiting],
        // [Tags], [OutputCache], [ProducesResponseType], and any future metadata attribute.
        // Warp's own [WarpHttp*] routing markers carry route/verb info, not endpoint metadata,
        // and are already consumed by the source generator — skip them.
        foreach (var attribute in descriptor.HandlerType.GetCustomAttributes(inherit: false))
        {
            if (attribute is WarpHttpAttribute)
            {
                continue;
            }

            builder.WithMetadata(attribute);
        }
    }

    private static bool IsBodyVerb(string method)
    {
        return string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
            || string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(method, "PATCH", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractFirstRouteSegment(string route)
    {
        if (string.IsNullOrEmpty(route))
        {
            return null;
        }

        var trimmed = route.TrimStart('/');
        var slash = trimmed.IndexOf('/', StringComparison.Ordinal);
        var first = slash < 0 ? trimmed : trimmed.Substring(0, slash);

        // Skip placeholder segments like "{id}".
        if (first.StartsWith('{'))
        {
            return null;
        }

        return string.IsNullOrEmpty(first) ? null : first;
    }

    private static class MarkerStorage
    {
#pragma warning disable IDE0028 // ConditionalWeakTable<,> doesn't support collection-expression init
        private static readonly ConditionalWeakTable<IEndpointRouteBuilder, ConcurrentDictionary<string, bool>> _markers = new();
#pragma warning restore IDE0028

        public static ConcurrentDictionary<string, bool> GetMarker(IEndpointRouteBuilder endpoints)
        {
            return _markers.GetValue(endpoints, CreateMarker);
        }

        private static ConcurrentDictionary<string, bool> CreateMarker(IEndpointRouteBuilder builder)
        {
            return new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
        }
    }
}
