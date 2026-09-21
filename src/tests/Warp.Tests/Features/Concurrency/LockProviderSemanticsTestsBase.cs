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
/// The WAITING half of <see cref="IWarpLockProvider"/>, against a real database on both providers.
/// <para>
/// Everything else in the concurrency suite acquires with <c>TimeSpan.Zero</c>, because that is what
/// <c>ConcurrencyPipelineBehavior</c> and <c>SagaHandlerProxy</c> use. But two production sites depend
/// on the lock provider actually BLOCKING for a bounded time —
/// <c>RecurringJobPublisher.AddOrUpdateRecurringJob</c> at host startup
/// (<c>RecurringJobPublisherConstants.LockTimeout</c>) and <c>RateLimitPipelineBehavior</c> per gated
/// job — and neither behaviour was covered. An implementation that returned early, ignored the
/// cancellation token, or busy-spun would have passed the whole suite.
/// </para>
/// <para>
/// Deliberately written against the public <see cref="IWarpLockProvider"/> seam resolved from DI, not
/// against a spy over the underlying lock library. <c>PostgresSemaphoreProviderTests</c> fakes
/// <c>IDistributedLockProvider</c>, which is the library's own abstraction — those tests validate our
/// wrapper but would supply no safety if the primitive underneath were ever replaced, because they
/// would be rewritten against whatever new seam appeared. These survive that.
/// </para>
/// </summary>
[GenerateDatabaseTests]
public abstract class LockProviderSemanticsTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected LockProviderSemanticsTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task TryAcquireAsync_UncontendedWithTimeout_ReturnsPromptly()
    {
        await using var provider = BuildProvider("solo");
        var locks = provider.GetRequiredService<IWarpLockProvider>();

        var handle = await locks.TryAcquireAsync(Key("uncontended"), TimeSpan.FromSeconds(5), TestCancellation);

        handle.ShouldNotBeNull();
        await handle.DisposeAsync();
    }

    [TimedFact]
    public async Task TryAcquireAsync_HeldLock_WaitsAndSucceedsOnceReleased()
    {
        // The behaviour AddOrUpdateRecurringJob depends on: a second host starting while the first
        // holds the registration lock must wait, then proceed — not fail.
        await using var holderScope = BuildProvider("holder");
        await using var waiterScope = BuildProvider("waiter");
        var key = Key("waits");

        var held = await holderScope.GetRequiredService<IWarpLockProvider>()
            .TryAcquireAsync(key, TimeSpan.Zero, TestCancellation);
        held.ShouldNotBeNull();

        // Start waiting BEFORE the release, so the acquire genuinely blocks rather than finding the
        // lock already free — otherwise this test passes against an implementation that never waits.
        var waiting = waiterScope.GetRequiredService<IWarpLockProvider>()
            .TryAcquireAsync(key, TimeSpan.FromSeconds(8), TestCancellation);

        waiting.IsCompleted.ShouldBeFalse("the acquire should still be blocked while the lock is held");

        await held.DisposeAsync();

        var acquired = await waiting;

        acquired.ShouldNotBeNull("the waiter should take the lock once the holder released it");
        await acquired.DisposeAsync();
    }

    [TimedFact]
    public async Task TryAcquireAsync_HeldLock_ReturnsNullAfterTheTimeoutElapses()
    {
        // The other half of the contract: a bounded wait must END. An implementation that waited
        // forever would hang a host startup rather than surfacing a TimeoutException.
        await using var holderScope = BuildProvider("holder");
        await using var waiterScope = BuildProvider("waiter");
        var key = Key("expires");

        var held = await holderScope.GetRequiredService<IWarpLockProvider>()
            .TryAcquireAsync(key, TimeSpan.Zero, TestCancellation);
        held.ShouldNotBeNull();

        try
        {
            var started = DateTime.UtcNow;
            var timeout = TimeSpan.FromSeconds(1);

            var contender = await waiterScope.GetRequiredService<IWarpLockProvider>()
                .TryAcquireAsync(key, timeout, TestCancellation);

            contender.ShouldBeNull();

            // Waited rather than failed fast. A generous floor (half the timeout) — the point is to
            // catch "returned immediately", not to assert timer precision.
            (DateTime.UtcNow - started).ShouldBeGreaterThan(timeout / 2);
        }
        finally
        {
            await held.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task TryAcquireAsync_CancelledWhileWaiting_StopsWaiting()
    {
        // The token must be honoured DURING the wait, not just checked before it. A blocked acquire
        // that ignores cancellation holds a worker (and a connection) until its timeout regardless of
        // host shutdown.
        await using var holderScope = BuildProvider("holder");
        await using var waiterScope = BuildProvider("waiter");
        var key = Key("cancelled");

        var held = await holderScope.GetRequiredService<IWarpLockProvider>()
            .TryAcquireAsync(key, TimeSpan.Zero, TestCancellation);
        held.ShouldNotBeNull();

        try
        {
            using var cts = new CancellationTokenSource();
            var waiting = waiterScope.GetRequiredService<IWarpLockProvider>()
                .TryAcquireAsync(key, TimeSpan.FromSeconds(20), cts.Token);

            waiting.IsCompleted.ShouldBeFalse();
            await cts.CancelAsync();

            // Either shape is acceptable — the contract is that it STOPS, well inside the 20s timeout
            // it was given. Pinning which one is what a swap of the primitive would have to preserve,
            // so record it rather than assert one arbitrarily.
            try
            {
                var result = await waiting;
                result.ShouldBeNull("a cancelled wait must not return a held lock");
            }
            catch (OperationCanceledException)
            {
                // The shape the current implementation uses.
            }
        }
        finally
        {
            await held.DisposeAsync();
        }
    }

    private static CancellationToken TestCancellation => Xunit.TestContext.Current.CancellationToken;

    /// <summary>Unique per test and per provider, so a shared database cannot let two tests contend on one key.</summary>
    private static string Key(string scenario) => $"warp:test:lock:{scenario}:{Guid.NewGuid():N}";

    /// <summary>
    /// A container per call, so each returns an independent <see cref="IWarpLockProvider"/> on its own
    /// connection — two providers in one container would share a singleton and the contention under
    /// test would collapse into the same process-local handle.
    /// <para>
    /// The provider package is chosen from the fixture's own context rather than being passed in, which
    /// is what lets one generated base cover PostgreSQL and SQL Server from a single source file.
    /// </para>
    /// </summary>
    private ServiceProvider BuildProvider(string role)
    {
        using var probe = _fixture.CreateContext();
        var isPostgres = probe.Database.ProviderName!.Contains("Npgsql", StringComparison.Ordinal);
        var connectionString = WithApplicationName(probe.Database.GetConnectionString()!, role, isPostgres);

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

    /// <summary>
    /// A per-role <c>Application Name</c>, which both Npgsql and SqlClient include in the pool key.
    /// <para>
    /// Not cosmetic — it is what makes the holder and the waiter independent. Medallion MULTIPLEXES
    /// session locks: its connection pool is keyed by connection string, so two providers sharing one
    /// string get put on the same physical session. Cancelling the waiter then aborts a batch on a
    /// session the holder is also using, and SQL Server answers with
    /// "another request is running in the same session, which makes the session busy" instead of a
    /// clean cancellation. That is how the first version of this test failed.
    /// </para>
    /// </summary>
    private static string WithApplicationName(string connectionString, string role, bool isPostgres)
    {
        var name = $"warp-test-{role}-{Guid.NewGuid():N}";

        return isPostgres
            ? new Npgsql.NpgsqlConnectionStringBuilder(connectionString) { ApplicationName = name }.ConnectionString
            : new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString) { ApplicationName = name }.ConnectionString;
    }
}
