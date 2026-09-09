using Medallion.Threading.SqlServer;
using Microsoft.Extensions.Logging;
using Warp.Core;

namespace Warp.Provider.SqlServer;

internal sealed class SqlServerSemaphoreProvider : IWarpSemaphoreProvider
{
    private readonly SqlDistributedSynchronizationProvider _inner;
    private readonly ILogger _logger;

    public SqlServerSemaphoreProvider(string connectionString, ILogger<SqlServerSemaphoreProvider> logger)
    {
        _inner = new SqlDistributedSynchronizationProvider(connectionString);
        _logger = logger;
    }

    public async Task<IAsyncDisposable?> TryAcquireAsync(string name, int maxCount, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);

        // Releasing must never throw — see SafeReleaseLockHandle.
        return SafeReleaseLockHandle.Wrap(
            await _inner.CreateSemaphore(name, maxCount).TryAcquireAsync(timeout, ct),
            name,
            _logger);
    }
}
