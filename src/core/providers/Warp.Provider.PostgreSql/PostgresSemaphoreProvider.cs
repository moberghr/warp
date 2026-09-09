using System.Collections.Concurrent;
using Medallion.Threading;
using Medallion.Threading.Postgres;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Warp.Core;

namespace Warp.Provider.PostgreSql;

// Postgres advisory locks are exclusive-only — Medallion.Threading.Postgres does not expose a
// counted semaphore. To support N-slot semantics we use the "N-distinct-named-locks" trick: derive
// lock names {name}:0..{name}:{maxCount-1} and try-acquire each in turn, returning the first that
// succeeds. This is the same algorithm Medallion's own SqlSemaphore uses on SQL Server. A random
// starting offset spreads concurrent acquirers across slots. A per-process cache lets sibling
// workers skip slots already held locally without a round-trip.
internal sealed class PostgresSemaphoreProvider : IWarpSemaphoreProvider
{
    private readonly IDistributedLockProvider _inner;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<(string Name, int Slot), byte> _heldInProcess = new();

    public PostgresSemaphoreProvider(string connectionString, ILogger<PostgresSemaphoreProvider> logger)
        : this(new PostgresDistributedSynchronizationProvider(connectionString), logger)
    {
    }

    // Data-source overload — same rationale as PostgresLockProvider: callers using
    // NpgsqlDataSource (Aspire / Managed Identity / pre-configured SSL) get the same
    // authentication and encryption settings on the lock connections that EF Core uses.
    public PostgresSemaphoreProvider(NpgsqlDataSource dataSource, ILogger<PostgresSemaphoreProvider> logger)
        : this(new PostgresDistributedSynchronizationProvider(dataSource), logger)
    {
    }

    // Test-only — lets unit tests spy on the underlying lock-name construction.
    internal PostgresSemaphoreProvider(IDistributedLockProvider inner, ILogger? logger = null)
    {
        _inner = inner;
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<IAsyncDisposable?> TryAcquireAsync(string name, int maxCount, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);

        if (maxCount == 1)
        {
            // Releasing must never throw — see SafeReleaseLockHandle.
            return SafeReleaseLockHandle.Wrap(
                await _inner.CreateLock(name).TryAcquireAsync(timeout, ct),
                name,
                _logger);
        }

        var start = Random.Shared.Next(maxCount);
        for (var k = 0; k < maxCount; k++)
        {
            var i = (start + k) % maxCount;

            if (_heldInProcess.ContainsKey((name, i)))
            {
                continue;
            }

            var handle = await _inner.CreateLock($"{name}:{i}").TryAcquireAsync(TimeSpan.Zero, ct);
            if (handle == null)
            {
                continue;
            }

            _heldInProcess.TryAdd((name, i), 0);

            // Wrapped OUTSIDE SlotHandle so a failing release still frees the in-process slot
            // (SlotHandle's finally) and then gets swallowed rather than surfacing to the caller.
            return SafeReleaseLockHandle.Wrap(
                new SlotHandle(handle, () => _heldInProcess.TryRemove((name, i), out _)),
                name,
                _logger);
        }

        return null;
    }

    private sealed class SlotHandle : IAsyncDisposable
    {
        private readonly IAsyncDisposable _inner;
        private readonly Action _onDispose;

        public SlotHandle(IAsyncDisposable inner, Action onDispose)
        {
            _inner = inner;
            _onDispose = onDispose;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _inner.DisposeAsync();
            }
            finally
            {
                _onDispose();
            }
        }
    }
}
