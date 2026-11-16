using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Shouldly;
using Warp.Core.Endpoints;
using Warp.Http.Observability;

namespace Warp.Tests.Endpoints;

/// <summary>
/// The identity a host endpoint is recorded under (method + route template + operation), built by the one
/// factory both opt-in paths share: the <c>WithWarpObservability</c> convention at startup and the middleware
/// for a <c>[WarpObserved]</c>-only endpoint. The template comes from the route pattern, never the request path.
/// </summary>
[Trait("Category", "NoDb")]
public class WarpEndpointIdentityFactoryTests
{
    [TimedFact]
    public void Create_SingleMethod_UsesUpperMethodAndConstraintFreeTemplate()
    {
        var identity = Create("/orders/{id:int}", [new HttpMethodMetadata(["get"])]);

        identity.Method.ShouldBe("GET");
        identity.RouteTemplate.ShouldBe("/orders/{id}");
    }

    [TimedFact]
    public void Create_AttributeRouteWithoutLeadingSlash_GetsLeadingSlash()
    {
        Create("api/orders/{id}", []).RouteTemplate.ShouldBe("/api/orders/{id}");
    }

    [TimedFact]
    public void Create_SeveralMethods_UsesAnyMethod()
    {
        Create("/orders", [new HttpMethodMetadata(["GET", "HEAD"])]).Method.ShouldBe("*");
    }

    [TimedFact]
    public void Create_NoMethodMetadata_UsesAnyMethod()
    {
        Create("/orders", []).Method.ShouldBe("*");
    }

    [TimedFact]
    public void Create_ExplicitOperation_WinsOverEndpointNameAndAction()
    {
        var identity = Create("/orders", [Action(), new EndpointNameMetadata("Named"), new WarpObservedAttribute { Operation = "Explicit" }]);

        identity.Operation.ShouldBe("Explicit");
    }

    [TimedFact]
    public void Create_InnermostExplicitOperation_Wins()
    {
        var identity = Create("/orders", [new WarpObservedAttribute { Operation = "Group" }, new WarpObservedAttribute { Operation = "Endpoint" }]);

        identity.Operation.ShouldBe("Endpoint");
    }

    [TimedFact]
    public void Create_EndpointName_WinsOverControllerAction()
    {
        Create("/orders", [Action(), new EndpointNameMetadata("Named")]).Operation.ShouldBe("Named");
    }

    [TimedFact]
    public void Create_RouteName_WinsOverControllerAction()
    {
        Create("/orders", [Action(), new RouteNameMetadata("RouteNamed")]).Operation.ShouldBe("RouteNamed");
    }

    [TimedFact]
    public void Create_UnnamedControllerAction_UsesControllerDotAction()
    {
        Create("/orders", [Action(), new WarpObservedAttribute()]).Operation.ShouldBe("Orders.Get");
    }

    [TimedFact]
    public void Create_NothingNamed_FallsBackToDisplayName()
    {
        Create("/orders", [], displayName: "HTTP: GET /orders").Operation.ShouldBe("HTTP: GET /orders");
    }

    [TimedFact]
    public void Create_OverlongOperation_TruncatedToColumnBound()
    {
        var identity = Create("/orders", [new WarpObservedAttribute { Operation = new string('x', 500) }]);

        identity.Operation.Length.ShouldBe(WarpEndpointIdentityFactory.MaxOperationLength);
    }

    [TimedFact]
    public void RenderTemplate_ConventionalRoute_SubstitutesRequiredValues()
    {
        var pattern = RoutePatternFactory.Parse(
            "{controller=Home}/{action=Index}/{id?}",
            defaults: null,
            parameterPolicies: null,
            requiredValues: new { controller = "Orders", action = "List" });

        WarpEndpointIdentityFactory.RenderTemplate(pattern).ShouldBe("/Orders/List/{id?}");
    }

    [TimedFact]
    public void RenderTemplate_CatchAllAndComplexSegment_RoundTrip()
    {
        WarpEndpointIdentityFactory.RenderTemplate(RoutePatternFactory.Parse("/files/{name}.{ext}/{**path}"))
            .ShouldBe("/files/{name}.{ext}/{**path}");
    }

    [TimedFact]
    public void TryCreateFromBuilder_OptedIn_BuildsIdentity()
    {
        var builder = Builder("/orders", new WarpObservedAttribute(), new HttpMethodMetadata(["POST"]));

        var identity = WarpEndpointIdentityFactory.TryCreate(builder).ShouldNotBeNull();

        identity.Method.ShouldBe("POST");
        identity.RouteTemplate.ShouldBe("/orders");
    }

    [TimedFact]
    public void TryCreateFromBuilder_ExistingIdentity_ReturnsNull()
    {
        var builder = Builder("/orders", new WarpObservedAttribute(), new WarpEndpointIdentity("GET", "/orders", "Warp"));

        WarpEndpointIdentityFactory.TryCreate(builder).ShouldBeNull();
    }

    [TimedFact]
    public void TryCreateFromBuilder_Excluded_ReturnsNull()
    {
        var builder = Builder("/orders", new ExcludeFromWarpObservabilityAttribute(), new WarpObservedAttribute());

        WarpEndpointIdentityFactory.TryCreate(builder).ShouldBeNull();
    }

    [TimedFact]
    public void TryCreateFromBuilder_NotOptedIn_ReturnsNull()
    {
        WarpEndpointIdentityFactory.TryCreate(Builder("/orders")).ShouldBeNull();
    }

    [TimedFact]
    public void TryCreateFromEndpoint_MarkerOnly_BuildsIdentity()
    {
        var endpoint = (RouteEndpoint)Builder("/orders/{id}", new WarpObservedAttribute(), new HttpMethodMetadata(["GET"])).Build();

        var identity = WarpEndpointIdentityFactory.TryCreate(endpoint).ShouldNotBeNull();

        identity.RouteTemplate.ShouldBe("/orders/{id}");
    }

    [TimedFact]
    public void TryCreateFromEndpoint_MarkerAndExclusion_ReturnsNull()
    {
        var endpoint = Builder("/orders", new WarpObservedAttribute(), new ExcludeFromWarpObservabilityAttribute()).Build();

        WarpEndpointIdentityFactory.TryCreate(endpoint).ShouldBeNull();
    }

    private static WarpEndpointIdentity Create(string route, object[] metadata, string? displayName = null)
        => WarpEndpointIdentityFactory.Create(RoutePatternFactory.Parse(route), metadata, displayName);

    private static ControllerActionDescriptor Action() => new() { ControllerName = "Orders", ActionName = "Get" };

    private static RouteEndpointBuilder Builder(string route, params object[] metadata)
    {
        var builder = new RouteEndpointBuilder(_ => Task.CompletedTask, RoutePatternFactory.Parse(route), 0);
        foreach (var item in metadata)
        {
            builder.Metadata.Add(item);
        }

        return builder;
    }
}
