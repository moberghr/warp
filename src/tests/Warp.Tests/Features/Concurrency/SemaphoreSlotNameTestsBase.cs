using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Provider.PostgreSql;
using Warp.Provider.SqlServer;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData;

namespace Warp.Tests.Features.Concurrency;

/// <summary>
/// Two semaphore keys never share a slot, on either database, even when one key is another plus a suffix that
/// looks like a slot index. PostgreSQL's slots are "{name}:{index}" advisory locks, and the index is the digits
/// after the last ':', so a slot name splits only one way. SQL Server's (Medallion) ticket names carry a hash of
/// the whole semaphore name, so two names meet only on a hash collision. Both cases are pinned here against the
/// real databases because the shape is easy to get wrong: a "{name}{index}" scheme would hand slots 10 and 11 of
/// "t" at 12 slots to "t1" at 2.
/// </summary>
[GenerateDatabaseTests]
public abstract class SemaphoreSlotNameTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected SemaphoreSlotNameTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedTheory]
    [InlineData("1")]
    [InlineData(":1")]
    public async Task TryAcquireAsync_KeyThatExtendsAnotherKey_DoesNotTakeItsSlots(string suffix)
    {
        await using var services = BuildProvider();
        var semaphores = services.GetRequiredService<IWarpSemaphoreProvider>();
        var key = $"warp:concurrency:slots-{Guid.NewGuid():N}";
        var handles = new List<IAsyncDisposable>();

        try
        {
            // Fill both slots of the longer key.
            for (var i = 0; i < 2; i++)
            {
                handles.Add((await semaphores.TryAcquireAsync(key + suffix, 2, TimeSpan.Zero, Ct)).ShouldNotBeNull());
            }

            // Every one of the shorter key's 12 slots is still free, including 10 and 11.
            for (var i = 0; i < 12; i++)
            {
                var handle = await semaphores.TryAcquireAsync(key, 12, TimeSpan.Zero, Ct);
                handle.ShouldNotBeNull($"slot {i + 1} of 12 was taken by the other key");
                handles.Add(handle);
            }
        }
        finally
        {
            foreach (var handle in handles)
            {
                await handle.DisposeAsync();
            }
        }
    }

    [TimedFact]
    public async Task TryAcquireAsync_SameKey_StillSharesItsSlots()
    {
        // The separator must not split one key into two: N=2 holders fill it, a third is refused.
        await using var services = BuildProvider();
        var semaphores = services.GetRequiredService<IWarpSemaphoreProvider>();
        var key = $"warp:concurrency:shared-{Guid.NewGuid():N}";

        await using var first = (await semaphores.TryAcquireAsync(key, 2, TimeSpan.Zero, Ct)).ShouldNotBeNull();
        await using var second = (await semaphores.TryAcquireAsync(key, 2, TimeSpan.Zero, Ct)).ShouldNotBeNull();

        var third = await semaphores.TryAcquireAsync(key, 2, TimeSpan.Zero, Ct);

        third.ShouldBeNull();
    }

    private ServiceProvider BuildProvider()
    {
        using var probe = _fixture.CreateContext();
        var isPostgres = probe.Database.ProviderName!.Contains("Npgsql", StringComparison.Ordinal);
        var connectionString = probe.Database.GetConnectionString()!;

        var services = new ServiceCollection();
        services.AddLogging();

        if (isPostgres)
        {
            services.AddDbContext<TestContext>(options => options.UseNpgsql(connectionString));
            services.AddWarp<TestContext>(config => config.UsePostgreSql());
        }
        else
        {
            services.AddDbContext<TestContext>(options => options.UseSqlServer(connectionString));
            services.AddWarp<TestContext>(config => config.UseSqlServer());
        }

        return services.BuildServiceProvider();
    }
}
