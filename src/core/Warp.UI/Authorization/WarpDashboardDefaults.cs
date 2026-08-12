namespace Warp.UI;

/// <summary>
/// Well-known authentication-scheme and authorization-policy names used by the dashboard's
/// built-in authorization helpers. A host that gates the dashboard on its own policy never
/// needs these — see <c>MapWarpUI(...).RequireAuthorization("YourPolicy")</c>.
/// </summary>
public static class WarpDashboardDefaults
{
    /// <summary>
    /// Cookie authentication scheme registered by <c>AddWarpDashboard().AddBuiltInLogin&lt;T&gt;()</c>.
    /// </summary>
    public const string AuthenticationScheme = "Warp.Dashboard";

    /// <summary>
    /// Policy applied by <c>RequireWarpDashboardLogin()</c>: requires a valid built-in login cookie.
    /// </summary>
    public const string LoginPolicy = "WarpDashboardLogin";

    /// <summary>
    /// Policy applied by <c>RequireLocalRequests()</c>: allows loopback callers only.
    /// </summary>
    public const string LocalRequestsPolicy = "WarpDashboardLocalRequests";

    /// <summary>
    /// Deny-only authentication scheme: authenticates nobody and renders every challenge and every
    /// forbid as a bare 403. Pin it on a policy that signing in cannot satisfy — a localhost-only
    /// rule, an API-key check — so the denial renders correctly in a host that has no identity
    /// provider registered at all.
    /// </summary>
    public const string DenyScheme = "Warp.Dashboard.Deny";

    /// <summary>Name of the cookie issued by the built-in login.</summary>
    public const string CookieName = ".Warp.Auth";
}
