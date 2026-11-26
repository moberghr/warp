namespace Warp.Dashboard;

/// <summary>
/// Options for the built-in dashboard cookie login, configured through
/// <c>AddWarpDashboard().AddBuiltInLogin&lt;TValidator&gt;(o =&gt; ...)</c>.
/// </summary>
public class WarpDashboardLoginOptions
{
    /// <summary>
    /// How long a login lasts. Enforced server-side by the cookie handler — an expired ticket is
    /// rejected regardless of what the browser chooses to keep sending. Default 1 day.
    /// </summary>
    public TimeSpan ExpireTimeSpan { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Whether the expiry window slides forward on activity. Default true.</summary>
    public bool SlidingExpiration { get; set; } = true;

    /// <summary>
    /// Cookie path. Null (the default) scopes the cookie to <see cref="WarpDashboardOptions.RoutePrefix"/>,
    /// so the dashboard cookie is never sent to the rest of the host app.
    /// </summary>
    public string? CookiePath { get; set; }

    /// <summary>
    /// Whether the login cookie is marked <c>Secure</c>, so the browser only ever sends it over HTTPS. Default true.
    /// Set it to false only for a dashboard served over plain HTTP on a development machine; browsers already
    /// treat <c>http://localhost</c> as secure, so most local setups need no change.
    /// </summary>
    public bool RequireSecureCookie { get; set; } = true;

    /// <summary>Brute-force throttling of the login endpoint. On by default.</summary>
    public WarpDashboardLoginThrottleOptions Throttling { get; } = new();
}
