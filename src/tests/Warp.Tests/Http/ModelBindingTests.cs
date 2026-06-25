using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Shouldly;
using Warp.Http;
using Warp.Tests.TestData;

namespace Warp.Tests.Http;

/// <summary>
/// Verifies that Warp.Http's generator wires requests to ASP.NET Minimal API correctly
/// across binding scenarios. We're not testing ASP.NET's binders themselves — we're
/// testing that our generated <see cref="WarpHttpDelegate"/> shape (WholeBody /
/// AsParameters / Mixed) lets ASP.NET do its job.
/// </summary>
[Trait("Category", "NoDb")]
public sealed class ModelBindingTests
{
    [TimedFact]
    public async Task BoolQuery_BindsTrue()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());
        var resp = await app.Client.GetAsync(new Uri("/api/binding/bool?Active=true", UriKind.Relative));
        var body = await resp.Content.ReadFromJsonAsync<BindingBoolResponse>();
        body!.Active.ShouldBeTrue();
    }

    [TimedFact]
    public async Task BoolQuery_BindsFalse()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());
        var resp = await app.Client.GetAsync(new Uri("/api/binding/bool?Active=false", UriKind.Relative));
        var body = await resp.Content.ReadFromJsonAsync<BindingBoolResponse>();
        body!.Active.ShouldBeFalse();
    }

    [TimedFact]
    public async Task BoolQuery_IsCaseInsensitive()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());
        var resp = await app.Client.GetAsync(new Uri("/api/binding/bool?Active=TRUE", UriKind.Relative));
        var body = await resp.Content.ReadFromJsonAsync<BindingBoolResponse>();
        body!.Active.ShouldBeTrue();
    }

    [TimedFact]
    public async Task BoolQuery_RejectsNonBoolString_With400()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        // ASP.NET's bool TryParse only accepts "true"/"false" — "1" is not auto-coerced.
        // Documenting this contract: users get 400, not silent false.
        var resp = await app.Client.GetAsync(new Uri("/api/binding/bool?Active=1", UriKind.Relative));

        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [TimedFact]
    public async Task StringArrayQuery_BindsRepeatedKeys()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());
        var resp = await app.Client.GetAsync(new Uri("/api/binding/strings?Tags=alpha&Tags=beta&Tags=gamma", UriKind.Relative));
        var body = await resp.Content.ReadFromJsonAsync<BindingStringArrayResponse>();
        body!.Tags.ShouldBe(["alpha", "beta", "gamma"]);
    }

    [TimedFact]
    public async Task StringArrayQuery_SingleValueProducesSingleElement()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());
        var resp = await app.Client.GetAsync(new Uri("/api/binding/strings?Tags=alpha", UriKind.Relative));
        var body = await resp.Content.ReadFromJsonAsync<BindingStringArrayResponse>();
        body!.Tags.ShouldBe(["alpha"]);
    }

    [TimedFact]
    public async Task StringArrayQuery_MissingProducesEmptyArray()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());
        var resp = await app.Client.GetAsync(new Uri("/api/binding/strings", UriKind.Relative));
        var body = await resp.Content.ReadFromJsonAsync<BindingStringArrayResponse>();
        body!.Tags.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task IntArrayQuery_BindsAndParsesEachValue()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());
        var resp = await app.Client.GetAsync(new Uri("/api/binding/ints?Ids=1&Ids=2&Ids=3", UriKind.Relative));
        var body = await resp.Content.ReadFromJsonAsync<BindingIntArrayResponse>();
        body!.Ids.ShouldBe([1, 2, 3]);
    }

    [TimedFact]
    public async Task IntArrayQuery_RejectsNonInt_With400()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());
        var resp = await app.Client.GetAsync(new Uri("/api/binding/ints?Ids=1&Ids=abc&Ids=3", UriKind.Relative));
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [TimedFact]
    public async Task NullableInt_PresentBindsValue()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());
        var resp = await app.Client.GetAsync(new Uri("/api/binding/nullable-int?Page=42", UriKind.Relative));
        var body = await resp.Content.ReadFromJsonAsync<BindingNullableIntResponse>();
        body!.HasValue.ShouldBeTrue();
        body.Value.ShouldBe(42);
    }

    [TimedFact]
    public async Task NullableInt_AbsentBindsNull()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());
        var resp = await app.Client.GetAsync(new Uri("/api/binding/nullable-int", UriKind.Relative));
        var body = await resp.Content.ReadFromJsonAsync<BindingNullableIntResponse>();
        body!.HasValue.ShouldBeFalse();
    }

    [TimedFact]
    public async Task MixedSources_BindsRouteAndQueryAndHeader()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        var id = Guid.NewGuid();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/binding/mixed/" + id + "?Name=alice");
        req.Headers.Add("X-Trace-Id", "trace-123");
        var resp = await app.Client.SendAsync(req);

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<BindingMixedSourcesResponse>();
        body!.Id.ShouldBe(id);
        body.Name.ShouldBe("alice");
        body.TraceId.ShouldBe("trace-123");
    }

    [TimedFact]
    public async Task UnattributedPropertyOnGet_BindsFromRouteWhenNameMatchesToken()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());
        var id = Guid.NewGuid();
        var resp = await app.Client.GetAsync(new Uri("/api/binding/route-by-name/" + id, UriKind.Relative));
        var body = await resp.Content.ReadFromJsonAsync<BindingRouteByNameResponse>();
        body!.Id.ShouldBe(id);
    }

    [TimedFact]
    public async Task GuidRouteConstraint_RejectsNonGuidWith404()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        // Route template `/api/binding/constraint-guid/{id:guid}` — non-GUIDs don't match,
        // so ASP.NET returns 404 (route doesn't match) rather than 400 (route matched but bind failed).
        var resp = await app.Client.GetAsync(new Uri("/api/binding/constraint-guid/not-a-guid", UriKind.Relative));

        resp.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [TimedFact]
    public async Task GuidRouteConstraint_AcceptsValidGuid()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        var id = Guid.NewGuid();
        var resp = await app.Client.GetAsync(new Uri("/api/binding/constraint-guid/" + id, UriKind.Relative));

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TimedFact]
    public async Task IntRouteConstraint_RejectsNonIntWith404()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());
        var resp = await app.Client.GetAsync(new Uri("/api/binding/constraint-int/not-a-number", UriKind.Relative));
        resp.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [TimedFact]
    public async Task IntRouteConstraint_AcceptsValidInt()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());
        var resp = await app.Client.GetAsync(new Uri("/api/binding/constraint-int/2026", UriKind.Relative));
        var body = await resp.Content.ReadFromJsonAsync<BindingIntConstraintResponse>();
        body!.Year.ShouldBe(2026);
    }

    [TimedFact]
    public async Task BodyOnlyClass_RoutesThroughMixedShapeAndDeserializesBody()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());
        var resp = await app.Client.PostAsJsonAsync("/api/binding/body-only", new { Tag = "hello" });
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<BodyOnlyResponse>();
        body!.Tag.ShouldBe("hello");
    }

    [TimedFact]
    public async Task EmptyRecordPostCommand_BindsWithoutBody()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        // A zero-member command on a body verb (e.g. logout) must not require a request body.
        // Regression: it was classified WholeBody and emitted a required [FromBody], so a bodyless
        // POST returned 400 before the handler ran.
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        var resp = await app.Client.SendAsync(req);

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<string>();
        body.ShouldBe("logged-out");
    }

    [TimedFact]
    public async Task EmptyRecordPostCommand_BindsWithExplicitEmptyJsonBody()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        // The AsParameters shape also tolerates a present (but empty) JSON body — clients that
        // always send Content-Type: application/json with {} must keep working.
        var resp = await app.Client.PostAsJsonAsync("/api/auth/logout", new { });

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TimedFact]
    public async Task InitOnlyProperties_BindFromQueryViaAsParameters()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());
        var resp = await app.Client.GetAsync(new Uri("/api/binding/init-only-query?Name=alice&Count=7", UriKind.Relative));
        var body = await resp.Content.ReadFromJsonAsync<BindingInitOnlyResponse>();
        body!.Name.ShouldBe("alice");
        body.Count.ShouldBe(7);
    }
}
