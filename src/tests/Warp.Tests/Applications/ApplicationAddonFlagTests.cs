using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Shouldly;
using Warp.Core;
using Warp.Core.Adapters;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Endpoints;
using Warp.Core.Notifications;
using Warp.Core.Services;
using Warp.Dashboard;
using Warp.Dashboard.Endpoints;

namespace Warp.Tests.Applications;

/// <summary>
/// Batch 7 coverage for the <c>WarpAddonsInfo.Applications</c> flag on <c>GET /api/addons</c>: true iff this
/// process set <c>WarpConfiguration.ApplicationName</c>, and <c>IApplicationQueryService</c> resolving in an
/// AddWarp-only (dashboard/publisher) process. NoDb — InMemory context + mocked provider scaffolding.
/// </summary>
[Trait("Category", "NoDb")]
public class ApplicationAddonFlagTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [TimedFact]
    public async Task Addons_ApplicationsFlag_TrueWhenApplicationNameSet()
    {
        var addons = await GetAddonsAsync(applicationName: "orders");

        addons.Applications.ShouldBeTrue();
    }

    [TimedFact]
    public async Task Addons_ApplicationsFlag_FalseWhenApplicationNameNull()
    {
        var addons = await GetAddonsAsync(applicationName: null);

        addons.Applications.ShouldBeFalse();
    }

    [TimedFact]
    public void ApplicationQueryService_ResolvesInAddWarpOnlyProcess()
    {
        var services = new ServiceCollection();
        RegisterMinimalDependencies(services);
        services.AddWarp<TestContext>();

        var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = sp.CreateScope();

        scope.ServiceProvider.GetRequiredService<IApplicationQueryService>().ShouldNotBeNull();
    }

    [TimedFact]
    public async Task Adapters_And_Endpoints_ApplicationQueryParam_ReturnsPerAppStats()
    {
        // Exercises the ?application= branch on GET /api/adapters and /api/endpoints over real HTTP (the
        // branch is otherwise never hit): with the param it returns the per-app stats shape; without it the
        // plain list. Seeds durable per-app + app-agnostic aggregates on the InMemory context.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.WebHost.UseDefaultServiceProvider(o => o.ValidateScopes = true);

        // Stable DB name captured once (not a fresh Guid per options-action invocation) so the seed scope
        // and the request scope share the same InMemory store.
        var dbName = $"addonflag-app-{Guid.NewGuid():N}";
        builder.Services.AddLogging();
        builder.Services.AddDbContext<TestContext>(o => o.UseInMemoryDatabase(dbName));
        builder.Services.AddSingleton(Mock.Of<IWarpSqlQueries<TestContext>>());
        builder.Services.AddSingleton(Mock.Of<IWarpLockProvider>());
        builder.Services.AddWarp<TestContext>();

        var app = builder.Build();
        app.MapWarpApiEndpoints(new WarpDashboardOptions(), []);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
            ctx.Set<AdapterDefinition>().Add(new AdapterDefinition { Name = "vendor", FirstSeenAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow });
            ctx.Set<Statistic>().Add(new Statistic { Key = AdapterCounterKeys.AppTotal("orders", "vendor", "success"), Value = 3 });
            ctx.Set<Statistic>().Add(new Statistic { Key = AdapterCounterKeys.Total("vendor", "success"), Value = 3 });
            ctx.Set<Statistic>().Add(new Statistic { Key = EndpointCounterKeys.AppTotal("orders", "GET /things", "success"), Value = 2 });
            ctx.Set<Statistic>().Add(new Statistic { Key = EndpointCounterKeys.Total("GET /things", "success"), Value = 2 });
            await ctx.SaveChangesAsync(CancellationToken.None);
        }

        await app.StartAsync(CancellationToken.None);
        var client = app.GetTestClient();

        try
        {
            // ?application= → per-app stats shape.
            var adapterStats = await client.GetFromJsonAsync<List<AdapterAppStatModel>>("/warp/api/adapters?application=orders", WebJson, CancellationToken.None);
            var vendorStat = adapterStats.ShouldNotBeNull().ShouldHaveSingleItem();
            vendorStat.Application.ShouldBe("orders");
            vendorStat.Adapter.ShouldBe("vendor");
            vendorStat.Calls.ShouldBe(3);

            var endpointStats = await client.GetFromJsonAsync<List<EndpointAppStatModel>>("/warp/api/endpoints?application=orders", WebJson, CancellationToken.None);
            var routeStat = endpointStats.ShouldNotBeNull().ShouldHaveSingleItem();
            routeStat.Application.ShouldBe("orders");
            routeStat.Route.ShouldBe("GET /things");

            // No param → plain list.
            var adapters = await client.GetFromJsonAsync<List<AdapterListItemModel>>("/warp/api/adapters", WebJson, CancellationToken.None);
            adapters.ShouldNotBeNull().ShouldContain(x => string.Equals(x.Name, "vendor", StringComparison.Ordinal));

            var endpoints = await client.GetFromJsonAsync<List<EndpointListItemModel>>("/warp/api/endpoints", WebJson, CancellationToken.None);
            endpoints.ShouldNotBeNull().ShouldContain(x => string.Equals(x.Route, "GET /things", StringComparison.Ordinal));
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    private static async Task<WarpAddonsInfo> GetAddonsAsync(string? applicationName)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.WebHost.UseDefaultServiceProvider(o => o.ValidateScopes = true);

        RegisterMinimalDependencies(builder.Services);
        builder.Services.AddWarp<TestContext>(opt => opt.ApplicationName = applicationName);

        var app = builder.Build();
        app.MapWarpApiEndpoints(new WarpDashboardOptions(), []);

        await app.StartAsync(CancellationToken.None);
        var client = app.GetTestClient();

        try
        {
            var response = await client.GetAsync("/warp/api/addons", CancellationToken.None);
            response.EnsureSuccessStatusCode();

            var addons = await response.Content.ReadFromJsonAsync<WarpAddonsInfo>(WebJson, CancellationToken.None);

            return addons.ShouldNotBeNull();
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    private static void RegisterMinimalDependencies(IServiceCollection services)
    {
        services.AddLogging();
        services.AddDbContext<TestContext>(o => o.UseInMemoryDatabase($"addonflag-{Guid.NewGuid():N}"));
        services.AddSingleton(Mock.Of<IWarpSqlQueries<TestContext>>());
        services.AddSingleton(Mock.Of<IWarpLockProvider>());
    }
}
