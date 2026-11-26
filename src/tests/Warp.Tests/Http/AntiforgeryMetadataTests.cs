using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Http;
using Warp.Tests.TestData;

namespace Warp.Tests.Http;

/// <summary>
/// Form-binding endpoints skip antiforgery validation by default, so programmatic clients can upload; the
/// host can keep it on for all of them, and a handler's own <c>[RequireAntiforgeryToken]</c> decides for its
/// endpoint either way.
/// </summary>
[Trait("Category", "NoDb")]
public sealed class AntiforgeryMetadataTests
{
    [TimedFact]
    public async Task FormEndpoint_ByDefault_DoesNotRequireAntiforgery()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        RequiresValidation(app, "/api/file-echo").ShouldBeFalse();
    }

    [TimedFact]
    public async Task FormEndpoint_WithOptionOn_RequiresAntiforgery()
    {
        // The test app already called AddWarpHttp() without options: a later call must still take effect.
        await using var app = await WarpHttpTestApp.StartAsync(
            configureServices: x => x.AddWarpHttp(y => y.ValidateAntiforgeryOnFormEndpoints = true),
            configureApp: a => a.MapWarpHttp());

        RequiresValidation(app, "/api/file-echo").ShouldBeTrue();
    }

    [TimedFact]
    public async Task FormEndpoint_HandlerRequiresAntiforgery_OverridesTheDefault()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        RequiresValidation(app, "/api/file-guarded").ShouldBeTrue();
    }

    private static bool RequiresValidation(WarpHttpTestApp app, string route)
    {
        var endpoint = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(x => string.Equals(x.RoutePattern.RawText, route, StringComparison.Ordinal));

        return endpoint.Metadata.GetMetadata<IAntiforgeryMetadata>().ShouldNotBeNull().RequiresValidation;
    }
}
