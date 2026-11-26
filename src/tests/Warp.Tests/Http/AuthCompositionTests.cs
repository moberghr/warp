using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Http;
using Warp.Tests.TestData;

namespace Warp.Tests.Http;

/// <summary>
/// Behavioral auth tests — confirm group-level <c>RequireAuthorization()</c> + per-handler
/// <c>[Authorize]</c> / <c>[AllowAnonymous]</c> compose correctly through Warp.Http.
/// We don't test ASP.NET's auth pipeline itself; we test that our generator surfaces the
/// attributes as endpoint metadata where ASP.NET expects them.
/// </summary>
[Trait("Category", "NoDb")]
public sealed class AuthCompositionTests
{
    private const string TestScheme = "Test";
    private const string TestUserHeader = "X-Test-User";

    [TimedFact]
    public async Task GroupRequireAuthorization_RejectsUnauthenticatedRequest()
    {
        await using var app = await WarpHttpTestApp.StartAsync(
            configureServices: ConfigureTestAuth,
            configureApp: a =>
            {
                a.UseAuthentication();
                a.UseAuthorization();

                // Map all endpoints under a group with RequireAuthorization. /api/secure/echo
                // has [Authorize(Policy=...)] on the handler and should require auth.
                a.MapGroup(string.Empty).RequireAuthorization().MapWarpHttp();
            });

        // No X-Test-User header → unauthenticated → group's RequireAuthorization rejects.
        var resp = await app.Client.GetAsync(new Uri("/api/secure/echo", UriKind.Relative));

        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [TimedFact]
    public async Task GroupRequireAuthorization_AcceptsAuthenticatedRequest()
    {
        await using var app = await WarpHttpTestApp.StartAsync(
            configureServices: ConfigureTestAuth,
            configureApp: a =>
            {
                a.UseAuthentication();
                a.UseAuthorization();
                a.MapGroup(string.Empty).RequireAuthorization().MapWarpHttp();
            });

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/secure/echo");
        req.Headers.Add(TestUserHeader, "alice");
        var resp = await app.Client.SendAsync(req);

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TimedFact]
    public async Task AllowAnonymousOnHandler_OverridesGroupRequireAuthorization()
    {
        await using var app = await WarpHttpTestApp.StartAsync(
            configureServices: ConfigureTestAuth,
            configureApp: a =>
            {
                a.UseAuthentication();
                a.UseAuthorization();

                // Same group with RequireAuthorization, but /api/anon/echo carries [AllowAnonymous]
                // on the handler — that attribute is surfaced as endpoint metadata and lets
                // anonymous requests through.
                a.MapGroup(string.Empty).RequireAuthorization().MapWarpHttp();
            });

        var resp = await app.Client.GetAsync(new Uri("/api/anon/echo", UriKind.Relative));

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TimedFact]
    public async Task AuthorizeOnAbstractBaseHandler_RejectsUnauthenticatedRequest()
    {
        // No group-level gate: the only thing protecting the endpoint is the base class's [Authorize].
        await using var app = await WarpHttpTestApp.StartAsync(
            configureServices: ConfigureTestAuth,
            configureApp: a =>
            {
                a.UseAuthentication();
                a.UseAuthorization();
                a.MapWarpHttp();
            });

        using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        var resp = await app.Client.PostAsync(new Uri("/api/secure/inherited", UriKind.Relative), content);

        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [TimedFact]
    public async Task RequireAuthorization_ChainedOnMapWarpHttp_GatesTheMappedEndpoints()
    {
        await using var app = await WarpHttpTestApp.StartAsync(
            configureServices: ConfigureTestAuth,
            configureApp: a =>
            {
                a.UseAuthentication();
                a.UseAuthorization();
                a.MapWarpHttp().RequireAuthorization();
            });

        // /api/echo has no attribute of its own: only the chained convention protects it.
        using var content = new StringContent("{\"message\":\"hi\"}", System.Text.Encoding.UTF8, "application/json");
        var resp = await app.Client.PostAsync(new Uri("/api/echo", UriKind.Relative), content);

        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static void ConfigureTestAuth(IServiceCollection services)
    {
        services
            .AddAuthentication(TestScheme)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestScheme, _ => { });

        services.AddAuthorization(opts =>
        {
            // The handler in TestHandlers.cs uses Policy = "WarpHttpTestPolicy"; satisfy it
            // for any authenticated principal so we can isolate the test to plumbing, not policy logic.
            opts.AddPolicy("WarpHttpTestPolicy", policy => policy.RequireAuthenticatedUser());
            opts.DefaultPolicy = new AuthorizationPolicyBuilder(TestScheme).RequireAuthenticatedUser().Build();
            opts.FallbackPolicy = null;
        });
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(TestUserHeader, out var user) || user.Count == 0)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new[] { new Claim(ClaimTypes.Name, user.ToString()) };
            var identity = new ClaimsIdentity(claims, TestScheme);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
