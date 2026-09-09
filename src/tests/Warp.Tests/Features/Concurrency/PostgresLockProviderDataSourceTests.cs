using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Warp.Provider.PostgreSql;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Features.Concurrency;

// Real-PG behavioral coverage of the new NpgsqlDataSource ctors on
// PostgresLockProvider and PostgresSemaphoreProvider. The spy-based tests in
// PostgresSemaphoreProviderTests cover slot-naming and cache logic with a fake
// IDistributedLockProvider, but they don't exercise the path where the inner
// Medallion provider opens connections from a real NpgsqlDataSource. These tests
// fill that gap by acquiring through the new ctors against the shared container.
[Trait("Category", "PostgreSql")]
public class PostgresLockProviderDataSourceTests : IAsyncLifetime, IClassFixture<PostgreSqlClassFixture>
{
    private readonly PostgreSqlClassFixture _fixture;

    public PostgresLockProviderDataSourceTests(PostgreSqlClassFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task LockProvider_WithDataSource_AcquiresAndReleases()
    {
        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var provider = new PostgresLockProvider(dataSource, NullLogger<PostgresLockProvider>.Instance);

        var handle = await provider.TryAcquireAsync("warp:ds:lock", TimeSpan.FromSeconds(1), CancellationToken.None);

        handle.ShouldNotBeNull();
        await handle.DisposeAsync();
    }

    [TimedFact]
    public async Task LockProvider_WithDataSource_HeldLock_BlocksSecondAcquire()
    {
        // Holding the lock through one data-source-backed provider must block a second
        // acquirer through an independent data-source-backed provider — proves the
        // connections it opens really hit the same Postgres advisory-lock namespace.
        await using var dataSourceA = NpgsqlDataSource.Create(_fixture.ConnectionString);
        await using var dataSourceB = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var providerA = new PostgresLockProvider(dataSourceA, NullLogger<PostgresLockProvider>.Instance);
        var providerB = new PostgresLockProvider(dataSourceB, NullLogger<PostgresLockProvider>.Instance);

        var held = await providerA.TryAcquireAsync("warp:ds:lock:contended", TimeSpan.FromSeconds(1), CancellationToken.None);
        held.ShouldNotBeNull();

        var contender = await providerB.TryAcquireAsync("warp:ds:lock:contended", TimeSpan.Zero, CancellationToken.None);
        contender.ShouldBeNull();

        await held.DisposeAsync();
    }

    [TimedFact]
    public async Task SemaphoreProvider_WithDataSource_AcquiresSlot()
    {
        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var provider = new PostgresSemaphoreProvider(dataSource, NullLogger<PostgresSemaphoreProvider>.Instance);

        var handle = await provider.TryAcquireAsync("warp:ds:sem", maxCount: 2, TimeSpan.FromSeconds(1), CancellationToken.None);

        handle.ShouldNotBeNull();
        await handle.DisposeAsync();
    }

    [TimedFact]
    public async Task SemaphoreProvider_WithDataSource_ExhaustedSlots_ReturnsNull()
    {
        // Hold both slots through one data-source-backed semaphore, then verify a third
        // acquire (through an independent data-source-backed provider against the same
        // key + maxCount) returns null. Pins that the data-source ctor lands on the same
        // slot namespace as the connection-string ctor would.
        await using var dataSourceA = NpgsqlDataSource.Create(_fixture.ConnectionString);
        await using var dataSourceB = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var providerA = new PostgresSemaphoreProvider(dataSourceA, NullLogger<PostgresSemaphoreProvider>.Instance);
        var providerB = new PostgresSemaphoreProvider(dataSourceB, NullLogger<PostgresSemaphoreProvider>.Instance);

        var slot0 = await providerA.TryAcquireAsync("warp:ds:sem:full", maxCount: 2, TimeSpan.Zero, CancellationToken.None);
        var slot1 = await providerA.TryAcquireAsync("warp:ds:sem:full", maxCount: 2, TimeSpan.Zero, CancellationToken.None);
        slot0.ShouldNotBeNull();
        slot1.ShouldNotBeNull();

        var contender = await providerB.TryAcquireAsync("warp:ds:sem:full", maxCount: 2, TimeSpan.Zero, CancellationToken.None);
        contender.ShouldBeNull();

        await slot1.DisposeAsync();
        await slot0.DisposeAsync();
    }
}
