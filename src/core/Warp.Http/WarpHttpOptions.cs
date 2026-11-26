namespace Warp.Http;

/// <summary>
/// Optional configuration for <see cref="ServiceCollectionExtensions.AddWarpHttp"/>.
/// </summary>
public sealed class WarpHttpOptions
{
    /// <summary>
    /// Whether endpoints that bind a form (<c>IFormFile</c>, <c>[FromForm]</c>) keep ASP.NET's antiforgery
    /// validation. Default false: Warp disables it on them, so programmatic clients can upload without a token.
    /// Turn it on when browsers post these forms with cookie authentication, which makes them a cross-site
    /// request forgery target; the host then needs <c>AddAntiforgery()</c> and <c>app.UseAntiforgery()</c>.
    /// Either way, <c>[RequireAntiforgeryToken]</c> or <c>[RequireAntiforgeryToken(false)]</c> on a handler
    /// class decides for that endpoint.
    /// </summary>
    public bool ValidateAntiforgeryOnFormEndpoints { get; set; }
}
