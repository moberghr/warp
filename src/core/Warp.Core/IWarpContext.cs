using Microsoft.EntityFrameworkCore;

namespace Warp.Core;

// The context the dashboard query, command and admin services run on (rule 2.14): the Warp context when the
// provider supports TContext's options, otherwise the scoped TContext itself (an application's tests on EF
// in-memory or SQLite, or a host without a provider package), which is how those services behaved before #344.
internal interface IWarpContext
{
    DbContext Context { get; }
}

internal sealed class WarpContextHandle : IWarpContext
{
    public WarpContextHandle(DbContext context)
    {
        Context = context;
    }

    public DbContext Context { get; }
}
