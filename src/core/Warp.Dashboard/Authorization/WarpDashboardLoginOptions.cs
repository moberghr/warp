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
}
