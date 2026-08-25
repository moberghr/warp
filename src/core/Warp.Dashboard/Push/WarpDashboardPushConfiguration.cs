namespace Warp.Dashboard.Push;

/// <summary>
/// Tunables for <c>opt.AddDashboardPush()</c>. Default values match the spec —
/// 100 ms coalescing collapses burst broadcasts (e.g., 50-job batch completion fires once,
/// not 50 times) without introducing perceptible UI lag.
/// </summary>
public sealed class WarpDashboardPushConfiguration
{
    /// <summary>
    /// Window over which incoming signals are coalesced into a single broadcast per event kind.
    /// Set to <see cref="TimeSpan.Zero"/> to disable coalescing (every signal becomes its own
    /// broadcast — useful for tests that need deterministic counts).
    /// </summary>
    public TimeSpan CoalesceWindow { get; set; } = TimeSpan.FromMilliseconds(100);
}
