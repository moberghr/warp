namespace Warp.Http;

/// <summary>
/// Tags a handler class — <c>IRequestHandler&lt;TRequest, TResponse&gt;</c> or
/// <c>IStreamRequestHandler&lt;TRequest, TResponse&gt;</c> — for HTTP exposure.
/// Multiple attributes may be applied to the same handler class to produce versioning
/// aliases; when multiple attributes are present, each must specify a distinct
/// <see cref="Name"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public class WarpHttpAttribute : Attribute
{
    public WarpHttpAttribute(string method, string route)
    {
        Method = method;
        Route = route;
    }

    /// <summary>The HTTP method (e.g. <c>GET</c>, <c>POST</c>) the endpoint responds to.</summary>
    public string Method { get; }

    /// <summary>The route template, e.g. <c>/orders/{id}</c>. Route tokens bind to request members.</summary>
    public string Route { get; }

    /// <summary>
    /// Optional named group. <see cref="EndpointRouteBuilderExtensions.MapWarpHttp"/> registers
    /// only descriptors whose group strictly matches the argument (null matches null).
    /// </summary>
    public string? Group { get; set; }

    /// <summary>
    /// Optional endpoint name (becomes <c>RouteEndpoint.DisplayName</c> /
    /// OpenAPI operationId). Required when the handler class carries multiple
    /// <see cref="WarpHttpAttribute"/> instances.
    /// </summary>
    public string? Name { get; set; }
}

/// <summary>Exposes the tagged handler as an HTTP <c>GET</c> endpoint at the given route.
/// Non-body verb: request members bind from route / query / header via <c>[AsParameters]</c>.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class WarpHttpGetAttribute : WarpHttpAttribute
{
    public WarpHttpGetAttribute(string route)
        : base("GET", route)
    {
    }
}

/// <summary>Exposes the tagged handler as an HTTP <c>POST</c> endpoint at the given route.
/// Body verb: an unattributed request type binds from the JSON body.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class WarpHttpPostAttribute : WarpHttpAttribute
{
    public WarpHttpPostAttribute(string route)
        : base("POST", route)
    {
    }
}

/// <summary>Exposes the tagged handler as an HTTP <c>PUT</c> endpoint at the given route.
/// Body verb: an unattributed request type binds from the JSON body.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class WarpHttpPutAttribute : WarpHttpAttribute
{
    public WarpHttpPutAttribute(string route)
        : base("PUT", route)
    {
    }
}

/// <summary>Exposes the tagged handler as an HTTP <c>PATCH</c> endpoint at the given route.
/// Body verb: typically a <c>[FromRoute]</c> id plus a single <c>[FromBody]</c> sub-DTO.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class WarpHttpPatchAttribute : WarpHttpAttribute
{
    public WarpHttpPatchAttribute(string route)
        : base("PATCH", route)
    {
    }
}

/// <summary>Exposes the tagged handler as an HTTP <c>DELETE</c> endpoint at the given route.
/// Non-body verb: request members bind from route / query / header via <c>[AsParameters]</c>.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class WarpHttpDeleteAttribute : WarpHttpAttribute
{
    public WarpHttpDeleteAttribute(string route)
        : base("DELETE", route)
    {
    }
}
