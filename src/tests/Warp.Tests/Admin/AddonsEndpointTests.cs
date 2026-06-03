using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;
using Warp.Core.Concurrency;
using Warp.Core.RateLimit;
using Warp.Core.Sagas;
using Warp.Core.Services;
using Warp.UI.DashboardPush;
using Warp.UI.Endpoints;
using Warp.UI.UIMiddleware;

namespace Warp.Tests.Admin;

[Trait("Category", "NoDb")]
public class AddonsEndpointTests
{
    private static async Task<(WebApplication App, HttpClient Client)> CreateApp(
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        // ValidateScopes=true catches the singleton-captures-scoped misuse pattern called out
        // in feedback_dbcontext_options_scoped — production registers addon managers scoped,
        // so the test fakes match that lifetime.
        builder.WebHost.UseDefaultServiceProvider(o => o.ValidateScopes = true);
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.MapWarpApiEndpoints(new WarpUIOptions(), []);

        await app.StartAsync(CancellationToken.None);
        return (app, app.GetTestClient());
    }

    [TimedFact]
    public async Task GetAddons_NoAddonsRegistered_AllFalse()
    {
        var (app, client) = await CreateApp();
        try
        {
            var response = await client.GetAsync("/warp/api/addons", CancellationToken.None);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var info = await response.Content.ReadFromJsonAsync<WarpAddonsInfo>();
            info.ShouldNotBeNull();
            info!.Concurrency.ShouldBeFalse();
            info.RateLimits.ShouldBeFalse();
            info.Push.ShouldBeFalse();
            info.Sagas.ShouldBeFalse();
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task GetAddons_AllAddonsRegistered_AllTrue()
    {
        var (app, client) = await CreateApp(services =>
        {
            services.AddScoped(_ => Mock.Of<IConcurrencyLimitManager>());
            services.AddScoped(_ => Mock.Of<IRateLimitManager>());
            services.AddSingleton<IDashboardPushMarker>(new DashboardPushMarker());
            services.AddScoped(_ => Mock.Of<ISagaQueryService>());
        });
        try
        {
            var response = await client.GetAsync("/warp/api/addons", CancellationToken.None);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var info = await response.Content.ReadFromJsonAsync<WarpAddonsInfo>();
            info.ShouldNotBeNull();
            info!.Concurrency.ShouldBeTrue();
            info.RateLimits.ShouldBeTrue();
            info.Push.ShouldBeTrue();
            info.Sagas.ShouldBeTrue();
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task GetAddons_EmitsCamelCaseJson()
    {
        // The bundled TS client decodes `concurrency`, `rateLimits`, `push`, `sagas` — lock the
        // wire shape so a future global JSON-options change can't silently break the dashboard.
        var (app, client) = await CreateApp();
        try
        {
            var response = await client.GetAsync("/warp/api/addons", CancellationToken.None);
            var body = await response.Content.ReadAsStringAsync(CancellationToken.None);

            body.ShouldContain("\"concurrency\":");
            body.ShouldContain("\"push\":");
            body.ShouldContain("\"rateLimits\":");
            body.ShouldContain("\"sagas\":");
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedTheory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, true, true)]
    public async Task GetAddons_PerAddonPermutation_FlagsMatchRegistration(
        bool concurrency,
        bool rateLimits,
        bool push,
        bool sagas)
    {
        var (app, client) = await CreateApp(svc =>
        {
            if (concurrency)
            {
                svc.AddScoped(_ => Mock.Of<IConcurrencyLimitManager>());
            }

            if (rateLimits)
            {
                svc.AddScoped(_ => Mock.Of<IRateLimitManager>());
            }

            if (push)
            {
                svc.AddSingleton<IDashboardPushMarker>(new DashboardPushMarker());
            }

            if (sagas)
            {
                svc.AddScoped(_ => Mock.Of<ISagaQueryService>());
            }
        });
        try
        {
            var response = await client.GetAsync("/warp/api/addons", CancellationToken.None);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var info = await response.Content.ReadFromJsonAsync<WarpAddonsInfo>();
            info.ShouldNotBeNull();
            info!.Concurrency.ShouldBe(concurrency);
            info.RateLimits.ShouldBe(rateLimits);
            info.Push.ShouldBe(push);
            info.Sagas.ShouldBe(sagas);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }
}
