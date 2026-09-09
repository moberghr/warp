using Medallion.Threading;
using Medallion.Threading.SqlServer;
using Microsoft.Extensions.Logging;
using Warp.Core;

namespace Warp.Provider.SqlServer;

internal sealed class SqlServerLockProvider : IWarpLockProvider
{
    private readonly IDistributedLockProvider _inner;
    private readonly ILogger _logger;

    public SqlServerLockProvider(string connectionString, ILogger<SqlServerLockProvider> logger)
    {
        _inner = new SqlDistributedSynchronizationProvider(connectionString);
        _logger = logger;
    }

    public async Task<IAsyncDisposable?> TryAcquireAsync(string name, TimeSpan timeout, CancellationToken ct)
    {
        var @lock = _inner.CreateLock(name);

        // Releasing must never throw — see SafeReleaseLockHandle.
        return SafeReleaseLockHandle.Wrap(await @lock.TryAcquireAsync(timeout, ct), name, _logger);
    }
}
