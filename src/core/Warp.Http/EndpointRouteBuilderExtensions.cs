using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
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
    /// <returns>
    /// A builder for the endpoints just mapped, so conventions chain:
    /// <c>app.MapWarpHttp().RequireAuthorization("policy")</c>. It is also <paramref name="endpoints"/> as an
    /// <see cref="IEndpointRouteBuilder"/>.
    /// </returns>
    public static WarpHttpEndpointConventionBuilder MapWarpHttp(this IEndpointRouteBuilder endpoints, string? group = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // A builder this method returned stands for the route builder it wraps, so mapping through it twice is
        // still caught below.
        if (endpoints is WarpHttpEndpointConventionBuilder returned)
        {
            endpoints = returned.Inner;
        }

        var marker = MarkerStorage.GetMarker(endpoints);
        var key = group ?? string.Empty;
        if (!marker.TryAdd(key, true))
        {
            throw new InvalidOperationException(
                $"MapWarpHttp(group: {(group is null ? "null" : "\"" + group + "\"")}) was already called on this endpoint route builder. " +
                "Each (builder, group) pair may be mapped only once.");
        }

        var options = endpoints.ServiceProvider.GetService<WarpHttpOptions>() ?? new WarpHttpOptions();

        // The descriptor's HandlerDelegate is the source-generated Minimal API delegate — ASP.NET binds its
        // parameters (route / query / header / body) before invoking our dispatch trampoline.
        var mapped = WarpGeneratedHttpRegistry.Snapshot()
            .Where(x => string.Equals(x.Group, group, StringComparison.Ordinal))
            .Select(x => (IEndpointConventionBuilder)MapOne(endpoints, x, options))
            .ToList();

        return new WarpHttpEndpointConventionBuilder(endpoints, mapped);
    }

    private static RouteHandlerBuilder MapOne(IEndpointRouteBuilder endpoints, HttpEndpointDescriptor descriptor, WarpHttpOptions options)
    {
        var builder = endpoints.MapMethods(descriptor.Route, [descriptor.Method], descriptor.HandlerDelegate);
        ApplyMetadata(builder, descriptor, options);

        return builder;
    }

    private static void ApplyMetadata(RouteHandlerBuilder builder, HttpEndpointDescriptor descriptor, WarpHttpOptions options)
    {
        // Stable identity for inbound observability: Warp endpoints are always observed. A host convention
        // (group.WithWarpObservability()) never overwrites it — its Finally step skips an endpoint that
        // already has one. Operation is the route name if set, else the handler type name.
        builder.WithMetadata(new Observability.WarpEndpointIdentity(
            descriptor.Method,
            descriptor.Route,
            string.IsNullOrEmpty(descriptor.Name) ? descriptor.HandlerType.Name : descriptor.Name!));

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
            // endpoints unless the host asked to keep it. A [RequireAntiforgeryToken] on the handler
            // class decides for its endpoint: it is forwarded below, and nothing is added here,
            // because DisableAntiforgery's metadata lands after the forwarded attributes and would win.
            builder.Accepts(descriptor.RequestType, "multipart/form-data");
            var handlerDecides = descriptor.HandlerType
                .GetCustomAttributes(inherit: false)
                .OfType<IAntiforgeryMetadata>()
                .Any();
            if (!handlerDecides && options.ValidateAntiforgeryOnFormEndpoints)
            {
                builder.WithMetadata(new RequireAntiforgeryTokenAttribute());
            }
            else if (!handlerDecides)
            {
                builder.DisableAntiforgery();
            }
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
        foreach (var attribute in HandlerAttributes(descriptor.HandlerType))
        {
            if (attribute is WarpHttpAttribute)
            {
                continue;
            }

            builder.WithMetadata(attribute);
        }
    }

    // The handler class's own attributes, as in 8.0, plus authorization read through its base classes.
    //
    // Authorization is inherited so an [Authorize] on an abstract base handler protects every subclass; reading
    // only the concrete class dropped it and left the endpoint open. Every [Authorize] in the hierarchy is kept,
    // so a subclass adds requirements and never sheds its base's (ASP.NET requires all of them). [AllowAnonymous]
    // is taken only from the NEAREST class that declares any authorization attribute: ASP.NET lets one
    // IAllowAnonymous override every [Authorize], so a base class's [AllowAnonymous] must not open a subclass that
    // declares [Authorize] itself. Each attribute's AttributeUsage.Inherited is honoured.
    //
    // Nothing else is inherited. Metadata ASP.NET honours by mere presence ([DisableRateLimiting] beside a
    // subclass's [EnableRateLimiting], say) would otherwise switch a subclass's own setting off.
    private static IEnumerable<object> HandlerAttributes(Type handlerType)
    {
        var nearest = NearestAuthorization(handlerType);

        return handlerType.GetCustomAttributes(inherit: false)
            .Where(x => !IsAuthorization(x))
            .Concat(handlerType.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>().Where(x => x is not IAllowAnonymous))
            .Concat(nearest.OfType<IAllowAnonymous>());
    }

    private static object[] NearestAuthorization(Type handlerType)
    {
        for (var type = handlerType; type != null; type = type.BaseType)
        {
            var declared = type.GetCustomAttributes(inherit: false)
                .Where(IsAuthorization)
                .Where(x => type == handlerType || x.GetType().GetCustomAttribute<AttributeUsageAttribute>()?.Inherited != false)
                .ToArray();

            if (declared.Length > 0)
            {
                return declared;
            }
        }

        return [];
    }

    private static bool IsAuthorization(object attribute) => attribute is IAuthorizeData or IAllowAnonymous;

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
