using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Shouldly;
using Warp.Http;
using Warp.Tests.TestData;

namespace Warp.Tests.Http;

[Trait("Category", "NoDb")]
public sealed class BindingTests
{
    [TimedFact]
    public async Task RouteValueBindsIntoMatchingCtorParameter()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        var id = Guid.NewGuid();
        var resp = await app.Client.GetAsync(new Uri("/api/orders/" + id, UriKind.Relative));

        resp.IsSuccessStatusCode.ShouldBeTrue();
        var body = await resp.Content.ReadFromJsonAsync<OrderDto>();
        body!.Id.ShouldBe(id);
    }

    [TimedFact]
    public async Task QueryStringBindsByName_OnGetVerb()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        var resp = await app.Client.GetAsync(new Uri("/api/orders?Page=2&PageSize=20", UriKind.Relative));

        var body = await resp.Content.ReadFromJsonAsync<ListOrdersResponse>();
        body!.Page.ShouldBe(2);
        body.PageSize.ShouldBe(20);
    }

    [TimedFact]
    public async Task HeaderBindsByExplicitName()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
        req.Headers.Add("X-User-Id", "alice");

        var resp = await app.Client.SendAsync(req);

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<string>();
        body.ShouldBe("alice");
    }

    [TimedFact]
    public async Task HeaderLookupIsCaseInsensitive()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
        req.Headers.Add("x-user-id", "bob");

        var resp = await app.Client.SendAsync(req);

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TimedFact]
    public async Task RouteAndBodyMixed_BodyDeserializesAroundRouteParam()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        var tenant = Guid.NewGuid();
        var resp = await app.Client.PostAsJsonAsync(
            "/api/orders/" + tenant + "/submit",
            new { Description = "from body" });

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<OrderDto>();
        body!.Id.ShouldBe(tenant);
        body.Status.ShouldBe("from body");
    }

    [TimedFact]
    public async Task MissingRequiredHeader_ReturnsBadRequest()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        // /api/whoami requires X-User-Id; without it we expect 400.
        var resp = await app.Client.GetAsync(new Uri("/api/whoami", UriKind.Relative));

        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [TimedFact]
    public async Task ClassWithInitOnlyProperties_BindsViaInitSetters()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        var resp = await app.Client.PostAsJsonAsync(
            "/api/init-only",
            new { Name = "init-set", Count = 7 });

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<InitOnlyResponse>();
        body!.Name.ShouldBe("init-set");
        body.Count.ShouldBe(7);
    }

    [TimedFact]
    public async Task RouteAndBodyScalar_BindsBothInMixedShape()
    {
        // POST with [FromRoute] route param + a single unattributed scalar body param —
        // exercises the Mixed shape with one body target end-to-end. Regression coverage
        // for #208. Minimal API binds a scalar [FromBody] from a raw JSON string body.
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        var id = Guid.NewGuid();
        using var content = new StringContent("\"admin\"", Encoding.UTF8, "application/json");
        var resp = await app.Client.PostAsync("/api/users/" + id + "/promote", content);

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<PromoteUserResponse>();
        body!.Id.ShouldBe(id);
        body.NewRole.ShouldBe("admin");
    }

    [TimedFact]
    public async Task ClassWithSettableProperties_BindsViaPropertySetters()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        var id = Guid.NewGuid();
        var resp = await app.Client.PutAsJsonAsync(
            "/api/products/" + id,
            new { Name = "from-prop", Price = 7.5m });

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<ProductDto>();
        body!.Id.ShouldBe(id);
        body.Name.ShouldBe("from-prop");
        body.Price.ShouldBe(7.5m);
    }
}
