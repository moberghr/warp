using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core.RateLimit;
using Warp.Dashboard;
using Warp.Dashboard.Endpoints;
using Warp.Dashboard.Extensions;

namespace Warp.Tests.Features.RateLimit;

/// <summary>
/// Tests for the dashboard rate-limit endpoints. Mirrors the concurrency-endpoint suite —
/// NoDb in-memory <see cref="TestServer"/>, fake <see cref="IRateLimitManager"/>. The
/// "addon not registered" case is covered by a second app that omits the manager.
/// </summary>
[Trait("Category", "NoDb")]
public class RateLimitEndpointsTests
{
    private static async Task<(WebApplication App, HttpClient Client, FakeRateLimitManager Mgr)> CreateAppWithManager()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        var mgr = new FakeRateLimitManager();
        builder.Services.AddSingleton<IRateLimitManager>(mgr);

        var app = builder.Build();
        var options = new WarpDashboardOptions();
        var extensions = new List<IWarpDashboardExtension>();
        app.MapWarpApiEndpoints(options, extensions);

        await app.StartAsync(CancellationToken.None);

        return (app, app.GetTestClient(), mgr);
    }

    private static async Task<(WebApplication App, HttpClient Client)> CreateAppWithoutManager()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        var app = builder.Build();
        var options = new WarpDashboardOptions();
        var extensions = new List<IWarpDashboardExtension>();
        app.MapWarpApiEndpoints(options, extensions);

        await app.StartAsync(CancellationToken.None);

        return (app, app.GetTestClient());
    }

    [TimedFact]
    public async Task Get_RateLimitsList_ReturnsAllRows()
    {
        var (app, client, mgr) = await CreateAppWithManager();
        try
        {
            mgr.Limits["alpha"] = new RateLimitInfo("alpha", 10, 60, DateTime.UtcNow);
            mgr.Limits["bravo"] = new RateLimitInfo("bravo", 100, 30, DateTime.UtcNow);

            var response = await client.GetAsync("/warp/api/ratelimits", Xunit.TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var list = await response.Content.ReadFromJsonAsync<List<RateLimitInfo>>(Xunit.TestContext.Current.CancellationToken);
            list.ShouldNotBeNull();
            list.Count.ShouldBe(2);
            list.ShouldContain(x => x.Name == "alpha" && x.Count == 10 && x.WindowSeconds == 60);
            list.ShouldContain(x => x.Name == "bravo" && x.Count == 100 && x.WindowSeconds == 30);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task Get_RateLimitByName_Existing_Returns200()
    {
        var (app, client, mgr) = await CreateAppWithManager();
        try
        {
            mgr.Limits["external-api"] = new RateLimitInfo("external-api", 100, 60, DateTime.UtcNow);

            var response = await client.GetAsync("/warp/api/ratelimits/external-api", Xunit.TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var info = await response.Content.ReadFromJsonAsync<RateLimitInfo>(Xunit.TestContext.Current.CancellationToken);
            info.ShouldNotBeNull();
            info.Name.ShouldBe("external-api");
            info.Count.ShouldBe(100);
            info.WindowSeconds.ShouldBe(60);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task Get_RateLimitByName_Missing_Returns404()
    {
        var (app, client, _) = await CreateAppWithManager();
        try
        {
            var response = await client.GetAsync("/warp/api/ratelimits/missing", Xunit.TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task Post_RateLimit_ValidBody_Upserts()
    {
        var (app, client, mgr) = await CreateAppWithManager();
        try
        {
            var body = new UpsertRateLimitRequest("payment-api", 10, 60);
            var response = await client.PostAsJsonAsync("/warp/api/ratelimits", body, Xunit.TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            mgr.Limits.ShouldContainKey("payment-api");
            mgr.Limits["payment-api"].Count.ShouldBe(10);
            mgr.Limits["payment-api"].WindowSeconds.ShouldBe(60);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task Post_RateLimit_CountZero_Returns400()
    {
        var (app, client, _) = await CreateAppWithManager();
        try
        {
            var body = new UpsertRateLimitRequest("payment-api", 0, 60);
            var response = await client.PostAsJsonAsync("/warp/api/ratelimits", body, Xunit.TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task Post_RateLimit_WindowZero_Returns400()
    {
        var (app, client, _) = await CreateAppWithManager();
        try
        {
            var body = new UpsertRateLimitRequest("payment-api", 10, 0);
            var response = await client.PostAsJsonAsync("/warp/api/ratelimits", body, Xunit.TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task Post_RateLimit_NameBlank_Returns400()
    {
        var (app, client, _) = await CreateAppWithManager();
        try
        {
            var body = new UpsertRateLimitRequest(string.Empty, 5, 60);
            var response = await client.PostAsJsonAsync("/warp/api/ratelimits", body, Xunit.TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task Post_RateLimit_NameTooLong_Returns400()
    {
        var (app, client, _) = await CreateAppWithManager();
        try
        {
            var body = new UpsertRateLimitRequest(new string('x', 201), 5, 60);
            var response = await client.PostAsJsonAsync("/warp/api/ratelimits", body, Xunit.TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task Put_RateLimit_NameTooLong_Returns400()
    {
        var (app, client, _) = await CreateAppWithManager();
        try
        {
            var name = new string('x', 201);
            var body = new UpdateRateLimitRequest(5, 60);
            var response = await client.PutAsJsonAsync($"/warp/api/ratelimits/{name}", body, Xunit.TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task Put_RateLimit_ValidBody_Updates()
    {
        var (app, client, mgr) = await CreateAppWithManager();
        try
        {
            mgr.Limits["k"] = new RateLimitInfo("k", 5, 30, DateTime.UtcNow);

            var body = new UpdateRateLimitRequest(20, 60);
            var response = await client.PutAsJsonAsync("/warp/api/ratelimits/k", body, Xunit.TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            mgr.Limits["k"].Count.ShouldBe(20);
            mgr.Limits["k"].WindowSeconds.ShouldBe(60);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task Put_RateLimit_CountZero_Returns400()
    {
        var (app, client, _) = await CreateAppWithManager();
        try
        {
            var body = new UpdateRateLimitRequest(0, 60);
            var response = await client.PutAsJsonAsync("/warp/api/ratelimits/k", body, Xunit.TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task Delete_RateLimit_Existing_Returns200()
    {
        var (app, client, mgr) = await CreateAppWithManager();
        try
        {
            mgr.Limits["k"] = new RateLimitInfo("k", 5, 60, DateTime.UtcNow);

            var response = await client.DeleteAsync("/warp/api/ratelimits/k", Xunit.TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            mgr.Limits.ShouldNotContainKey("k");
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task Delete_RateLimit_Missing_Returns404()
    {
        var (app, client, _) = await CreateAppWithManager();
        try
        {
            var response = await client.DeleteAsync("/warp/api/ratelimits/missing", Xunit.TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task AllEndpoints_Return404_WhenManagerNotRegistered()
    {
        var (app, client) = await CreateAppWithoutManager();
        try
        {
            var ct = Xunit.TestContext.Current.CancellationToken;

            var listResponse = await client.GetAsync("/warp/api/ratelimits", ct);
            listResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);

            var getResponse = await client.GetAsync("/warp/api/ratelimits/whatever", ct);
            getResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);

            var postResponse = await client.PostAsJsonAsync(
                "/warp/api/ratelimits",
                new UpsertRateLimitRequest("k", 1, 60),
                ct);
            postResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);

            var putResponse = await client.PutAsJsonAsync(
                "/warp/api/ratelimits/k",
                new UpdateRateLimitRequest(1, 60),
                ct);
            putResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);

            var deleteResponse = await client.DeleteAsync("/warp/api/ratelimits/k", ct);
            deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    private sealed class FakeRateLimitManager : IRateLimitManager
    {
        public Dictionary<string, RateLimitInfo> Limits { get; } = new(StringComparer.Ordinal);

        public Task AddOrUpdateLimit(string name, int count, int windowSeconds, CancellationToken ct = default)
        {
            Limits[name] = new RateLimitInfo(name, count, windowSeconds, DateTime.UtcNow);

            return Task.CompletedTask;
        }

        public Task<bool> RemoveLimit(string name, CancellationToken ct = default)
        {
            return Task.FromResult(Limits.Remove(name));
        }

        public Task<RateLimitInfo?> GetLimit(string name, CancellationToken ct = default)
        {
            return Task.FromResult(Limits.TryGetValue(name, out var info) ? info : null);
        }

        public Task<IReadOnlyList<RateLimitInfo>> ListLimits(CancellationToken ct = default)
        {
            IReadOnlyList<RateLimitInfo> list = [.. Limits.Values.OrderBy(x => x.Name, StringComparer.Ordinal)];

            return Task.FromResult(list);
        }
    }
}
