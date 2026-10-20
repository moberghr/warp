namespace Warp.Dashboard.Push;

/// <summary>
/// How many dashboard clients are connected to this process's <see cref="WarpDashboardHub"/>.
/// </summary>
/// <remarks>
/// Lets <see cref="DashboardBroadcaster{TContext}"/> skip the stats snapshot it attaches to each
/// broadcast when nobody here is watching. Local to this process: clients on another server, or
/// reached through a SignalR backplane, are not counted, which is why the broadcaster still sends
/// the event itself and only drops the snapshot.
/// </remarks>
public sealed class DashboardConnectionCounter
{
    /// <summary>
    /// Connected clients; written with <see cref="Interlocked"/> from the hub's connect/disconnect callbacks.
    /// </summary>
    private int _count;

    /// <summary>
    /// The number of currently connected dashboard clients.
    /// </summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>
    /// Records a client connecting.
    /// </summary>
    internal void Connected() => Interlocked.Increment(ref _count);

    /// <summary>
    /// Records a client disconnecting.
    /// </summary>
    internal void Disconnected() => Interlocked.Decrement(ref _count);
}
