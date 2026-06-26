using Microsoft.EntityFrameworkCore;

namespace Warp.Core;

/// <summary>
/// The Warp server context exposed as an injectable abstraction. Server-internal components
/// (worker fetch/complete, server tasks, the background-service host) depend on this rather than the
/// concrete generic <c>WarpServerContext&lt;TContext&gt;</c>, so they're decoupled from the context's
/// type parameter and trivially testable — a test wraps any <see cref="DbContext"/>. The underlying
/// context carries Warp's own (quiet) <c>ILoggerFactory</c>, keeping server polling out of the
/// application's command logs.
/// </summary>
public interface IWarpServerContext
{
    DbContext Context { get; }
}
