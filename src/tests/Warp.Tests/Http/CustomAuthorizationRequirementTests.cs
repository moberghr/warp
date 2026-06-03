using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Http;

namespace Warp.Tests.Http;

/// <summary>
/// Confirms a custom <see cref="IAuthorizationRequirement"/> + <see cref="AuthorizationHandler{TRequirement}"/>
/// composes correctly with <c>[Authorize(Policy = "...")]</c> on a <c>[WarpHttpPost]</c> endpoint.
///
/// This pattern was reported by an external adopter as not firing — they observed the handler
/// body never executing and requests with the correct header getting 401. These tests pin down
/// the actual behavior: the handler DOES fire, the policy IS evaluated, and the request is
/// denied (401/403) when the header is missing or wrong. Requires the host to wire
/// UseAuthentication + at least one auth scheme (modern AspNet throws at request time if a
/// policy is configured without an auth scheme).
/// </summary>
[Trait("Category", "NoDb")]
public sealed class CustomAuthorizationRequirementTests
{
    private const string WebhookPasswordHeader = "X-Webhook-Password";
    private const string WebhookPasswordValue = "secret";
    private const string PassthroughScheme = "Passthrough";
    private const string NoCredentialScheme = "NoCredentials";

    [TimedFact]
    public async Task CustomRequirement_AuthorizationHandler_Fires_AndGrantsWhenHeaderMatches()
    {
        var probe = new HandlerInvocationProbe();
        await using var app = await StartAppWithAuth(probe);

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/secure/webhook")
        {
            Content = JsonContent.Create(new { }),
        };
        req.Headers.Add(WebhookPasswordHeader, WebhookPasswordValue);

        var resp = await app.Client.SendAsync(req);

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        probe.InvocationCount.ShouldBe(1, "custom IAuthorizationHandler must fire exactly once for the request");
    }

    [TimedFact]
    public async Task CustomRequirement_AuthorizationHandler_Fires_AndDeniesWhenHeaderMissing()
    {
        var probe = new HandlerInvocationProbe();
        await using var app = await StartAppWithAuth(probe);

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/secure/webhook")
        {
            Content = JsonContent.Create(new { }),
        };

        var resp = await app.Client.SendAsync(req);

        resp.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        probe.InvocationCount.ShouldBe(1, "custom IAuthorizationHandler must still fire so it can decide to deny");
    }

    [TimedFact]
    public async Task CustomRequirement_AuthorizationHandler_Fires_AndDeniesWhenHeaderWrong()
    {
        var probe = new HandlerInvocationProbe();
        await using var app = await StartAppWithAuth(probe);

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/secure/webhook")
        {
            Content = JsonContent.Create(new { }),
        };
        req.Headers.Add(WebhookPasswordHeader, "wrong-password");

        var resp = await app.Client.SendAsync(req);

        resp.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        probe.InvocationCount.ShouldBe(1);
    }

    [TimedFact]
    public async Task NoCredentialAuthScheme_HandlerStillFiresAndGrantsOnCorrectHeader()
    {
        // Pin the actual behavior that the external bug report ("handler never fires +
        // correct header → 401") is INCONSISTENT with. Wire a realistic auth scheme that
        // returns AuthenticateResult.NoResult() (the standard behavior of JWT bearer /
        // cookie schemes when no credentials are present), and prove that:
        //   1. The custom IAuthorizationHandler DOES fire — `probe.InvocationCount >= 1`
        //   2. With the correct header the policy succeeds — 200 OK
        // Combined, this rules out "NoResult auth scheme prevents policy from running"
        // as the explanation for the report. The user-reported symptom must be caused by
        // something else in their wiring — likely a policy that explicitly adds
        // RequireAuthenticatedUser, a FallbackPolicy that does, or a scheme that calls
        // AuthenticateResult.Fail (which short-circuits before requirements are evaluated).
        var probe = new HandlerInvocationProbe();
        await using var app = await WarpHttpTestApp.StartAsync(
            configureServices: services =>
            {
                services.AddSingleton(probe);
                services.AddSingleton<IAuthorizationHandler, WebhookPasswordAuthorizationHandler>();
                services
                    .AddAuthentication(NoCredentialScheme)
                    .AddScheme<AuthenticationSchemeOptions, NoCredentialAuthHandler>(NoCredentialScheme, _ => { });
                services.AddAuthorization(opts => opts.AddPolicy(
                    "WebhookPasswordPolicy",
                    policy => policy.AddRequirements(new WebhookPasswordRequirement(WebhookPasswordValue))));
            },
            configureApp: a =>
            {
                a.UseAuthentication();
                a.UseAuthorization();
                a.MapWarpHttp();
            });

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/secure/webhook")
        {
            Content = JsonContent.Create(new { }),
        };
        req.Headers.Add(WebhookPasswordHeader, WebhookPasswordValue);

        var resp = await app.Client.SendAsync(req);

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        probe.InvocationCount.ShouldBeGreaterThanOrEqualTo(1, "custom handler fires even when authn returned NoResult");
    }

    [TimedFact]
    public async Task PolicyWithRequireAuthenticatedUser_NoCredentials_DeniesAndHandlerStillFires()
    {
        // Pins one combination that PARTIALLY matches the reported symptom (401 with the
        // correct header) but contradicts the rest (handler never fires): a NoResult auth
        // scheme combined with a policy that has RequireAuthenticatedUser plus the custom
        // requirement. Under ASP.NET's default AuthorizationOptions.InvokeHandlersAfterFailure
        // value of true, the custom handler still runs even though the authenticated-user
        // requirement has already marked the policy as failing. The request is denied 401.
        //
        // This rules out RequireAuthenticatedUser as the root cause of the reported
        // "handler never fires" symptom. Remaining hypotheses for further investigation
        // are documented in website/docs/operations/migrating-from-wolverine.md under the
        // authorization section.
        var probe = new HandlerInvocationProbe();
        await using var app = await WarpHttpTestApp.StartAsync(
            configureServices: services =>
            {
                services.AddSingleton(probe);
                services.AddSingleton<IAuthorizationHandler, WebhookPasswordAuthorizationHandler>();
                services
                    .AddAuthentication(NoCredentialScheme)
                    .AddScheme<AuthenticationSchemeOptions, NoCredentialAuthHandler>(NoCredentialScheme, _ => { });
                services.AddAuthorization(opts => opts.AddPolicy(
                    "WebhookPasswordPolicy",
                    policy => policy
                        .RequireAuthenticatedUser()
                        .AddRequirements(new WebhookPasswordRequirement(WebhookPasswordValue))));
            },
            configureApp: a =>
            {
                a.UseAuthentication();
                a.UseAuthorization();
                a.MapWarpHttp();
            });

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/secure/webhook")
        {
            Content = JsonContent.Create(new { }),
        };
        req.Headers.Add(WebhookPasswordHeader, WebhookPasswordValue);

        var resp = await app.Client.SendAsync(req);

        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        probe.InvocationCount.ShouldBeGreaterThanOrEqualTo(1, "custom handler fires under default InvokeHandlersAfterFailure=true even when an earlier requirement marks the policy as failing");
    }

    private static Task<WarpHttpTestApp> StartAppWithAuth(HandlerInvocationProbe probe)
        => WarpHttpTestApp.StartAsync(
            configureServices: services =>
            {
                services.AddSingleton(probe);
                services.AddSingleton<IAuthorizationHandler, WebhookPasswordAuthorizationHandler>();
                services
                    .AddAuthentication(PassthroughScheme)
                    .AddScheme<AuthenticationSchemeOptions, PassthroughAuthHandler>(PassthroughScheme, _ => { });
                services.AddAuthorization(opts => opts.AddPolicy(
                    "WebhookPasswordPolicy",
                    policy => policy.AddRequirements(new WebhookPasswordRequirement(WebhookPasswordValue))));
            },
            configureApp: a =>
            {
                a.UseAuthentication();
                a.UseAuthorization();
                a.MapWarpHttp();
            });

    private sealed class WebhookPasswordRequirement : IAuthorizationRequirement
    {
        public WebhookPasswordRequirement(string expectedPassword)
        {
            ExpectedPassword = expectedPassword;
        }

        public string ExpectedPassword { get; }
    }

    private sealed class WebhookPasswordAuthorizationHandler(HandlerInvocationProbe probe)
        : AuthorizationHandler<WebhookPasswordRequirement>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, WebhookPasswordRequirement requirement)
        {
            probe.RecordInvocation();

            // ASP.NET endpoint authorization sets Resource to the current HttpContext.
            if (context.Resource is HttpContext http
                && http.Request.Headers.TryGetValue(WebhookPasswordHeader, out var pw)
                && string.Equals(pw.ToString(), requirement.ExpectedPassword, StringComparison.Ordinal))
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }
    }

    // Empty auth scheme — gives the authorization middleware something to challenge against
    // so failed policies actually return 401 instead of silently allowing the request.
    private sealed class PassthroughAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity()), PassthroughScheme);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    // Realistic auth scheme that returns NoResult when the request has no credentials.
    // Mirrors what a real JWT bearer / cookie scheme does for a webhook endpoint reached
    // without a token — ASP.NET treats this as "unauthenticated" and challenges (401)
    // before any IAuthorizationHandler is invoked.
    private sealed class NoCredentialAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(AuthenticateResult.NoResult());
    }

    private sealed class HandlerInvocationProbe
    {
        private int _invocationCount;

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public void RecordInvocation() => Interlocked.Increment(ref _invocationCount);
    }
}
