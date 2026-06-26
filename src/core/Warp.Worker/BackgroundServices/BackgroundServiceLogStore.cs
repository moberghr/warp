using Microsoft.EntityFrameworkCore;
using Warp.Core;
using Warp.Core.BackgroundServices;
using Warp.Core.Data.Entities;

namespace Warp.Worker.BackgroundServices;

/// <summary>
/// EF Core implementation of <see cref="IBackgroundServiceLogStore"/>. Inserts all entries
/// in a single <c>AddRange</c> + <c>SaveChangesAsync</c> call — one transaction per flush.
/// </summary>
public sealed class BackgroundServiceLogStore<TContext> : IBackgroundServiceLogStore
    where TContext : DbContext
{
    private readonly DbContext _context;

    public BackgroundServiceLogStore(IWarpServerContext serverContext)
    {
        _context = serverContext.Context;
    }

    /// <inheritdoc/>
    public async Task InsertManyAsync(IReadOnlyList<BackgroundServiceLog> entries, CancellationToken ct)
    {
        if (entries.Count == 0)
        {
            return;
        }

        _context.Set<BackgroundServiceLog>().AddRange(entries);
        await _context.SaveChangesAsync(ct);
    }
}
