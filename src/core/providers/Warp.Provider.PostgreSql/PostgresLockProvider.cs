using Medallion.Threading;
using Medallion.Threading.Postgres;
using Microsoft.Extensions.Logging;
using Npgsql;
using Warp.Core;

namespace Warp.Provider.PostgreSql;

// Medallion's concrete PostgresDistributedSynchronizationProvider uses PostgresAdvisoryLockKey,
// but the IDistributedLockProvider interface method CreateLock(string) wraps a string name
// into that key internally — so we store the reference as the interface type.
internal sealed class PostgresLockProvider : IWarpLockProvider
{
    private readonly IDistributedLockProvider _inner;
    private readonly ILogger _logger;

    public PostgresLockProvider(string connectionString, ILogger<PostgresLockProvider> logger)
    {
        _inner = new PostgresDistributedSynchronizationProvider(connectionString);
        _logger = logger;
    }

    // Data-source overload: lets callers using NpgsqlDataSource (e.g. Aspire's
    // AddAzureNpgsqlDataSource with Managed Identity / SSL) keep auth and encryption
    // settings centralised — otherwise a raw NpgsqlConnection(connectionString) skips
    // the periodic password provider and SSL config attached to the data source.
    public PostgresLockProvider(NpgsqlDataSource dataSource, ILogger<PostgresLockProvider> logger)
    {
        _inner = new PostgresDistributedSynchronizationProvider(dataSource);
        _logger = logger;
    }

    public async Task<IAsyncDisposable?> TryAcquireAsync(string name, TimeSpan timeout, CancellationToken ct)
    {
        var @lock = _inner.CreateLock(name);

        // Releasing must never throw — a pooler in transaction mode makes pg_advisory_unlock
        // report "lock was not held", which would otherwise fail already-committed work.
        return SafeReleaseLockHandle.Wrap(await @lock.TryAcquireAsync(timeout, ct), name, _logger);
    }
}
