using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;
using Warp.Core;
using Warp.Core.Concurrency;
using Warp.Core.RateLimit;
using Warp.Core.Sagas;
using Warp.Core.Services;
using Warp.Dashboard;
using Warp.Dashboard.Endpoints;
using Warp.Dashboard.Push;

namespace Warp.Tests.Admin;

[Trait("Category", "NoDb")]
public class AddonsEndpointTests
{
    private static async Task<(WebApplication App, HttpClient Client)> CreateApp(
        Action<IServiceCollection>? configureServices = null,
        Action<WarpDashboardOptions>? configureDashboard = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        // ValidateScopes=true catches the singleton-captures-scoped misuse pattern called out
        // in feedback_dbcontext_options_scoped — production registers addon managers scoped,
        // so the test fakes match that lifetime.
        builder.WebHost.UseDefaultServiceProvider(o => o.ValidateScopes = true);
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        var dashboardOptions = new WarpDashboardOptions();
        configureDashboard?.Invoke(dashboardOptions);
        app.MapWarpApiEndpoints(dashboardOptions, []);

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

    /// <summary>
    /// Retry is the one flag with no marker behind it: retrying jobs are read out of <c>Job.Metadata</c>,
    /// which any dashboard can do. So it reports true with nothing registered, unlike every other flag.
    /// </summary>
    [TimedFact]
    public async Task GetAddons_NoDeclaration_RetryDefaultsTrue()
    {
        var (app, client) = await CreateApp();
        try
        {
            var info = await client.GetFromJsonAsync<WarpAddonsInfo>("/warp/api/addons", CancellationToken.None);

            info.ShouldNotBeNull();
            info!.Retry.ShouldBeTrue();
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    /// <summary>
    /// The point of the declaration: a dashboard-only host holds none of the addons, so marker detection
    /// hides pages over data that is in the database. An explicit <c>ShowX()</c> outranks the marker.
    /// </summary>
    [TimedFact]
    public async Task GetAddons_HostDeclaresSurfaces_OverridesMarkerDetection()
    {
        var (app, client) = await CreateApp(configureDashboard: o => o
            .ShowRetries(false)
            .ShowAdapters());
        try
        {
            var info = await client.GetFromJsonAsync<WarpAddonsInfo>("/warp/api/addons", CancellationToken.None);

            info.ShouldNotBeNull();

            // Declared off despite defaulting on.
            info!.Retry.ShouldBeFalse();

            // Declared on despite no IAdapterRecordingMarker here. Safe because AddWarp registers
            // IAdapterQueryService, so /api/adapters* serves data in a dashboard-only host.
            info.Adapters.ShouldBeTrue();

            // Undeclared surfaces keep auto-detecting, so nothing changes for hosts that never call ShowX().
            info.Sagas.ShouldBeFalse();
            info.Concurrency.ShouldBeFalse();
            info.Endpoints.ShouldBeFalse();
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    /// <summary>
    /// The browser re-derives liveness rather than re-asking, so it needs the same threshold the API
    /// answered with — otherwise it guesses, and a guess that disagrees renders one silent process red
    /// on one page and green on another.
    /// </summary>
    [TimedFact]
    public async Task GetAddons_ReportsTheConfiguredInstanceStaleGrace()
    {
        var (app, client) = await CreateApp(services =>
            services.Configure<WarpConfiguration>(o => o.ApplicationInstanceStaleGrace = TimeSpan.FromSeconds(90)));
        try
        {
            var info = await client.GetFromJsonAsync<WarpAddonsInfo>("/warp/api/addons", CancellationToken.None);

            info.ShouldNotBeNull();
            info!.InstanceStaleAfterSeconds.ShouldBe(90);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task GetAddons_UnconfiguredGrace_ReportsTheDefault()
    {
        var (app, client) = await CreateApp();
        try
        {
            var info = await client.GetFromJsonAsync<WarpAddonsInfo>("/warp/api/addons", CancellationToken.None);

            info.ShouldNotBeNull();
            info!.InstanceStaleAfterSeconds.ShouldBe((int)new WarpConfiguration().ApplicationInstanceStaleGrace.TotalSeconds);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    /// <summary>
    /// A sub-second grace truncates to zero, and a zero threshold reads every instance as stale the moment
    /// it arrives — the floor keeps a misconfiguration from painting a healthy cluster red.
    /// </summary>
    [TimedFact]
    public async Task GetAddons_SubSecondGrace_FloorsAtOneSecond()
    {
        var (app, client) = await CreateApp(services =>
            services.Configure<WarpConfiguration>(o => o.ApplicationInstanceStaleGrace = TimeSpan.FromMilliseconds(200)));
        try
        {
            var info = await client.GetFromJsonAsync<WarpAddonsInfo>("/warp/api/addons", CancellationToken.None);

            info.ShouldNotBeNull();
            info!.InstanceStaleAfterSeconds.ShouldBe(1);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }
}
