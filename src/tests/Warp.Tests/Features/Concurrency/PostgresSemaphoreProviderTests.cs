using System.Collections.Concurrent;
using Medallion.Threading;
using Shouldly;
using Warp.Provider.PostgreSql;

namespace Warp.Tests.Features.Concurrency;

/// <summary>
/// Direct unit tests against <see cref="PostgresSemaphoreProvider"/> using a spy
/// over <see cref="IDistributedLockProvider"/>. These cover the provider-internal
/// logic (lock-name construction, per-process cache, slot-handle disposal contract)
/// that integration tests exercise only indirectly.
/// </summary>
[Trait("Category", "NoDb")]
public class PostgresSemaphoreProviderTests
{
    [TimedFact]
    public async Task MaxCountOne_AcquiresBaseNameUnchanged()
    {
        // The maxCount=1 fast path passes the name through to CreateLock(name) directly —
        // proving the Mutex flow keeps using "warp:concurrency:k" (no ":0" suffix).
        var spy = new SpyLockProvider();
        var provider = new PostgresSemaphoreProvider(spy);

        var handle = await provider.TryAcquireAsync("k", maxCount: 1, TimeSpan.Zero, CancellationToken.None);

        handle.ShouldNotBeNull();
        spy.NamesRequested.ShouldBe(["k"]);

        await handle.DisposeAsync();
    }

    [TimedFact]
    public async Task MaxCountGreaterThanOne_AcquiresSlotKeyedNames()
    {
        // The maxCount>1 path must construct slot-keyed names: "k:0", "k:1", ..., "k:{N-1}".
        // Random start offset means the order can vary, but the SET of attempted names must
        // equal the slot-keyed namespace and never include the bare "k".
        var spy = new SpyLockProvider();
        var provider = new PostgresSemaphoreProvider(spy);

        var handle = await provider.TryAcquireAsync("k", maxCount: 3, TimeSpan.Zero, CancellationToken.None);

        handle.ShouldNotBeNull();

        // First acquire succeeds on whatever slot it tries first; only that one name appears.
        spy.NamesRequested.Length.ShouldBe(1);
        spy.NamesRequested[0].ShouldStartWith("k:");
        spy.NamesRequested[0].ShouldNotBe("k");

        await handle.DisposeAsync();
    }

    [TimedFact]
    public async Task PostgresProvider_MutexAndSemaphore_UseDisjointLockNames()
    {
        // PG-specific contract: PostgresSemaphoreProvider's maxCount=1 fast path uses the bare
        // base name ("k"), while the maxCount>1 path uses slot-keyed names ("k:0".."k:{N-1}").
        // So a [Mutex("k")] acquire and a concurrent [Semaphore("k", N)] acquire don't share
        // underlying lock names — combined concurrency on PG is mutex_limit + semaphore_limit.
        //
        // SQL Server differs: SqlServerSemaphoreProvider always delegates to Medallion's
        // SqlSemaphore which uses lock names "k0".."k{N-1}" regardless of maxCount, so on SQL
        // Server [Mutex("k")] and [Semaphore("k", N)] DO share the slot pool. The spec/docs
        // call out the backend-specific behavior; this test locks in the PG side.
        //
        // A future refactor that unified the PG namespace (e.g. used "k:0" at maxCount=1 too)
        // would fail this test — it would also align PG with SQL Server's behavior, but is a
        // breaking change for existing Mutex deployments and should go through review.
        var spy = new SpyLockProvider();
        var provider = new PostgresSemaphoreProvider(spy);

        var mutexHandle = await provider.TryAcquireAsync("k", maxCount: 1, TimeSpan.Zero, CancellationToken.None);
        var semaphoreHandle = await provider.TryAcquireAsync("k", maxCount: 5, TimeSpan.Zero, CancellationToken.None);

        mutexHandle.ShouldNotBeNull();
        semaphoreHandle.ShouldNotBeNull();

        spy.NamesRequested.ShouldContain("k");
        spy.NamesRequested.Where(n => n.StartsWith("k:", StringComparison.Ordinal)).ShouldNotBeEmpty();

        // The mutex's "k" acquire and the semaphore's "k:N" acquire are distinct lock names,
        // so the spy registered both as separately-held.
        spy.HeldNames.ShouldContain("k");
        spy.HeldNames.Any(n => n.StartsWith("k:", StringComparison.Ordinal)).ShouldBeTrue();

        await semaphoreHandle.DisposeAsync();
        await mutexHandle.DisposeAsync();
    }

    [TimedFact]
    public async Task PerProcessCache_HeldSlot_SkippedOnNextCall()
    {
        // After acquiring slot i, a second TryAcquireAsync with the same name must skip slot i
        // (cache hit — never re-queries the inner provider for that index) and try other slots.
        // We force this by making the spy always return null for fresh acquires beyond the
        // first one — except the cache-skip means slot i is not even probed a second time.
        var spy = new SpyLockProvider();
        var provider = new PostgresSemaphoreProvider(spy);

        var first = await provider.TryAcquireAsync("k", maxCount: 3, TimeSpan.Zero, CancellationToken.None);
        first.ShouldNotBeNull();
        var firstSlotName = spy.NamesRequested[^1];

        // Now make ALL further inner-provider acquires fail (return null handle).
        spy.SimulateAllBusy = true;

        var second = await provider.TryAcquireAsync("k", maxCount: 3, TimeSpan.Zero, CancellationToken.None);
        second.ShouldBeNull();

        // The cache-hit on slot i means the first slot's name is NOT re-requested — the
        // provider only probes the OTHER 2 slots. So total names requested after the second
        // call is firstAcquireCount(=1) + 2 (the other slots), never 1 + 3.
        spy.NamesRequested.Count(n => string.Equals(n, firstSlotName, StringComparison.Ordinal)).ShouldBe(1);

        await first.DisposeAsync();
    }

    [TimedFact]
    public async Task SlotHandle_DisposeAsync_RemovesCacheEntry_EvenIfInnerThrows()
    {
        // The provider's SlotHandle wraps the inner handle in a try/finally; the cache-eviction
        // step must run regardless of whether the inner DisposeAsync throws.
        //
        // This test runs at maxCount=2 because the maxCount=1 fast path bypasses SlotHandle
        // entirely (returns the inner handle directly, with no cache entry). At maxCount=2,
        // the SlotHandle wraps the inner handle and the cache contains the held slot.
        //
        // After a throwing dispose, we set spy.SimulateAllBusy = true and probe acquire again.
        // The probe-count distinguishes the two scenarios:
        //   - Cache cleared (correct): second acquire probes BOTH slots → 2 spy.CreateLock calls
        //   - Cache leaked (regression): second acquire skips the cached slot → 1 spy.CreateLock call
        var spy = new SpyLockProvider { ThrowOnDispose = true };
        var provider = new PostgresSemaphoreProvider(spy);

        var first = await provider.TryAcquireAsync("k", maxCount: 2, TimeSpan.Zero, CancellationToken.None);
        first.ShouldNotBeNull();
        spy.NamesRequested.Length.ShouldBe(1, "first acquire probes one slot before succeeding");

        // Inner DisposeAsync throws; the SlotHandle's finally must still clear the cache entry,
        // and SafeReleaseLockHandle (wrapped around the SlotHandle) must swallow the throw —
        // releasing a Warp lock never surfaces to the caller, it only logs a Warning.
        await Should.NotThrowAsync(async () => await first.DisposeAsync());

        // Now force every subsequent inner acquire to fail. If the cache was cleared in the
        // finally, the next TryAcquireAsync probes BOTH slots (cache hit on neither). If the
        // cache leaked, it skips the previously-held slot and only probes the other.
        spy.SimulateAllBusy = true;

        var second = await provider.TryAcquireAsync("k", maxCount: 2, TimeSpan.Zero, CancellationToken.None);
        second.ShouldBeNull();

        var probesInSecondCall = spy.NamesRequested.Length - 1;
        probesInSecondCall.ShouldBe(2, "cache was cleared so both slots must be probed; if it leaked, only one would be");
    }

    private sealed class SpyLockProvider : IDistributedLockProvider
    {
        private readonly ConcurrentBag<string> _names = [];
        private readonly ConcurrentDictionary<string, byte> _held = [];

        public bool SimulateAllBusy { get; set; }

        public bool ThrowOnDispose { get; set; }

        public string[] NamesRequested => [.. _names];

        public string[] HeldNames => [.. _held.Keys];

        public IDistributedLock CreateLock(string name)
        {
            _names.Add(name);

            return new SpyLock(name, this);
        }

        internal bool TryAcquire(string name) =>
            !SimulateAllBusy && _held.TryAdd(name, 0);

        internal void Release(string name) => _held.TryRemove(name, out _);
    }

    private sealed class SpyLock : IDistributedLock
    {
        private readonly SpyLockProvider _provider;

        public SpyLock(string name, SpyLockProvider provider)
        {
            Name = name;
            _provider = provider;
        }

        public string Name { get; }

        public ValueTask<IDistributedSynchronizationHandle?> TryAcquireAsync(
            TimeSpan timeout = default, CancellationToken cancellationToken = default)
        {
            if (_provider.TryAcquire(Name))
            {
                IDistributedSynchronizationHandle handle = new SpyHandle(Name, _provider);

                return ValueTask.FromResult<IDistributedSynchronizationHandle?>(handle);
            }

            return ValueTask.FromResult<IDistributedSynchronizationHandle?>(null);
        }

        public ValueTask<IDistributedSynchronizationHandle> AcquireAsync(
            TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IDistributedSynchronizationHandle? TryAcquire(
            TimeSpan timeout = default, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IDistributedSynchronizationHandle Acquire(
            TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class SpyHandle : IDistributedSynchronizationHandle
    {
        private readonly string _name;
        private readonly SpyLockProvider _provider;

        public SpyHandle(string name, SpyLockProvider provider)
        {
            _name = name;
            _provider = provider;
        }

        public CancellationToken HandleLostToken => CancellationToken.None;

        public void Dispose() => DisposeCore();

        public ValueTask DisposeAsync()
        {
            DisposeCore();

            return default;
        }

        private void DisposeCore()
        {
            _provider.Release(_name);
            if (_provider.ThrowOnDispose)
            {
                throw new InvalidOperationException("Spy: configured to throw on dispose");
            }
        }
    }
}
