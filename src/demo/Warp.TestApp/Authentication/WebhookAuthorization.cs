using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Warp.TestApp.Authentication;

// Demo auth setup that proves a custom IAuthorizationRequirement + AuthorizationHandler
// composes with [Authorize(Policy = "...")] on a [WarpHttpPost] handler. Mirrors the
// "webhook password" pattern from external feedback. Curl examples — POST against the
// running TestApp:
//
//   # 200 — header matches the expected password
//   curl -i -X POST -H "Content-Type: application/json" \
//        -H "X-Webhook-Password: secret" \
//        -d '{"Payload":"hi"}' http://localhost:5000/http/webhook
//
//   # 403 — header missing or wrong. PermissiveAuthHandler always authenticates, so the
//   # principal is "authenticated" → ASP.NET issues 403 (forbid), not 401 (challenge).
//   curl -i -X POST -H "Content-Type: application/json" -d '{"Payload":"hi"}' \
//        http://localhost:5000/http/webhook
//
// The 403 path proves the IAuthorizationHandler fires: it ran, found the header missing
// or wrong, and simply did not call context.Succeed() — the requirement stays
// unsatisfied, the policy fails, and the middleware forbids the request. (Distinct from
// calling context.Fail() explicitly, which would set a hard-fail flag.) The 200 path
// proves the handler can grant. Both confirm Warp.Http surfaces [Authorize] metadata
// so ASP.NET's authorization middleware can evaluate the policy.
public sealed class WebhookPasswordRequirement : IAuthorizationRequirement
{
    public WebhookPasswordRequirement(string expectedPassword)
    {
        ExpectedPassword = expectedPassword;
    }

    public string ExpectedPassword { get; }
}

public sealed class WebhookPasswordAuthorizationHandler : AuthorizationHandler<WebhookPasswordRequirement>
{
    private readonly ILogger<WebhookPasswordAuthorizationHandler> _logger;

    public WebhookPasswordAuthorizationHandler(ILogger<WebhookPasswordAuthorizationHandler> logger)
    {
        _logger = logger;
    }

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, WebhookPasswordRequirement requirement)
    {
        // Log every invocation so an operator running `dotnet run` against TestApp can see
        // the handler firing for both the success and the denial path. This is the line that
        // never appeared in the external feedback report — proving it fires here is the
        // verification artifact for §1.3.
        if (context.Resource is HttpContext http
            && http.Request.Headers.TryGetValue("X-Webhook-Password", out var pw)
            && string.Equals(pw.ToString(), requirement.ExpectedPassword, StringComparison.Ordinal))
        {
            _logger.LogInformation("WebhookPasswordAuthorizationHandler: header matched → granting");
            context.Succeed(requirement);
        }
        else
        {
            _logger.LogInformation("WebhookPasswordAuthorizationHandler: header missing or wrong → denying");
        }

        return Task.CompletedTask;
    }
}

// Permissive auth scheme — gives the authorization middleware a successful authentication
// ticket so it actually reaches policy evaluation. Without this (or any other scheme),
// AspNet's authorization middleware throws "No authenticationScheme was specified" on the
// first request to any [Authorize]-decorated endpoint.
//
// In a real webhook scenario the right move is to mark the handler [AllowAnonymous] and
// rely entirely on the custom policy to gate access — that bypasses the implicit
// "must be authenticated" requirement on [Authorize] and means the policy handler is the
// only gatekeeper. See the migrating-from-wolverine.md doc for that pattern. This demo
// uses the permissive scheme so both paths work side-by-side.
public sealed class PermissiveAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Demo";

    public PermissiveAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(SchemeName)), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
