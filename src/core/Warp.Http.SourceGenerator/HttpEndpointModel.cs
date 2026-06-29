using Microsoft.CodeAnalysis;

namespace Warp.Http.SourceGenerator;

/// <summary>
/// Internal model of one HTTP-exposed handler class discovered in the compilation.
/// Carries the handler symbol plus the resolved request/response from
/// <c>IRequestHandler&lt;TRequest, TResponse&gt;</c> or
/// <c>IStreamRequestHandler&lt;TRequest, TResponse&gt;</c>.
/// </summary>
internal sealed class HttpEndpointModel
{
    public HttpEndpointModel(
        INamedTypeSymbol handlerType,
        INamedTypeSymbol requestType,
        ITypeSymbol responseType,
        HttpHandlerKind kind,
        string method,
        string route,
        string? group,
        string? name,
        bool requiresFormBinding)
    {
        HandlerType = handlerType;
        RequestType = requestType;
        ResponseType = responseType;
        Kind = kind;
        Method = method;
        Route = route;
        Group = group;
        Name = name;
        RequiresFormBinding = requiresFormBinding;
    }

    public INamedTypeSymbol HandlerType { get; }

    public INamedTypeSymbol RequestType { get; }

    public ITypeSymbol ResponseType { get; }

    public HttpHandlerKind Kind { get; }

    public string Method { get; }

    public string Route { get; }

    public string? Group { get; }

    public string? Name { get; }

    /// <summary>True when the request binds from a multipart form (IFormFile / [FromForm]).
    /// Drives antiforgery suppression and the OpenAPI content type on the mapped endpoint.</summary>
    public bool RequiresFormBinding { get; }
}

internal enum HttpHandlerKind
{
    Request = 1,
    Stream = 2,
}
