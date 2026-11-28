using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Respawn;
using Warp.Core.Interceptors;

namespace Warp.Tests.Fixtures;

/// <summary>
/// Per-test-class SQL Server fixture. Each test class gets its own database inside the
/// shared SQL Server container, so test classes run fully in parallel up to xunit's
/// MaxParallelThreads. Tests within a class share the database and rely on Respawn between
/// tests for isolation.
/// <para>
/// Service Broker is NOT enabled by default — it adds ~100–200ms per fixture-init via
/// <c>ALTER DATABASE … SET ENABLE_BROKER</c>. Tests that exercise the SQL Server
/// notification transport (push) must use <see cref="SqlServerPushClassFixture"/> instead.
/// </para>
/// </summary>
public class SqlServerClassFixture : IAsyncLifetime, IDatabaseFixture
{
    private Respawner _respawner = null!;
    private string _connectionString = null!;

    public string ConnectionString => _connectionString;

    protected virtual bool EnableServiceBroker => false;

    public async ValueTask InitializeAsync()
    {
        TestLifecycleTrace.Record("SqlServerClassFixture.InitializeAsync starting");
        var databaseName = $"warp_t_{Guid.NewGuid():N}";

        TestLifecycleTrace.Record("SharedSqlServerContainer.CreateDatabaseAsync starting");
        var rawConnectionString = await SharedSqlServerContainer.CreateDatabaseAsync(
            databaseName,
            EnableServiceBroker,
            Xunit.TestContext.Current.CancellationToken);
        TestLifecycleTrace.Record("SharedSqlServerContainer.CreateDatabaseAsync returned");

        // Cap MaxPoolSize per fixture for parity with PostgreSqlClassFixture — see that
        // file's comment for rationale. SQL Server's default max_user_connections is
        // 32,767 so this isn't a correctness gate the way it is for PG, but the symmetry
        // keeps per-class connection budget bounded.
        _connectionString = new SqlConnectionStringBuilder(rawConnectionString)
        {
            MaxPoolSize = 50,
        }.ConnectionString;

        TestLifecycleTrace.Record("EnsureCreatedAsync starting");
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync(Xunit.TestContext.Current.CancellationToken);
        TestLifecycleTrace.Record("EnsureCreatedAsync returned");

        TestLifecycleTrace.Record("Respawner.CreateAsync starting");
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(Xunit.TestContext.Current.CancellationToken);
        _respawner = await Respawner.CreateAsync(conn, new RespawnerOptions
        {
            DbAdapter = DbAdapter.SqlServer,
        });
        TestLifecycleTrace.Record("Respawner.CreateAsync returned");
    }

    public async Task ResetAsync()
    {
        await RunningTestServers.WaitForAllStoppedAsync(this);
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(Xunit.TestContext.Current.CancellationToken);
        await _respawner.ResetAsync(conn);
    }

    public TestContext CreateContext()
    {
        return new TestContext(new DbContextOptionsBuilder<TestContext>()
            .UseSqlServer(_connectionString)
            .AddInterceptors(new SaveChangesConcurrencyTokenInterceptor())
            .Options);
    }

    public async ValueTask DisposeAsync()
    {
        // The class's last test may have timed out with its server still stopping (#364).
        await RunningTestServers.WaitForAllStoppedAsync(this);

        // Mirror of PostgreSqlClassFixture.DisposeAsync. SQL Server's default max user
        // connections is 32,767 so per-fixture leak doesn't cause "too many connections"
        // failures the way PG's max_connections=500 does, but the latent leak still wastes
        // sessions and may contribute to flakiness under contention. SET SINGLE_USER WITH
        // ROLLBACK IMMEDIATE is the SQL Server analogue of PG's WITH (FORCE).
        SqlConnection.ClearPool(new SqlConnection(_connectionString));

        var databaseName = new SqlConnectionStringBuilder(_connectionString).InitialCatalog;
        await SharedSqlServerContainer.DropDatabaseAsync(databaseName, CancellationToken.None);
    }
}

/// <summary>
/// SQL Server fixture variant with Service Broker enabled. Use for test classes that exercise
/// <c>SqlServerNotificationTransport</c> directly or call <c>UseDatabasePush()</c> in their
/// server config — without Broker, the transport's listener can't subscribe.
/// </summary>
public sealed class SqlServerPushClassFixture : SqlServerClassFixture
{
    protected override bool EnableServiceBroker => true;
}
