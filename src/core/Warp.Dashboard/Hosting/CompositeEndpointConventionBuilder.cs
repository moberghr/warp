using Microsoft.AspNetCore.Builder;

namespace Warp.Dashboard;

/// <summary>
/// Fans an endpoint convention out to several builders, so a group of separately-mapped endpoints can be
/// configured as one.
/// </summary>
internal sealed class CompositeEndpointConventionBuilder : IEndpointConventionBuilder
{
    private readonly IReadOnlyList<IEndpointConventionBuilder> _builders;

    public CompositeEndpointConventionBuilder(IReadOnlyList<IEndpointConventionBuilder> builders) => _builders = builders;

    public void Add(Action<EndpointBuilder> convention)
    {
        foreach (var builder in _builders)
        {
            builder.Add(convention);
        }
    }

    public void Finally(Action<EndpointBuilder> finallyConvention)
    {
        foreach (var builder in _builders)
        {
            builder.Finally(finallyConvention);
        }
    }
}
