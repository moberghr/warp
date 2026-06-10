namespace Warp.Core.Notifications;

/// <summary>
/// Options for <c>opt.UseDatabasePush() (inside the AddWarp/AddWarpServer lambda)</c>. Defaults are sensible; override only if you
/// need to share a channel name with an external system or tune reconnect behavior.
/// </summary>
public class WarpDatabasePushConfiguration
{
    /// <summary>
    /// Channel/queue identifier used by the transport. PostgreSQL LISTEN/NOTIFY channel,
    /// SQL Server Service Broker queue/service name.
    /// </summary>
    public string ChannelName { get; set; } = "warp_notify";

    /// <summary>
    /// Initial delay before reconnecting after a transport error. Doubles up to <see cref="ReconnectMaxDelay"/>.
    /// </summary>
    public TimeSpan ReconnectInitialDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Upper bound on the reconnect delay.
    /// </summary>
    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(30);
}
