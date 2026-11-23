using Microsoft.EntityFrameworkCore;
using Warp.Core;

namespace Warp.Tests.Helpers;

/// <summary>
/// An <see cref="IWarpServerContext"/> over an empty in-memory DbContext, for dispatcher tests that mock the
/// claim query: the dispatcher configures the context it claims on (no tracking, rule 6.20), so the context
/// has to be a real one, though nothing is ever read from it.
/// </summary>
internal sealed class InMemoryServerContext : IWarpServerContext, IDisposable
{
    public DbContext Context { get; } = new(new DbContextOptionsBuilder<DbContext>()
        .UseInMemoryDatabase($"server-context-{Guid.NewGuid():N}")
        .Options);

    public void Dispose() => Context.Dispose();
}
