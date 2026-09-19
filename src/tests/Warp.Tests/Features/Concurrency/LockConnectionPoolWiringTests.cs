using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Warp.Core;
using Warp.Provider.PostgreSql;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData;

namespace Warp.Tests.Features.Concurrency;

/// <summary>
/// The wiring half of the advisory-lock pool split, against a real database.
/// <para>
/// <see cref="LockConnectionPoolIsolationTests"/> pins what
/// <c>ResolveLockConnectionString</c> returns, but a refactor that points
/// <c>UsePostgreSql</c>'s lock registrations back at <c>ResolveConnectionString</c> would leave that
/// test green while restoring the regression in full — the helper would still be correct, just
/// unused. This test resolves the provider the way an application does and looks at what Postgres
/// actually sees, so it fails on that edit.
/// </para>
/// </summary>
[Trait("Category", "PostgreSql")]
public class LockConnectionPoolWiringTests : IAsyncLifetime, IClassFixture<PostgreSqlClassFixture>
{
    private readonly PostgreSqlClassFixture _fixture;

    public LockConnectionPoolWiringTests(PostgreSqlClassFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task RegisteredSemaphoreProvider_HoldsItsLockOnASessionOutsideTheDbContextPool()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TestContext>(options => options.UseNpgsql(AsApplication("guard-semaphore")));
        services.AddWarp<TestContext>(config => config.UsePostgreSql());

        await using var root = services.BuildServiceProvider();
        var semaphores = root.GetRequiredService<IWarpSemaphoreProvider>();

        var handle = await semaphores.TryAcquireAsync("warp:pool-guard", 1, TimeSpan.Zero, TestContextCancellation);
        handle.ShouldNotBeNull();

        try
        {
            // A session-scoped advisory lock holds its connection open for as long as the handle
            // lives (§2.16), so while we are inside this block Postgres must be able to see that
            // session — and its application_name is the whole of the pool separation.
            var lockSessions = await CountSessionsAsync("guard-semaphore:warp-locks");

            lockSessions.ShouldBeGreaterThan(
                0,
                "the lock provider should hold its session on the suffixed connection string, which is what gives it its own Npgsql pool");
        }
        finally
        {
            await handle.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task RegisteredLockProvider_HoldsItsLockOnASessionOutsideTheDbContextPool()
    {
        // The lock provider is a SECOND registration beside the semaphore's, and reverting either one
        // alone is a silent regression for its own consumers — IWarpLockProvider backs rate limiting,
        // sagas, recurring-job registration and the server-task loop (§2.16). One test per call site,
        // because a guard on one of them passes happily while the other is undone.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TestContext>(options => options.UseNpgsql(AsApplication("guard-lock")));
        services.AddWarp<TestContext>(config => config.UsePostgreSql());

        await using var root = services.BuildServiceProvider();
        var locks = root.GetRequiredService<IWarpLockProvider>();

        var handle = await locks.TryAcquireAsync("warp:pool-guard:lock", TimeSpan.Zero, TestContextCancellation);
        handle.ShouldNotBeNull();

        try
        {
            var lockSessions = await CountSessionsAsync("guard-lock:warp-locks");

            lockSessions.ShouldBeGreaterThan(0, "the lock provider should hold its session on its own pool, like the semaphore provider");
        }
        finally
        {
            await handle.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task DataSourceRegistration_WithALockDataSource_UsesTheSuppliedPool()
    {
        // The remedy for that path: the host builds its own lock data source — keeping whatever auth
        // configuration its DbContext data source carries, since Warp copies nothing — and hands it over.
        await using var contextDataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        await using var lockDataSource = NpgsqlDataSource.Create(
            new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { ApplicationName = "host-supplied-warp-locks" }.ConnectionString);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TestContext>(options => options.UseNpgsql(contextDataSource));
        services.AddWarp<TestContext>(config => config.UsePostgreSql(lockDataSource));

        await using var root = services.BuildServiceProvider();
        var semaphores = root.GetRequiredService<IWarpSemaphoreProvider>();

        var handle = await semaphores.TryAcquireAsync("warp:pool-guard:supplied", 1, TimeSpan.Zero, TestContextCancellation);
        handle.ShouldNotBeNull();

        try
        {
            var lockSessions = await CountSessionsAsync("host-supplied-warp-locks");

            lockSessions.ShouldBeGreaterThan(0, "the supplied lock data source should take precedence over the DbContext's");
        }
        finally
        {
            await handle.DisposeAsync();
        }
    }

    private static CancellationToken TestContextCancellation => Xunit.TestContext.Current.CancellationToken;

    /// <summary>
    /// The fixture's connection string carrying a per-test <c>Application Name</c>, so the lock pool
    /// Core derives from it (<c>{name}:warp-locks</c>) is unique to this test.
    /// <para>
    /// Without this every test in the class looks for the same bare <c>warp-locks</c> session, and
    /// Npgsql pools outlive the <c>ServiceProvider</c> that made them — so whichever test ran first
    /// leaves an idle connector that satisfies the next one's assertion for it. That is not a
    /// hypothetical: a guard written that way passed with its own call site reverted.
    /// </para>
    /// </summary>
    private string AsApplication(string applicationName) =>
        new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { ApplicationName = applicationName }.ConnectionString;

    /// <summary>
    /// Counts live sessions by EXACT application_name, scoped to this fixture's own database.
    /// <para>
    /// Both narrowings matter. <c>pg_stat_activity</c> is cluster-wide, and every DB-backed test class
    /// gets its own database on a shared container — without the <c>datname</c> filter a sibling class
    /// running in parallel would satisfy this assertion on our behalf. And the count must be of a
    /// presence, never an absence: Npgsql pools outlive the <c>ServiceProvider</c> that created them, so
    /// an idle connector from an earlier test in this same class is still listed here.
    /// </para>
    /// </summary>
    private async Task<long> CountSessionsAsync(string applicationName)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync(TestContextCancellation);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM pg_stat_activity WHERE application_name = @name AND datname = current_database()";
        command.Parameters.AddWithValue("name", applicationName);

        return (long)(await command.ExecuteScalarAsync(TestContextCancellation))!;
    }
}
