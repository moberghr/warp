namespace Warp.Core;

/// <summary>
/// Empty marker registered as a singleton by <c>AddWarpServer</c>. Its sole purpose is to let the
/// non-server <c>ApplicationHeartbeatHost</c> (which lives in <c>Warp.Core</c>) detect that it is running
/// inside a server process — without <c>Warp.Core</c> taking a dependency on <c>Warp.Worker</c>. A server
/// process records itself on the <c>Server</c> row via the existing <c>Heartbeat</c> server task, so the
/// application heartbeat host stays inert there (it would otherwise double-write an
/// <c>ApplicationInstance</c> row for the same process).
/// </summary>
public interface IWarpServerPresence;
