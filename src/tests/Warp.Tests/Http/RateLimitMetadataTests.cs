using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Http;

namespace Warp.Tests.Http;

[Trait("Category", "NoDb")]
public sealed class RateLimitMetadataTests
{
    [TimedFact]
    public async Task EnableRateLimitingAttribute_OnHandler_SurfacesAsEndpointMetadata()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        var dataSource = app.Services.GetRequiredService<EndpointDataSource>();
        var endpoint = dataSource.Endpoints.FirstOrDefault(e =>
            e is RouteEndpoint re && string.Equals(re.RoutePattern.RawText, "/api/rate-limited/echo", StringComparison.Ordinal));

        endpoint.ShouldNotBeNull();

        var meta = endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>();
        meta.ShouldNotBeNull();
        meta.PolicyName.ShouldBe("WarpHttpTestRateLimit");
    }
}
