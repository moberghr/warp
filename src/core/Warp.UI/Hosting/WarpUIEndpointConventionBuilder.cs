using Microsoft.AspNetCore.Builder;

namespace Warp.UI;

/// <summary>
/// Returned by <c>MapWarpUI</c>. Applies endpoint conventions across every dashboard surface — the SPA
/// shell, the REST API and the SignalR hub — so one <c>RequireAuthorization(...)</c> gates all of them.
/// </summary>
public sealed class WarpUIEndpointConventionBuilder : IEndpointConventionBuilder
{
    private readonly IEndpointConventionBuilder _all;

    internal WarpUIEndpointConventionBuilder(IServiceProvider services, IEndpointConventionBuilder shell, IEndpointConventionBuilder api)
    {
        Services = services;
        Shell = shell;
        Api = api;
        _all = new CompositeEndpointConventionBuilder([shell, api]);
    }

    /// <summary>The host's services, so the <c>Require*</c> conventions can verify their registrations.</summary>
    internal IServiceProvider Services { get; }

    /// <summary>The SPA shell endpoints (<c>{prefix}</c> and <c>{prefix}/{**path}</c>).</summary>
    internal IEndpointConventionBuilder Shell { get; }

    /// <summary>The REST API group and, when dashboard push is enabled, the SignalR hub.</summary>
    internal IEndpointConventionBuilder Api { get; }

    public void Add(Action<EndpointBuilder> convention) => _all.Add(convention);

    public void Finally(Action<EndpointBuilder> finallyConvention) => _all.Finally(finallyConvention);
}
