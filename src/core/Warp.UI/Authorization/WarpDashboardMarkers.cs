namespace Warp.UI;

/// <summary>
/// Marker registered by <c>AddWarpDashboard()</c>. <c>RequireWarpDashboardLogin()</c> and
/// <c>RequireLocalRequests()</c> check for it so a missing service registration fails at startup with a
/// clear message instead of per-request with "policy not found" naming a policy the host never typed.
/// </summary>
internal interface IWarpDashboardMarker;

internal sealed class WarpDashboardMarker : IWarpDashboardMarker;

/// <summary>
/// Marker registered iff <c>AddWarpDashboard().AddBuiltInLogin&lt;T&gt;()</c> was called. Gates the
/// login/logout endpoints and drives the <c>window.hasBuiltInLogin</c> flag injected into the SPA,
/// which decides whether the React app renders its own login page.
/// </summary>
public interface IWarpDashboardLoginMarker;

internal sealed class WarpDashboardLoginMarker : IWarpDashboardLoginMarker;

/// <summary>
/// Carries the resolved <see cref="WarpUIOptions.RoutePrefix"/> from <c>MapWarpUI</c> to the login
/// cookie's <c>Path</c>. The prefix is only known once the host maps the dashboard, which is after DI
/// is built — but cookie options resolve lazily on the first request, so writing it at map time lands
/// before any reader. Explicit <see cref="WarpDashboardLoginOptions.CookiePath"/> wins over this.
/// </summary>
internal sealed class WarpDashboardCookiePath
{
    public string? Value { get; set; }
}
