using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Warp.Http;

/// <summary>
/// Returned by <see cref="EndpointRouteBuilderExtensions.MapWarpHttp"/>. As an
/// <see cref="IEndpointConventionBuilder"/> it applies conventions to the endpoints that call mapped, so
/// <c>app.MapWarpHttp().RequireAuthorization()</c> gates every one of them. It is also the
/// <see cref="IEndpointRouteBuilder"/> the call was made on, so code written against the earlier return type
/// keeps compiling.
/// </summary>
public sealed class WarpHttpEndpointConventionBuilder : IEndpointConventionBuilder, IEndpointRouteBuilder
{
    private readonly IEndpointRouteBuilder _endpoints;
    private readonly List<IEndpointConventionBuilder> _mapped;

    internal WarpHttpEndpointConventionBuilder(IEndpointRouteBuilder endpoints, List<IEndpointConventionBuilder> mapped)
    {
        _endpoints = endpoints;
        _mapped = mapped;
    }

    /// <inheritdoc />
    public IServiceProvider ServiceProvider => _endpoints.ServiceProvider;

    /// <inheritdoc />
    public ICollection<EndpointDataSource> DataSources => _endpoints.DataSources;

    internal IEndpointRouteBuilder Inner => _endpoints;

    /// <inheritdoc />
    public void Add(Action<EndpointBuilder> convention)
    {
        foreach (var builder in _mapped)
        {
            builder.Add(convention);
        }
    }

    /// <inheritdoc />
    public void Finally(Action<EndpointBuilder> finallyConvention)
    {
        foreach (var builder in _mapped)
        {
            builder.Finally(finallyConvention);
        }
    }

    /// <inheritdoc />
    public IApplicationBuilder CreateApplicationBuilder() => _endpoints.CreateApplicationBuilder();
}
