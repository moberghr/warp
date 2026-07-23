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
using Warp.Core.Data.Queries;
using Warp.Core.Notifications;
using Warp.Core.Services;
using Warp.UI.Endpoints;
using Warp.UI.UIMiddleware;

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

    private static async Task<WarpAddonsInfo> GetAddonsAsync(string? applicationName)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.WebHost.UseDefaultServiceProvider(o => o.ValidateScopes = true);

        RegisterMinimalDependencies(builder.Services);
        builder.Services.AddWarp<TestContext>(opt => opt.ApplicationName = applicationName);

        var app = builder.Build();
        app.MapWarpApiEndpoints(new WarpUIOptions(), []);

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
