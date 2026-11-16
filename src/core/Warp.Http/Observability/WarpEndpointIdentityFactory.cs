using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Warp.Core.Endpoints;

namespace Warp.Http.Observability;

// The one place a host endpoint's identity is built — used at endpoint-build time by the
// WithWarpObservability convention and at request time by the middleware for [WarpObserved]-only endpoints.
internal static class WarpEndpointIdentityFactory
{
    // Any method, or several: the identity is per endpoint and EndpointCallLog.Method is 16 chars, so a
    // joined list ("GET,HEAD,POST") is neither bounded nor guaranteed to fit.
    internal const string AnyMethod = "*";

    // Column bounds of EndpointCallLog (RouteTemplate 1024, Operation 200) — an over-long value would fail
    // the whole flush batch on SQL Server.
    internal const int MaxRouteTemplateLength = 1024;
    internal const int MaxOperationLength = 200;

    // Convention path. Null when the endpoint already carries an identity (a Warp endpoint, or an inner
    // convention ran first), is excluded, or was never opted in.
    public static WarpEndpointIdentity? TryCreate(EndpointBuilder builder)
    {
        if (!IsOptedIn(builder.Metadata))
        {
            return null;
        }

        var metadata = builder.Metadata as IReadOnlyList<object> ?? [.. builder.Metadata];

        return Create((builder as RouteEndpointBuilder)?.RoutePattern, metadata, builder.DisplayName);
    }

    // Middleware path for an endpoint carrying the [WarpObserved] marker but no identity (the attribute-only
    // opt-in, which has no convention to build it at startup).
    public static WarpEndpointIdentity? TryCreate(Endpoint endpoint)
    {
        var metadata = endpoint.Metadata;
        if (metadata.GetMetadata<WarpEndpointIdentity>() is not null
            || metadata.GetMetadata<WarpObservedAttribute>() is null
            || metadata.GetMetadata<ExcludeFromWarpObservabilityAttribute>() is not null)
        {
            return null;
        }

        return Create((endpoint as RouteEndpoint)?.RoutePattern, metadata, endpoint.DisplayName);
    }

    public static WarpEndpointIdentity Create(RoutePattern? pattern, IReadOnlyList<object> metadata, string? displayName)
    {
        var template = Truncate(pattern is null ? displayName ?? "/" : RenderTemplate(pattern), MaxRouteTemplateLength);

        return new WarpEndpointIdentity(
            ResolveMethod(metadata),
            template,
            Truncate(ResolveOperation(metadata, displayName) ?? template, MaxOperationLength));
    }

    // Rendered from the parsed segments rather than RawText so a conventional MVC route
    // ("{controller=Home}/{action=Index}/{id?}") becomes the action's own template ("/Home/Index/{id?}") via
    // its required values, instead of one shared template for every action. Inline constraints and defaults
    // are dropped — the flusher strips constraints anyway, and the meter route stays clean.
    internal static string RenderTemplate(RoutePattern pattern)
    {
        var sb = new StringBuilder();
        foreach (var segment in pattern.PathSegments)
        {
            sb.Append('/');
            foreach (var part in segment.Parts)
            {
                AppendPart(sb, part, pattern.RequiredValues);
            }
        }

        return sb.Length == 0 ? "/" : sb.ToString();
    }

    private static bool IsOptedIn(IList<object> metadata)
    {
        var observed = false;
        foreach (var item in metadata)
        {
            if (item is WarpEndpointIdentity or ExcludeFromWarpObservabilityAttribute)
            {
                return false;
            }

            observed |= item is WarpObservedAttribute;
        }

        return observed;
    }

    private static void AppendPart(StringBuilder sb, RoutePatternPart part, IReadOnlyDictionary<string, object?> requiredValues)
    {
        switch (part)
        {
            case RoutePatternLiteralPart literal:
                sb.Append(literal.Content);
                break;
            case RoutePatternSeparatorPart separator:
                sb.Append(separator.Content);
                break;
            case RoutePatternParameterPart parameter
                when requiredValues.TryGetValue(parameter.Name, out var value) && value is string { Length: > 0 } text:
                sb.Append(text);
                break;
            case RoutePatternParameterPart parameter:
                sb.Append('{');
                if (parameter.IsCatchAll)
                {
                    sb.Append(parameter.EncodeSlashes ? "*" : "**");
                }

                sb.Append(parameter.Name);
                if (parameter.IsOptional)
                {
                    sb.Append('?');
                }

                sb.Append('}');
                break;
        }
    }

    private static string ResolveMethod(IReadOnlyList<object> metadata)
    {
        var methods = Last<HttpMethodMetadata>(metadata)?.HttpMethods;

        return methods is { Count: 1 } ? methods[0].ToUpperInvariant() : AnyMethod;
    }

    // Explicit name (innermost marker that set one) > endpoint name > route name > Controller.Action > display name.
    private static string? ResolveOperation(IReadOnlyList<object> metadata, string? displayName)
    {
        for (var i = metadata.Count - 1; i >= 0; i--)
        {
            if (metadata[i] is WarpObservedAttribute { Operation: { Length: > 0 } explicitName })
            {
                return explicitName;
            }
        }

        return NullIfEmpty(Last<IEndpointNameMetadata>(metadata)?.EndpointName)
            ?? NullIfEmpty(Last<IRouteNameMetadata>(metadata)?.RouteName)
            ?? ControllerAction(Last<ControllerActionDescriptor>(metadata))
            ?? NullIfEmpty(displayName);
    }

    private static string? ControllerAction(ControllerActionDescriptor? action)
        => action is null ? null : $"{action.ControllerName}.{action.ActionName}";

    private static T? Last<T>(IReadOnlyList<object> metadata)
        where T : class
    {
        for (var i = metadata.Count - 1; i >= 0; i--)
        {
            if (metadata[i] is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
