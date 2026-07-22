using System.Reflection;

namespace Warp.UI.UIMiddleware;

public class WarpUIOptions
{
    public string RoutePrefix { get; set; } = "/warp";

    /// <summary>
    /// Optional instance label (e.g. "Production", "Staging") shown in the dashboard nav and appended to
    /// the browser-tab title, so tabs for different Warp deployments are distinguishable. Null = none.
    /// </summary>
    public string? InstanceName { get; set; }

    /// <summary>
    /// Optional URL for a "back to app" link rendered in the dashboard nav (e.g. the host portal's home).
    /// Null = no link.
    /// </summary>
    public string? PortalUrl { get; set; }

    /// <summary>Label for the <see cref="PortalUrl"/> link. Defaults to "Back to app" when a URL is set.</summary>
    public string? PortalLabel { get; set; }

    /// <summary>Optional logo image URL shown in the dashboard nav header. Null = the default Warp wordmark.</summary>
    public string? LogoUrl { get; set; }

    public Func<Stream> IndexStream { get; set; } = () => typeof(WarpUIOptions).GetTypeInfo().Assembly.GetManifestResourceStream("Warp.UI.dist.index.html")!;

    /// <summary>
    /// Authorization filter for the dashboard. Null = allow all (default).
    /// When CredentialValidator is set, this is auto-configured to check the Warp cookie.
    /// </summary>
    public IWarpAuthorizationFilter? Authorization { get; set; }

    /// <summary>
    /// URL to redirect to when unauthorized. If set, browser requests get 302 redirect
    /// with ?returnUrl= parameter. Takes precedence over the built-in login page.
    /// </summary>
    public string? UnauthorizedRedirectUrl { get; set; }

    /// <summary>
    /// Type of the IWarpCredentialValidator implementation for the built-in login page.
    /// Set via UseBuiltInLogin&lt;T&gt;(). Null = no built-in login.
    /// </summary>
    internal Type? CredentialValidatorType { get; set; }

    /// <summary>
    /// Enables the built-in login page with the specified credential validator.
    /// The validator is registered in DI as scoped, so it can inject DbContext, etc.
    /// </summary>
    public void UseBuiltInLogin<TValidator>()
        where TValidator : class, IWarpCredentialValidator
    {
        CredentialValidatorType = typeof(TValidator);
    }
}
