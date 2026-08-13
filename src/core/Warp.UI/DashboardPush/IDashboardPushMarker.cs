namespace Warp.UI.DashboardPush;

/// <summary>
/// Marker service registered iff <c>opt.AddDashboardPush()</c> was called. Resolved
/// from DI by the <c>/api/addons</c> discovery endpoint (sets <c>push: false</c> when absent)
/// and by <c>MapWarpUI</c> to decide whether to <c>MapHub</c>.
/// </summary>
public interface IDashboardPushMarker;

public sealed class DashboardPushMarker : IDashboardPushMarker;
