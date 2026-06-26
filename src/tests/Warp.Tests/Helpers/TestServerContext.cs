using Microsoft.EntityFrameworkCore;
using Warp.Core;

namespace Warp.Tests.Helpers;

/// <summary>
/// Wraps any <see cref="DbContext"/> as an <see cref="IWarpServerContext"/> for unit-constructing
/// server tasks / hosts directly (the §4.8 one-method-one-class pattern). In production the server
/// context is the quiet-logging <c>WarpServerContext&lt;TContext&gt;</c>; in tests it's just the
/// fixture's context, since the names/model are identical for the default (non-renamed) schema.
/// </summary>
internal sealed class TestServerContext : IWarpServerContext
{
    public TestServerContext(DbContext context) => Context = context;

    public DbContext Context { get; }
}
