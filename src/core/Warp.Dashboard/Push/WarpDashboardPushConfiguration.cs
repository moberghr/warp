namespace Warp.Dashboard.Push;

/// <summary>
/// Tunables for <c>opt.AddDashboardPush()</c>. Default values match the spec —
/// 100 ms coalescing collapses burst broadcasts (e.g., 50-job batch completion fires once,
/// not 50 times) without introducing perceptible UI lag.
/// </summary>
public sealed class WarpDashboardPushConfiguration
{
    /// <summary>
    /// Largest accepted <see cref="MinBroadcastInterval"/>. Beyond a minute the dashboard stops reading
    /// as live; the clients' 30s safety poll would be doing the work.
    /// </summary>
    public static readonly TimeSpan MaxMinBroadcastInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Window over which incoming signals are coalesced into a single broadcast per event kind.
    /// Set to <see cref="TimeSpan.Zero"/> to disable coalescing (every signal becomes its own
    /// broadcast — useful for tests that need deterministic counts).
    /// </summary>
    public TimeSpan CoalesceWindow { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Least time between two broadcasts. Signals arriving sooner are held and coalesced into the
    /// next broadcast, which still carries a fresh stats snapshot.
    /// </summary>
    /// <remarks>
    /// Every broadcast runs the dashboard stats queries once and makes every connected client
    /// refetch the lists it shows, so under steady throughput the 100 ms <see cref="CoalesceWindow"/>
    /// alone allowed about ten of each per second. The first signal after a quiet period is not
    /// delayed beyond <see cref="CoalesceWindow"/>. <see cref="TimeSpan.Zero"/> disables the floor.
    /// Must be between zero and <see cref="MaxMinBroadcastInterval"/>; <c>AddDashboardPush</c> rejects
    /// anything else.
    /// </remarks>
    public TimeSpan MinBroadcastInterval { get; set; } = TimeSpan.FromSeconds(1);
}
