using Microsoft.AspNetCore.Builder;

namespace Warp.Dashboard;

/// <summary>
/// Returned by <c>MapWarpDashboard</c>. Applies endpoint conventions across every dashboard surface — the SPA
/// shell, the REST API and the SignalR hub — so one <c>RequireAuthorization(...)</c> gates all of them.
/// </summary>
public sealed class WarpDashboardEndpointConventionBuilder : IEndpointConventionBuilder
{
    private readonly CompositeEndpointConventionBuilder _all;

    internal WarpDashboardEndpointConventionBuilder(IServiceProvider services, IEndpointConventionBuilder shell, IEndpointConventionBuilder api)
    {
        Services = services;
        Api = api;
        _all = new CompositeEndpointConventionBuilder([shell, api]);
    }

    /// <summary>The host's services, so the <c>Require*</c> conventions can verify their registrations.</summary>
    internal IServiceProvider Services { get; }

    /// <summary>
    /// The REST API group, the bare API root, and — when dashboard push is enabled — the SignalR hub. Held
    /// separately because the built-in login gates these while leaving the SPA shell anonymous.
    /// </summary>
    internal IEndpointConventionBuilder Api { get; }

    public void Add(Action<EndpointBuilder> convention) => _all.Add(convention);

    public void Finally(Action<EndpointBuilder> finallyConvention) => _all.Finally(finallyConvention);
}
