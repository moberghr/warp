using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Warp.Core.BackgroundServices;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;

namespace Warp.Worker.BackgroundServices;

/// <summary>
/// Manages <c>BackgroundServiceDefinition</c> and <c>BackgroundServiceInstance</c> rows for
/// services running on this server.
/// </summary>
public sealed class BackgroundServiceStateService<TContext> : IBackgroundServiceStateService
    where TContext : DbContext
{
    private const int MaxErrorLength = 4096;

    private readonly TContext _context;
    private readonly TimeProvider _time;
    private readonly Guid _serverId;
    private readonly IWarpSqlQueries<TContext> _sqlQueries;

    public BackgroundServiceStateService(
        TContext context,
        TimeProvider time,
        IOptions<WarpServerConfiguration> options,
        IWarpSqlQueries<TContext> sqlQueries)
    {
        _context = context;
        _time = time;
        _serverId = options.Value.ServerId;
        _sqlQueries = sqlQueries;
    }

    /// <inheritdoc/>
    public async Task<RegistrationOutcome> RegisterAsync(
        string serviceName,
        ServiceScope declaredScope,
        CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        await using var transaction = await _context.Database.BeginTransactionAsync(ct);

        // Row-level lock (FOR UPDATE / WITH UPDLOCK,ROWLOCK) eliminates the TOCTOU window
        // between SELECT and the INSERT/UPDATE below (§1.4). The lock is held until
        // COMMIT/ROLLBACK, so concurrent callers serialise correctly.
        var definition = await _sqlQueries.LockDefinitionByServiceNameAsync(_context, serviceName, ct);

        ServiceScope effectiveScope;
        var isNew = definition == null;

        if (isNew)
        {
            definition = new BackgroundServiceDefinition
            {
                Name = serviceName,
                DeclaredScope = declaredScope,
                FirstSeenAt = now,
                LastSeenAt = now,
            };

            _context.Set<BackgroundServiceDefinition>().Add(definition);
            effectiveScope = declaredScope;
        }
        else
        {
            definition!.LastSeenAt = now;
            effectiveScope = definition.DeclaredScope;
        }

        var isMismatch = !isNew && effectiveScope != declaredScope;

        BackgroundServiceStatus initialStatus;
        if (isMismatch)
        {
            initialStatus = BackgroundServiceStatus.ConfigurationMismatch;
        }
        else if (declaredScope == ServiceScope.Singleton)
        {
            initialStatus = BackgroundServiceStatus.Waiting;
        }
        else
        {
            initialStatus = BackgroundServiceStatus.Running;
        }

        var existing = await _context.Set<BackgroundServiceInstance>()
            .Where(x => x.ServerId == _serverId)
            .Where(x => x.ServiceName == serviceName)
            .FirstOrDefaultAsync(ct);

        if (existing == null)
        {
            _context.Set<BackgroundServiceInstance>().Add(new BackgroundServiceInstance
            {
                ServerId = _serverId,
                ServiceName = serviceName,
                DeclaredScope = declaredScope,
                Status = initialStatus,
                StartedAt = now,
                LastHeartbeatAt = now,
                RestartCount = 0,
            });
        }
        else
        {
            existing.DeclaredScope = declaredScope;
            existing.Status = initialStatus;
            existing.StartedAt = now;
            existing.LastHeartbeatAt = now;
        }

        await _context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return isMismatch ? RegistrationOutcome.ConfigurationMismatch : RegistrationOutcome.Registered;
    }

    /// <inheritdoc/>
    public async Task SetStatusAsync(string serviceName, BackgroundServiceStatus status, CancellationToken ct)
    {
        await _context.Set<BackgroundServiceInstance>()
            .Where(x => x.ServerId == _serverId)
            .Where(x => x.ServiceName == serviceName)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status), ct);
    }

    /// <inheritdoc/>
    public async Task RecordFaultAsync(string serviceName, Exception ex, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var lastError = Truncate($"{ex.GetType().FullName}: {ex.Message}", MaxErrorLength);

        await _context.Set<BackgroundServiceInstance>()
            .Where(x => x.ServerId == _serverId)
            .Where(x => x.ServiceName == serviceName)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.Status, BackgroundServiceStatus.Faulted)
                    .SetProperty(x => x.LastError, lastError)
                    .SetProperty(x => x.LastErrorAt, now)
                    .SetProperty(x => x.RestartCount, x => x.RestartCount + 1),
                ct);
    }

    /// <inheritdoc/>
    public async Task ResetRestartCountAsync(string serviceName, CancellationToken ct)
    {
        await _context.Set<BackgroundServiceInstance>()
            .Where(x => x.ServerId == _serverId)
            .Where(x => x.ServiceName == serviceName)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RestartCount, 0), ct);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string serviceName, CancellationToken ct)
    {
        await _context.Set<BackgroundServiceInstance>()
            .Where(x => x.ServerId == _serverId)
            .Where(x => x.ServiceName == serviceName)
            .ExecuteDeleteAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<ServiceScope?> GetDefinedScopeAsync(string serviceName, CancellationToken ct)
    {
        return await _context.Set<BackgroundServiceDefinition>()
            .Where(x => x.Name == serviceName)
            .Select(x => (ServiceScope?)x.DeclaredScope)
            .FirstOrDefaultAsync(ct);
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }
}
