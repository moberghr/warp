namespace Warp.Core.Data;

/// <summary>
/// How a server coordinates cluster-wide server tasks, stamped on its <c>Server</c> row at registration.
/// A row without one is a server from before the lease (rule 6.23); while one is alive, newer servers
/// also take each task's old lock around a run.
/// </summary>
public static class ServerCoordination
{
    /// <summary>Cluster-wide tasks are claimed per tick with a lease on their <c>ClusterTask</c> row.</summary>
    public const int LeaseVersion = 1;
}
