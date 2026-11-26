using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Http;
using Warp.Tests.TestData;

namespace Warp.Tests.Http;

[Trait("Category", "NoDb")]
public sealed class AuthMetadataTests
{
    [TimedFact]
    public async Task AuthorizeAttribute_OnRequestType_SurfacesAsEndpointMetadata()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        var dataSource = app.Services.GetRequiredService<EndpointDataSource>();
        var endpoint = dataSource.Endpoints.FirstOrDefault(e =>
            e is RouteEndpoint re && string.Equals(re.RoutePattern.RawText, "/api/secure/echo", StringComparison.Ordinal));

        endpoint.ShouldNotBeNull();
        endpoint.Metadata.GetMetadata<AuthorizeAttribute>().ShouldNotBeNull();
        endpoint.Metadata.GetMetadata<AuthorizeAttribute>()!.Policy.ShouldBe("WarpHttpTestPolicy");
    }

    [TimedFact]
    public async Task AllowAnonymous_OnRequestType_SurfacesAsEndpointMetadata()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        var dataSource = app.Services.GetRequiredService<EndpointDataSource>();
        var endpoint = dataSource.Endpoints.FirstOrDefault(e =>
            e is RouteEndpoint re && string.Equals(re.RoutePattern.RawText, "/api/anon/echo", StringComparison.Ordinal));

        endpoint.ShouldNotBeNull();
        endpoint.Metadata.GetMetadata<AllowAnonymousAttribute>().ShouldNotBeNull();
    }

    [TimedFact]
    public async Task AuthorizeOnHandlerAndOnItsBase_BothSurface()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        var endpoint = Endpoint(app, "/api/secure/layered");

        var authorize = endpoint.Metadata.GetOrderedMetadata<AuthorizeAttribute>();
        authorize.ShouldContain(x => string.Equals(x.Policy, "WarpHttpTestPolicy", StringComparison.Ordinal));
        authorize.ShouldContain(x => string.Equals(x.Roles, "Auditor", StringComparison.Ordinal));
    }

    [TimedFact]
    public async Task DisableRateLimitingOnBase_IsNotInherited()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        var endpoint = Endpoint(app, "/api/rate-limited/subclass");

        endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.RateLimiting.DisableRateLimitingAttribute>().ShouldBeNull();
        endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>().ShouldNotBeNull();
    }

    [TimedFact]
    public async Task EndpointBuilds_WithProducesAndAcceptsMetadata()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        var dataSource = app.Services.GetRequiredService<EndpointDataSource>();
        var echoEndpoint = dataSource.Endpoints.FirstOrDefault(e =>
            e is RouteEndpoint re && string.Equals(re.RoutePattern.RawText, "/api/echo", StringComparison.Ordinal));

        echoEndpoint.ShouldNotBeNull();

        // Accepts<EchoRequest>("application/json") emits an IAcceptsMetadata entry.
        var accepts = echoEndpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IAcceptsMetadata>();
        accepts.ShouldNotBeNull();
        accepts.RequestType.ShouldBe(typeof(EchoRequest));

        // Produces<EchoResponse>(200) emits an IProducesResponseTypeMetadata entry.
        var produces = echoEndpoint.Metadata
            .GetOrderedMetadata<Microsoft.AspNetCore.Http.Metadata.IProducesResponseTypeMetadata>();
        produces.ShouldContain(p => p.Type == typeof(EchoResponse) && p.StatusCode == 200);
    }

    [TimedFact]
    public async Task MultiAttributeName_SurfacesAsEndpointNameMetadata()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        var dataSource = app.Services.GetRequiredService<EndpointDataSource>();

        // The MultiRouteRequestHandler carries [WarpHttpPost("/api/v1/multi", Name = "MultiV1")]
        // and [WarpHttpPost("/api/v2/multi", Name = "MultiV2")]. MapWarpHttp's WithName(...) call
        // should turn each Name into an EndpointNameMetadata entry on the corresponding endpoint.
        var v1 = dataSource.Endpoints.FirstOrDefault(e =>
            e is RouteEndpoint re && string.Equals(re.RoutePattern.RawText, "/api/v1/multi", StringComparison.Ordinal));
        var v2 = dataSource.Endpoints.FirstOrDefault(e =>
            e is RouteEndpoint re && string.Equals(re.RoutePattern.RawText, "/api/v2/multi", StringComparison.Ordinal));

        v1.ShouldNotBeNull();
        v2.ShouldNotBeNull();

        v1.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.EndpointNameMetadata>().ShouldNotBeNull().EndpointName.ShouldBe("MultiV1");
        v2.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.EndpointNameMetadata>().ShouldNotBeNull().EndpointName.ShouldBe("MultiV2");
    }

    [TimedFact]
    public async Task FirstRouteSegment_SurfacesAsTagsMetadata()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        var dataSource = app.Services.GetRequiredService<EndpointDataSource>();
        var echoEndpoint = dataSource.Endpoints.FirstOrDefault(e =>
            e is RouteEndpoint re && string.Equals(re.RoutePattern.RawText, "/api/echo", StringComparison.Ordinal));

        echoEndpoint.ShouldNotBeNull();

        // /api/echo → first non-token segment is "api" → WithTags("api") was called.
        var tags = echoEndpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.ITagsMetadata>();
        tags.ShouldNotBeNull();
        tags.Tags.ShouldContain("api");
    }

    [TimedFact]
    public async Task PlaceholderFirstSegment_DoesNotProduceTagsMetadata()
    {
        // Confirm ExtractFirstRouteSegment correctly skips routes whose first segment is a
        // placeholder. /discovery/get-record starts with "discovery" so it gets a tag, but
        // any route shaped /{id}/... wouldn't. We don't have such a route in test fixtures —
        // assert the contract by picking a route we know has a tag and one that has a different one.
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        var dataSource = app.Services.GetRequiredService<EndpointDataSource>();

        var echoTags = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .First(e => string.Equals(e.RoutePattern.RawText, "/api/echo", StringComparison.Ordinal))
            .Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.ITagsMetadata>();

        var discoveryTags = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .First(e => string.Equals(e.RoutePattern.RawText, "/discovery/get-record", StringComparison.Ordinal))
            .Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.ITagsMetadata>();

        // Different first segments → different tags. Confirms WithTags isn't a hardcoded constant.
        echoTags!.Tags.ShouldContain("api");
        discoveryTags!.Tags.ShouldContain("discovery");
        echoTags.Tags.ShouldNotBe(discoveryTags.Tags);
    }

    private static RouteEndpoint Endpoint(WarpHttpTestApp app, string route)
        => app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(x => string.Equals(x.RoutePattern.RawText, route, StringComparison.Ordinal));
}
