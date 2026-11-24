namespace Warp.Core;

/// <summary>
/// Whether a server from before 8.0 (a <c>Server</c> row without <c>CoordinationVersion</c> and a live
/// heartbeat) is in the cluster, as server registration and then every cluster-task claim report it (rules
/// 2.20, 6.23). Such a server writes and compares its OWN clock, so while one is alive an 8.0 server does the
/// same: <see cref="DatabaseClock"/> applies no correction and the heartbeat writes the server's own time, as
/// 7.7 did. A cluster whose hosts disagree with the database is then no worse than a pure 7.7 one; mixing the
/// two clocks was (a singleton lease written on one clock looked expired on the other).
/// <para>
/// Starts as alive, like the claim's own flag: until something has looked, assuming an old server costs no
/// more than 7.7's behaviour.
/// </para>
/// <para>
/// REMOVE in the release after 8.x, together with <c>LegacyTaskLock</c>: once no server from before 8.0 can
/// be alive in a cluster, the database clock always applies.
/// </para>
/// </summary>
internal sealed class LegacyServerPresence
{
    private volatile bool _alive;

    public LegacyServerPresence(bool alive) => _alive = alive;

    /// <summary>True while a server from before 8.0 may be alive (and before anything has looked).</summary>
    public bool Alive => _alive;

    public void Report(bool alive) => _alive = alive;
}
