namespace Warp.Http.Discovery;

/// <summary>
/// Describes a single HTTP endpoint generated from a handler class tagged with
/// <see cref="WarpHttpAttribute"/>. Populated at assembly-load time by
/// <c>[ModuleInitializer]</c> code emitted by <c>Warp.Http.SourceGenerator</c> and
/// consumed by <see cref="EndpointRouteBuilderExtensions.MapWarpHttp"/>.
/// </summary>
public sealed class HttpEndpointDescriptor
{
    public required string Method { get; init; }

    public required string Route { get; init; }

    public string? Group { get; init; }

    public string? Name { get; init; }

    /// <summary>
    /// The handler class that carries the <see cref="WarpHttpAttribute"/>.
    /// <see cref="EndpointRouteBuilderExtensions.MapWarpHttp"/> reflects on this type
    /// to surface <c>[Authorize]</c> / <c>[AllowAnonymous]</c> as endpoint metadata.
    /// </summary>
    public required Type HandlerType { get; init; }

    public required Type RequestType { get; init; }

    /// <summary>
    /// For <see cref="HandlerKind.Request"/>, the <c>TResponse</c> type argument of
    /// <c>IRequest&lt;TResponse&gt;</c>. For <see cref="HandlerKind.Stream"/>, the
    /// <c>TResponse</c> type argument of <c>IStreamRequest&lt;TResponse&gt;</c>
    /// (the per-item type, not the enclosing <see cref="IAsyncEnumerable{T}"/>).
    /// </summary>
    public required Type ResponseType { get; init; }

    public required HandlerKind Kind { get; init; }

    /// <summary>
    /// Strongly-typed <see cref="Delegate"/> that ASP.NET Minimal API binds via
    /// <c>MapMethods</c>. The delegate's signature exposes <c>HttpContext</c> plus
    /// the request type (with <c>[AsParameters]</c> when per-property binding is
    /// needed); ASP.NET parses route values, query strings, headers, body, etc.,
    /// before invoking the dispatch trampoline that calls <c>IMediator</c>.
    /// </summary>
    public required Delegate HandlerDelegate { get; init; }

    /// <summary>
    /// True when the request binds from a multipart form (an <c>IFormFile</c> member or a
    /// <c>[FromForm]</c> field). <see cref="EndpointRouteBuilderExtensions.MapWarpHttp"/> uses
    /// this to disable antiforgery on the endpoint and to declare the
    /// <c>multipart/form-data</c> content type instead of <c>application/json</c>.
    /// </summary>
    public bool RequiresFormBinding { get; init; }
}
