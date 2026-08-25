using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Warp.Dashboard;

/// <summary>
/// Authenticates nobody and renders both challenge and forbid as a bare 403.
/// </summary>
/// <remarks>
/// ASP.NET's authorization middleware classifies any failure by an anonymous caller as a
/// <em>challenge</em>, and <c>ChallengeAsync</c> throws when the host registered no schemes. A policy
/// that signing in can never satisfy — a localhost-only rule, an API-key check — pins this scheme so
/// the denial renders as 403 instead of crashing a host with no identity provider.
/// </remarks>
internal sealed class WarpDenyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public WarpDenyAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) => Deny();

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) => Deny();

    private Task Deny()
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;

        return Task.CompletedTask;
    }
}
