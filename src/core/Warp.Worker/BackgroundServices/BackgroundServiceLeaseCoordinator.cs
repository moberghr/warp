using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.BackgroundServices;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;

namespace Warp.Worker.BackgroundServices;

/// <summary>
/// Manages <c>BackgroundServiceLease</c> rows for singleton service coordination.
/// </summary>
public sealed class BackgroundServiceLeaseCoordinator<TContext> : IBackgroundServiceLeaseCoordinator
    where TContext : DbContext
{
    private readonly DbContext _context;
    private readonly TimeProvider _time;
    private readonly Guid _serverId;
    private readonly IWarpSqlQueries<TContext> _sqlQueries;

    public BackgroundServiceLeaseCoordinator(
        IWarpServerContext serverContext,
        TimeProvider time,
        IOptions<WarpServerConfiguration> options,
        IWarpSqlQueries<TContext> sqlQueries)
    {
        _context = serverContext.Context;
        _time = time;
        _serverId = options.Value.ServerId;
        _sqlQueries = sqlQueries;
    }

    /// <inheritdoc/>
    public async Task<bool> TryAcquireAsync(string serviceName, TimeSpan ttl, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var expiresAt = now.Add(ttl);

        await using var transaction = await _context.Database.BeginTransactionAsync(ct);

        // Row-level lock (FOR UPDATE / WITH UPDLOCK,ROWLOCK) eliminates the TOCTOU window
        // between SELECT and the INSERT/UPDATE below (§1.4). The lock is held until
        // COMMIT/ROLLBACK, so concurrent callers serialise correctly.
        var existing = await _sqlQueries.LockLeaseByServiceNameAsync(_context, serviceName, ct);

        if (existing == null)
        {
            _context.Set<BackgroundServiceLease>().Add(new BackgroundServiceLease
            {
                ServiceName = serviceName,
                HolderServerId = _serverId,
                LeaseExpiresAt = expiresAt,
            });

            await _context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return true;
        }

        var isExpired = existing.LeaseExpiresAt < now;
        var isOwnLease = existing.HolderServerId == _serverId;

        if (!isExpired && !isOwnLease)
        {
            await transaction.RollbackAsync(ct);

            return false;
        }

        existing.HolderServerId = _serverId;
        existing.LeaseExpiresAt = expiresAt;

        await _context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return true;
    }

    /// <inheritdoc/>
    public async Task ReleaseAsync(string serviceName, CancellationToken ct)
    {
        await _context.Set<BackgroundServiceLease>()
            .Where(x => x.ServiceName == serviceName)
            .Where(x => x.HolderServerId == _serverId)
            .ExecuteDeleteAsync(ct);
    }
}
