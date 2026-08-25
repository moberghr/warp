using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Dashboard;
using Warp.Dashboard.Extensions;
using Warp.Dashboard.Extensions.Retry;
using XunitTestContext = Xunit.TestContext;

namespace Warp.Tests.Admin;

/// <summary>
/// Dashboard authorization tests. The dashboard is a set of routed endpoints, so gating it is ASP.NET's
/// job — these assert Warp hands the decision over correctly. The two that matter most are
/// <see cref="HostPolicy_AnonymousBrowserRequest_ChallengesToSignIn"/> and
/// <see cref="HostPolicy_AuthenticatedWithoutPermission_ForbidsInsteadOfBouncingToSignIn"/>: before this
/// became endpoint-based, a bool-returning filter collapsed both into one bare 401, and the
/// redirect option that was meant to fix the first one bounced the second between the dashboard and the
/// sign-in page forever.
/// </summary>
[Trait("Category", "NoDb")]
public class DashboardAuthTests
{
    // Mapped from a list captured at map time, so it needs no Warp services — the cheapest honest probe
    // of whether the API group carries the host's conventions.
    private const string ApiProbe = "/warp/api/extensions";
    private const string HostPolicy = "WarpDashboard";
    private const string AdminClaim = "warp-admin";
    private const string PermissiveScheme = "Permissive";

    [TimedFact]
    public async Task NoGate_ApiReturnsOk()
    {
        var (app, client) = await StartAsync();
        try
        {
            var response = await client.GetAsync(ApiProbe, XunitTestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task NoGate_ShellReturnsIndexHtml()
    {
        var (app, client) = await StartAsync();
        try
        {
            var response = await client.GetAsync("/warp", XunitTestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await response.Content.ReadAsStringAsync(XunitTestContext.Current.CancellationToken);
            body.ShouldContain("window.hasBuiltInLogin = false");
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task UnmatchedApiPath_ReturnsNotFound()
    {
        var (app, client) = await StartAsync();
        try
        {
            var response = await client.GetAsync("/warp/api/does-not-exist", XunitTestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task BuiltInLogin_ApiReturns401WithoutCookie()
    {
        var (app, client) = await StartAsync(AddBuiltInLogin, x => x.RequireWarpDashboardLogin());
        try
        {
            var response = await client.GetAsync(ApiProbe, XunitTestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task BuiltInLogin_ShellStaysAnonymousSoTheSpaCanRenderItsLoginPage()
    {
        var (app, client) = await StartAsync(AddBuiltInLogin, x => x.RequireWarpDashboardLogin());
        try
        {
            var response = await client.GetAsync("/warp", XunitTestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await response.Content.ReadAsStringAsync(XunitTestContext.Current.CancellationToken);
            body.ShouldContain("window.hasBuiltInLogin = true");
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task BuiltInLogin_ValidCredentials_Returns200AndSetsCookie()
    {
        var (app, client) = await StartAsync(AddBuiltInLogin, x => x.RequireWarpDashboardLogin());
        try
        {
            var response = await LoginAsync(client, "admin", "admin");

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            response.Headers.TryGetValues("Set-Cookie", out var cookies).ShouldBeTrue();
            cookies.ShouldContain(x => x.Contains(WarpDashboardDefaults.CookieName, StringComparison.Ordinal));
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task BuiltInLogin_CookieIsScopedToTheDashboardPrefix()
    {
        // The prefix isn't known until MapWarpDashboard runs, which is after DI is built, so the cookie path is
        // late-bound through a holder. Cookie options resolve on the first request, which is why that works
        // — a non-default prefix is what proves the holder is actually read.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddWarpDashboard().AddBuiltInLogin<TestCredentialValidator>();

        var app = builder.Build();
        app.MapWarpDashboard("/admin/warp").RequireWarpDashboardLogin();
        await app.StartAsync(XunitTestContext.Current.CancellationToken);

        var server = app.GetTestServer();
        using var client = new HttpClient(server.CreateHandler()) { BaseAddress = server.BaseAddress };
        try
        {
            using var form = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("username", "admin"),
                new KeyValuePair<string, string>("password", "admin"),
            ]);
            var login = await client.PostAsync("/admin/warp/api/auth/login", form, XunitTestContext.Current.CancellationToken);

            login.StatusCode.ShouldBe(HttpStatusCode.OK);
            var setCookie = login.Headers.GetValues("Set-Cookie").First();
            setCookie.ShouldContain("path=/admin/warp", Case.Insensitive);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task BuiltInLogin_InvalidCredentials_Returns401()
    {
        var (app, client) = await StartAsync(AddBuiltInLogin, x => x.RequireWarpDashboardLogin());
        try
        {
            var response = await LoginAsync(client, "admin", "wrong");

            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task BuiltInLogin_WithCookie_ApiReturnsOk()
    {
        var (app, client) = await StartAsync(AddBuiltInLogin, x => x.RequireWarpDashboardLogin());
        try
        {
            var login = await LoginAsync(client, "admin", "admin");
            login.StatusCode.ShouldBe(HttpStatusCode.OK);
            CarryCookie(client, login);

            var response = await client.GetAsync(ApiProbe, XunitTestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task BuiltInLogin_AfterLogout_ApiReturns401()
    {
        var (app, client) = await StartAsync(AddBuiltInLogin, x => x.RequireWarpDashboardLogin());
        try
        {
            var login = await LoginAsync(client, "admin", "admin");
            CarryCookie(client, login);

            using var empty = new ByteArrayContent([]);
            var logout = await client.PostAsync("/warp/api/auth/logout", empty, XunitTestContext.Current.CancellationToken);
            logout.StatusCode.ShouldBe(HttpStatusCode.OK);
            CarryCookie(client, logout);

            var response = await client.GetAsync(ApiProbe, XunitTestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task AuthStatus_WithoutBuiltInLogin_IsNotMapped()
    {
        // The probe is AllowAnonymous, so mapping it unconditionally would bypass every convention the host
        // applied and answer a constant "true" — wrong under a host policy, and a recon signal on a
        // dashboard meant to be loopback-only.
        var (app, client) = await StartAsync();
        try
        {
            var response = await client.GetAsync("/warp/api/auth/status", XunitTestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task AuthStatus_LocalRequestsOnly_IsNotReachableRemotely()
    {
        var (app, client) = await StartAsync(x => x.AddWarpDashboard(), x => x.RequireLocalRequests());
        try
        {
            var response = await client.GetAsync("/warp/api/auth/status", XunitTestContext.Current.CancellationToken);

            response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task AuthStatus_BuiltInLogin_NoCookie_ReturnsAuthenticatedFalse()
    {
        var (app, client) = await StartAsync(AddBuiltInLogin, x => x.RequireWarpDashboardLogin());
        try
        {
            var body = await GetAuthStatusAsync(client);

            body.ShouldBe("{\"authenticated\":false}");
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task AuthStatus_BuiltInLogin_WithCookie_ReturnsAuthenticatedTrue()
    {
        var (app, client) = await StartAsync(AddBuiltInLogin, x => x.RequireWarpDashboardLogin());
        try
        {
            var login = await LoginAsync(client, "admin", "admin");
            CarryCookie(client, login);

            var body = await GetAuthStatusAsync(client);

            body.ShouldBe("{\"authenticated\":true}");
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task HostPolicy_AnonymousBrowserRequest_ChallengesToSignIn()
    {
        var (app, client) = await StartAsync(AddHostIdentity, x => x.RequireAuthorization(HostPolicy), MapSignIn);
        try
        {
            var response = await client.GetAsync("/warp", XunitTestContext.Current.CancellationToken);

            // The whole point of the change: a signed-out browser is challenged through the host's own
            // scheme and reaches a sign-in, instead of being handed a bare 401 it cannot act on.
            response.StatusCode.ShouldBe(HttpStatusCode.Found);
            RedirectPath(response).ShouldBe("/login");
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task HostPolicy_AuthenticatedWithoutPermission_ForbidsInsteadOfBouncingToSignIn()
    {
        var (app, client) = await StartAsync(AddHostIdentity, x => x.RequireAuthorization(HostPolicy), MapSignIn);
        try
        {
            var signIn = await client.GetAsync("/signin?admin=false", XunitTestContext.Current.CancellationToken);
            CarryCookie(client, signIn);

            var response = await client.GetAsync("/warp", XunitTestContext.Current.CancellationToken);

            // Forbidden, not challenged. Sending this caller to the sign-in would return them in an
            // identical state — the bounce loop the old redirect option could not avoid.
            RedirectPath(response).ShouldNotBe("/login");
            RedirectPath(response).ShouldBe("/denied");
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task HostPolicy_AuthenticatedWithPermission_ReturnsShell()
    {
        var (app, client) = await StartAsync(AddHostIdentity, x => x.RequireAuthorization(HostPolicy), MapSignIn);
        try
        {
            var signIn = await client.GetAsync("/signin?admin=true", XunitTestContext.Current.CancellationToken);
            CarryCookie(client, signIn);

            var response = await client.GetAsync("/warp", XunitTestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task HostPolicy_AnonymousApiRequest_IsGated()
    {
        var (app, client) = await StartAsync(AddHostIdentity, x => x.RequireAuthorization(HostPolicy), MapSignIn);
        try
        {
            var response = await client.GetAsync(ApiProbe, XunitTestContext.Current.CancellationToken);

            response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task ApiResponses_CarryTheWarpApiMarkerHeader()
    {
        // The SPA uses this header to tell "the Warp API answered" from "something intercepted the call
        // and returned its own 200" — a sign-in redirect it followed. Without a positive marker the SPA
        // can only sniff content types, which misfires on extension endpoints that return HTML.
        var (app, client) = await StartAsync();
        try
        {
            var response = await client.GetAsync(ApiProbe, XunitTestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            response.Headers.GetValues("X-Warp-Api").ShouldContain("1");
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task ShellResponse_DoesNotCarryTheApiMarkerHeader()
    {
        var (app, client) = await StartAsync();
        try
        {
            var response = await client.GetAsync("/warp", XunitTestContext.Current.CancellationToken);

            response.Headers.Contains("X-Warp-Api").ShouldBeFalse();
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task StaticAssets_AreServed()
    {
        // The shell's catch-all route matches every asset path, and StaticFileMiddleware stands down once
        // routing has matched an endpoint — so the SPA's own bundle is served by the endpoint too. Without
        // this the dashboard renders blank: the HTML loads and every script under it 404s.
        var (app, client) = await StartAsync();
        try
        {
            var asset = await client.GetAsync("/warp/favicon.svg", XunitTestContext.Current.CancellationToken);

            asset.StatusCode.ShouldBe(HttpStatusCode.OK);
            asset.Content.Headers.ContentType!.MediaType.ShouldBe("image/svg+xml");
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task StaticAssets_DashboardExtensionJs_IsServedFromItsOwnAssembly()
    {
        // Extension assets live in a different assembly and resource namespace than the SPA bundle, so the
        // endpoint has to route /_ext/{name}/ to that extension's own provider.
        var (app, client) = await StartAsync(x => x.AddSingleton<IWarpDashboardExtension, RetryDashboardExtension>());
        try
        {
            var response = await client.GetAsync("/warp/_ext/retry/index.js", XunitTestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.ShouldBe("text/javascript");
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task StaticAssets_MissingFile_ReturnsNotFound()
    {
        var (app, client) = await StartAsync();
        try
        {
            var response = await client.GetAsync("/warp/assets/nope-00000000.js", XunitTestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task StaticAssets_AreGatedByTheHostConvention()
    {
        // Uniform gating is the upside of the endpoint owning its assets: one convention covers the shell,
        // its bundle, the API and the hub.
        var (app, client) = await StartAsync(x => x.AddWarpDashboard(), x => x.RequireLocalRequests());
        try
        {
            var response = await client.GetAsync("/warp/favicon.svg", XunitTestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task BuiltInLogin_PermissiveDefaultScheme_StillRequiresTheWarpCookie()
    {
        // The login policy pins its own scheme. Were it merely RequireAuthenticatedUser() against the
        // host's default, any always-succeeding scheme in the host — the demo app registers exactly such a
        // scheme for an unrelated reason — would leave the dashboard wide open.
        var (app, client) = await StartAsync(
            x =>
            {
                x.AddWarpDashboard().AddBuiltInLogin<TestCredentialValidator>();
                x.AddAuthentication(PermissiveScheme)
                    .AddScheme<AuthenticationSchemeOptions, PermissiveAuthHandler>(PermissiveScheme, _ => { });
            },
            x => x.RequireWarpDashboardLogin());
        try
        {
            var response = await client.GetAsync(ApiProbe, XunitTestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task LocalRequests_NonLoopbackCaller_Forbidden()
    {
        // TestServer leaves RemoteIpAddress null, which the policy treats as "cannot prove loopback".
        var (app, client) = await StartAsync(x => x.AddWarpDashboard(), x => x.RequireLocalRequests());
        try
        {
            var response = await client.GetAsync("/warp", XunitTestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task LocalRequests_LoopbackCaller_ReturnsOk()
    {
        var (app, client) = await StartAsync(x => x.AddWarpDashboard(), x => x.RequireLocalRequests(), StampLoopback);
        try
        {
            var response = await client.GetAsync("/warp", XunitTestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task RequireLocalRequests_WithoutAddWarpDashboard_ThrowsAtStartup()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();

        var mapped = app.MapWarpDashboard("/warp");

        Should.Throw<InvalidOperationException>(() => mapped.RequireLocalRequests())
            .Message.ShouldContain("AddWarpDashboard");
    }

    [TimedFact]
    public async Task RequireWarpDashboardLogin_WithoutBuiltInLogin_ThrowsAtStartup()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddWarpDashboard();
        await using var app = builder.Build();

        var mapped = app.MapWarpDashboard("/warp");

        Should.Throw<InvalidOperationException>(() => mapped.RequireWarpDashboardLogin())
            .Message.ShouldContain("AddBuiltInLogin");
    }

    [TimedFact]
    public async Task SpaDeepLink_WithDottedSegment_ReturnsShell()
    {
        // /jobs/by-type/{type} carries an assembly-qualified job type, and encodeURIComponent leaves dots
        // alone — so a refresh or bookmark of a real dashboard link lands here. Treating it as a file
        // request 404s a page that works on first navigation.
        var (app, client) = await StartAsync();
        try
        {
            var response = await client.GetAsync("/warp/jobs/by-type/MyApp.Jobs.SendEmail", XunitTestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await response.Content.ReadAsStringAsync(XunitTestContext.Current.CancellationToken);
            body.ShouldContain("window.basePath");
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task UnmatchedApiPath_MixedCase_ReturnsNotFound()
    {
        // Routing is case-insensitive, so the guard in front of it must be too — otherwise /warp/API/x
        // answers the HTML shell while its lowercase twin 404s.
        var (app, client) = await StartAsync();
        try
        {
            var response = await client.GetAsync("/warp/API/does-not-exist", XunitTestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task ApiRootPath_ReturnsNotFoundNotTheShell()
    {
        var (app, client) = await StartAsync();
        try
        {
            var response = await client.GetAsync("/warp/api", XunitTestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task UnmatchedApiPath_UnderGate_IsDeniedRatherThanAnonymously404d()
    {
        // An anonymous 404 for unregistered routes next to a 401 for registered ones is an enumeration
        // oracle: it tells an unauthenticated prober which addons are live. Unmatched API paths must
        // inherit the group's authorization and answer like every other API route.
        var (app, client) = await StartAsync(AddBuiltInLogin, x => x.RequireWarpDashboardLogin());
        try
        {
            var registered = await client.GetAsync(ApiProbe, XunitTestContext.Current.CancellationToken);
            var unregistered = await client.GetAsync("/warp/api/does-not-exist", XunitTestContext.Current.CancellationToken);

            registered.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            unregistered.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task RoutePrefix_WithoutLeadingSlash_IsNormalized()
    {
        // Unnormalized, the prefix is sliced off request paths by length, so every asset resolves to
        // garbage and the dashboard renders blank with no error.
        var (app, client) = await StartAsync(routePrefix: "warp");
        try
        {
            var shell = await client.GetAsync("/warp", XunitTestContext.Current.CancellationToken);
            var asset = await client.GetAsync("/warp/favicon.svg", XunitTestContext.Current.CancellationToken);

            shell.StatusCode.ShouldBe(HttpStatusCode.OK);
            asset.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await shell.Content.ReadAsStringAsync(XunitTestContext.Current.CancellationToken))
                .ShouldContain("window.basePath = \"/warp\"");
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task RoutePrefix_WithTrailingSlash_IsNormalized()
    {
        // "/warp/" built the pattern "/warp//{**path}" and died inside the route parser.
        var (app, client) = await StartAsync(routePrefix: "/warp/");
        try
        {
            var response = await client.GetAsync("/warp/favicon.svg", XunitTestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task RoutePrefix_ApplicationRoot_ThrowsWithAClearMessage()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();

        Should.Throw<ArgumentException>(() => app.MapWarpDashboard("/"))
            .Message.ShouldContain("RoutePrefix");
    }

    [TimedFact]
    public async Task BuiltInLogin_WithoutTheExplicitConvention_StillGatesTheApi()
    {
        // The half-migrated shape: the service half of the old UseBuiltInLogin<T> without the endpoint half.
        // It compiles and renders a login page, so if it did not gate, the dashboard would look locked while
        // every API route answered anonymously. Registering the login is therefore enough on its own — the
        // same guarantee UseBuiltInLogin gave before the endpoints existed.
        var (app, client) = await StartAsync(AddBuiltInLogin);
        try
        {
            var response = await client.GetAsync(ApiProbe, XunitTestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task BuiltInLogin_WithTheExplicitConvention_IsIdempotent()
    {
        // Applying the convention on top of the automatic one must not double-gate into something a valid
        // cookie can no longer satisfy.
        var (app, client) = await StartAsync(AddBuiltInLogin, x => x.RequireWarpDashboardLogin());
        try
        {
            var login = await LoginAsync(client, "admin", "admin");
            CarryCookie(client, login);

            var response = await client.GetAsync(ApiProbe, XunitTestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task MapWarpDashboard_TwiceWithBuiltInLogin_ThrowsRatherThanBreakingSignIn()
    {
        // One scheme, one cookie name, one path: the second map wins the cookie path and sign-in on the
        // first dashboard silently stops working.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddWarpDashboard().AddBuiltInLogin<TestCredentialValidator>();

        await using var app = builder.Build();
        app.MapWarpDashboard("/warp").RequireWarpDashboardLogin();

        Should.Throw<InvalidOperationException>(() => app.MapWarpDashboard("/admin/warp"))
            .Message.ShouldContain("more than once");
    }

    [TimedFact]
    public async Task HostPolicy_ReplacesTheBuiltInLoginGate()
    {
        // Registering the built-in login and then applying your own policy is contradictory, and requiring
        // BOTH is the wrong reading — a caller who satisfies the host policy would still be turned away for
        // lacking a Warp cookie there is no longer any way to obtain. The host's policy wins outright.
        var (app, client) = await StartAsync(
            x =>
            {
                x.AddWarpDashboard().AddBuiltInLogin<TestCredentialValidator>();
                AddHostIdentity(x);
            },
            x => x.RequireAuthorization(HostPolicy),
            MapSignIn);
        try
        {
            var signIn = await client.GetAsync("/signin?admin=true", XunitTestContext.Current.CancellationToken);
            CarryCookie(client, signIn);

            var response = await client.GetAsync(ApiProbe, XunitTestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task HostPolicy_ReplacingTheLogin_TellsTheSpaThereIsNoLoginPage()
    {
        // The SPA must not offer a login form that cannot grant access — the cookie it would issue is no
        // longer the gate.
        var (app, client) = await StartAsync(
            x =>
            {
                x.AddWarpDashboard().AddBuiltInLogin<TestCredentialValidator>();
                AddHostIdentity(x);
            },
            x => x.RequireAuthorization(HostPolicy),
            MapSignIn);
        try
        {
            var signIn = await client.GetAsync("/signin?admin=true", XunitTestContext.Current.CancellationToken);
            CarryCookie(client, signIn);

            var response = await client.GetAsync("/warp", XunitTestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await response.Content.ReadAsStringAsync(XunitTestContext.Current.CancellationToken);
            body.ShouldContain("window.hasBuiltInLogin = false");
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task HostPolicy_ReplacingTheLogin_StillGatesAnonymousCallers()
    {
        // Replacing the gate must not mean removing it.
        var (app, client) = await StartAsync(
            x =>
            {
                x.AddWarpDashboard().AddBuiltInLogin<TestCredentialValidator>();
                AddHostIdentity(x);
            },
            x => x.RequireAuthorization(HostPolicy),
            MapSignIn);
        try
        {
            var response = await client.GetAsync(ApiProbe, XunitTestContext.Current.CancellationToken);

            response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    private static void AddBuiltInLogin(IServiceCollection services)
        => services.AddWarpDashboard().AddBuiltInLogin<TestCredentialValidator>();

    private static void AddHostIdentity(IServiceCollection services)
    {
        // A stand-in for the host's own identity system: the challenge lands on its sign-in path and a
        // permission failure lands on its access-denied path, both driven by ASP.NET rather than Warp.
        services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(x =>
            {
                x.LoginPath = "/login";
                x.AccessDeniedPath = "/denied";
            });

        services
            .AddAuthorizationBuilder()
            .AddPolicy(HostPolicy, x => x.RequireClaim(AdminClaim));
    }

    private static void MapSignIn(WebApplication app)
        => app.MapGet("/signin", async (HttpContext context, bool admin) =>
        {
            var claims = admin ? new[] { new Claim(AdminClaim, "true") } : [];
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme, ClaimTypes.Name, ClaimTypes.Role);
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

            return Results.Ok();
        });

    private static void StampLoopback(WebApplication app)
        => app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            await next(context);
        });

    private static async Task<(WebApplication App, HttpClient Client)> StartAsync(
        Action<IServiceCollection>? services = null,
        Action<WarpDashboardEndpointConventionBuilder>? gate = null,
        Action<WebApplication>? pipeline = null,
        string routePrefix = "/warp")
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        services?.Invoke(builder.Services);

        var app = builder.Build();
        pipeline?.Invoke(app);

        // Wired explicitly, and after the pipeline callback: WebApplication would otherwise auto-insert
        // these ahead of anything the test registers, so a test that stamps a connection property could
        // never influence the authorization decision that reads it.
        if (app.Services.GetService<IAuthenticationSchemeProvider>() != null)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }

        var warp = app.MapWarpDashboard(routePrefix);
        gate?.Invoke(warp);

        await app.StartAsync(XunitTestContext.Current.CancellationToken);

        // Deliberately not TestServer.CreateClient(): that wraps the handler in a redirect-following one,
        // and a challenge redirect is exactly what several of these assert on.
        var server = app.GetTestServer();
        var client = new HttpClient(server.CreateHandler()) { BaseAddress = server.BaseAddress };

        return (app, client);
    }

    private static async Task StopAsync(WebApplication app, HttpClient client)
    {
        client.Dispose();
        await app.DisposeAsync();
    }

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string username, string password)
    {
        using var form = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("username", username),
            new KeyValuePair<string, string>("password", password),
        ]);

        return await client.PostAsync("/warp/api/auth/login", form, XunitTestContext.Current.CancellationToken);
    }

    private static async Task<string> GetAuthStatusAsync(HttpClient client)
    {
        var response = await client.GetAsync("/warp/api/auth/status", XunitTestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await response.Content.ReadAsStringAsync(XunitTestContext.Current.CancellationToken);
    }

    // The cookie handler builds an absolute redirect target, so compare the path rather than the string.
    private static string RedirectPath(HttpResponseMessage response)
    {
        var location = response.Headers.Location.ShouldNotBeNull();

        return location.IsAbsoluteUri ? location.AbsolutePath : location.ToString().Split('?')[0];
    }

    private static void CarryCookie(HttpClient client, HttpResponseMessage response)
    {
        var cookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", cookie);
    }
}

// Authenticates every caller with an empty identity. Stands in for a host scheme that exists for some
// unrelated purpose, to prove it cannot be mistaken for dashboard authorization.
internal sealed class PermissiveAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public PermissiveAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity("Permissive")), "Permissive");

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

internal class TestCredentialValidator : IWarpCredentialValidator
{
    public Task<bool> ValidateAsync(string username, string password)
    {
        return Task.FromResult(string.Equals(username, "admin", StringComparison.Ordinal) && string.Equals(password, "admin", StringComparison.Ordinal));
    }
}
