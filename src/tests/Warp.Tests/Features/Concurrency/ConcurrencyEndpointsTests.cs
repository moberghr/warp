using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core.Concurrency;
using Warp.Dashboard;
using Warp.Dashboard.Endpoints;
using Warp.Dashboard.Extensions;

namespace Warp.Tests.Features.Concurrency;

/// <summary>
/// Tests for the dashboard concurrency-limit endpoints. These run NoDb in-memory using
/// <see cref="TestServer"/>, registering a fake <see cref="IConcurrencyLimitManager"/> so the
/// endpoints can be exercised without a real database. The "addon not registered" case is
/// covered by spinning up a second app that omits <see cref="IConcurrencyLimitManager"/> from DI
/// and asserting every endpoint returns 404.
/// </summary>
[Trait("Category", "NoDb")]
public class ConcurrencyEndpointsTests
{
    private static async Task<(WebApplication App, HttpClient Client, FakeConcurrencyLimitManager Mgr)> CreateAppWithManager()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        var mgr = new FakeConcurrencyLimitManager();
        builder.Services.AddSingleton<IConcurrencyLimitManager>(mgr);

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
    public async Task Get_ConcurrencyList_ReturnsAllRows()
    {
        var (app, client, mgr) = await CreateAppWithManager();
        try
        {
            mgr.Limits["alpha"] = new ConcurrencyLimitInfo("alpha", 1, DateTime.UtcNow);
            mgr.Limits["bravo"] = new ConcurrencyLimitInfo("bravo", 5, DateTime.UtcNow);

            var response = await client.GetAsync("/warp/api/concurrency", Xunit.TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var list = await response.Content.ReadFromJsonAsync<List<ConcurrencyLimitInfo>>(Xunit.TestContext.Current.CancellationToken);
            list.ShouldNotBeNull();
            list.Count.ShouldBe(2);
            list.ShouldContain(x => x.Name == "alpha" && x.Limit == 1);
            list.ShouldContain(x => x.Name == "bravo" && x.Limit == 5);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task Get_ConcurrencyByName_ExistingName_Returns200()
    {
        var (app, client, mgr) = await CreateAppWithManager();
        try
        {
            mgr.Limits["payment-api"] = new ConcurrencyLimitInfo("payment-api", 7, DateTime.UtcNow);

            var response = await client.GetAsync("/warp/api/concurrency/payment-api", Xunit.TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var info = await response.Content.ReadFromJsonAsync<ConcurrencyLimitInfo>(Xunit.TestContext.Current.CancellationToken);
            info.ShouldNotBeNull();
            info.Name.ShouldBe("payment-api");
            info.Limit.ShouldBe(7);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task Get_ConcurrencyByName_MissingName_Returns404()
    {
        var (app, client, _) = await CreateAppWithManager();
        try
        {
            var response = await client.GetAsync("/warp/api/concurrency/missing", Xunit.TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task Post_Concurrency_ValidBody_Creates()
    {
        var (app, client, mgr) = await CreateAppWithManager();
        try
        {
            var body = new UpsertConcurrencyLimitRequest("payment-api", 10);

            var response = await client.PostAsJsonAsync("/warp/api/concurrency", body, Xunit.TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            mgr.Limits.ShouldContainKey("payment-api");
            mgr.Limits["payment-api"].Limit.ShouldBe(10);

            var info = await response.Content.ReadFromJsonAsync<ConcurrencyLimitInfo>(Xunit.TestContext.Current.CancellationToken);
            info.ShouldNotBeNull();
            info.Name.ShouldBe("payment-api");
            info.Limit.ShouldBe(10);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task Post_Concurrency_InvalidLimit_Returns400()
    {
        var (app, client, mgr) = await CreateAppWithManager();
        try
        {
            var body = new UpsertConcurrencyLimitRequest("payment-api", 0);

            var response = await client.PostAsJsonAsync("/warp/api/concurrency", body, Xunit.TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            mgr.Limits.ShouldNotContainKey("payment-api");
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task Post_Concurrency_EmptyName_Returns400()
    {
        var (app, client, mgr) = await CreateAppWithManager();
        try
        {
            var body = new UpsertConcurrencyLimitRequest(string.Empty, 5);

            var response = await client.PostAsJsonAsync("/warp/api/concurrency", body, Xunit.TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            mgr.Limits.ShouldBeEmpty();
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task Put_Concurrency_UpdatesExisting()
    {
        var (app, client, mgr) = await CreateAppWithManager();
        try
        {
            mgr.Limits["payment-api"] = new ConcurrencyLimitInfo("payment-api", 5, DateTime.UtcNow);
            var body = new UpdateConcurrencyLimitRequest(20);

            var response = await client.PutAsJsonAsync("/warp/api/concurrency/payment-api", body, Xunit.TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            mgr.Limits["payment-api"].Limit.ShouldBe(20);

            var info = await response.Content.ReadFromJsonAsync<ConcurrencyLimitInfo>(Xunit.TestContext.Current.CancellationToken);
            info.ShouldNotBeNull();
            info.Limit.ShouldBe(20);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task Put_Concurrency_InvalidLimit_Returns400()
    {
        var (app, client, _) = await CreateAppWithManager();
        try
        {
            var body = new UpdateConcurrencyLimitRequest(0);

            var response = await client.PutAsJsonAsync("/warp/api/concurrency/payment-api", body, Xunit.TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task Delete_Concurrency_ExistingName_Returns200()
    {
        var (app, client, mgr) = await CreateAppWithManager();
        try
        {
            mgr.Limits["payment-api"] = new ConcurrencyLimitInfo("payment-api", 5, DateTime.UtcNow);

            var response = await client.DeleteAsync("/warp/api/concurrency/payment-api", Xunit.TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            mgr.Limits.ShouldNotContainKey("payment-api");
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task Delete_Concurrency_MissingName_Returns404()
    {
        var (app, client, _) = await CreateAppWithManager();
        try
        {
            var response = await client.DeleteAsync("/warp/api/concurrency/missing", Xunit.TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task When_AddonNotRegistered_AllEndpointsReturn404()
    {
        var (app, client) = await CreateAppWithoutManager();
        try
        {
            var ct = Xunit.TestContext.Current.CancellationToken;

            var listResponse = await client.GetAsync("/warp/api/concurrency", ct);
            listResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);

            var getResponse = await client.GetAsync("/warp/api/concurrency/whatever", ct);
            getResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);

            var postResponse = await client.PostAsJsonAsync(
                "/warp/api/concurrency",
                new UpsertConcurrencyLimitRequest("k", 1),
                ct);
            postResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);

            var putResponse = await client.PutAsJsonAsync(
                "/warp/api/concurrency/k",
                new UpdateConcurrencyLimitRequest(1),
                ct);
            putResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);

            var deleteResponse = await client.DeleteAsync("/warp/api/concurrency/k", ct);
            deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    private sealed class FakeConcurrencyLimitManager : IConcurrencyLimitManager
    {
        public Dictionary<string, ConcurrencyLimitInfo> Limits { get; } = new(StringComparer.Ordinal);

        public Task AddOrUpdateLimit(string name, int limit, CancellationToken ct = default)
        {
            Limits[name] = new ConcurrencyLimitInfo(name, limit, DateTime.UtcNow);

            return Task.CompletedTask;
        }

        public Task<bool> RemoveLimit(string name, CancellationToken ct = default)
        {
            return Task.FromResult(Limits.Remove(name));
        }

        public Task<ConcurrencyLimitInfo?> GetLimit(string name, CancellationToken ct = default)
        {
            return Task.FromResult(Limits.TryGetValue(name, out var info) ? info : null);
        }

        public Task<IReadOnlyList<ConcurrencyLimitInfo>> ListLimits(CancellationToken ct = default)
        {
            IReadOnlyList<ConcurrencyLimitInfo> list = [.. Limits.Values.OrderBy(x => x.Name, StringComparer.Ordinal)];

            return Task.FromResult(list);
        }
    }
}
