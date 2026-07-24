using Warp.Core;

namespace Warp.Worker;

/// <summary>
/// Trivial <see cref="IWarpServerPresence"/> implementation registered by <c>AddWarpServer</c>. Its
/// presence in DI signals to the Core-side <c>ApplicationHeartbeatHost</c> that this is a server process
/// (which records itself on its <c>Server</c> row), so the host stays inert instead of writing a
/// duplicate <c>ApplicationInstance</c> row.
/// </summary>
internal sealed class WarpServerPresence : IWarpServerPresence;
