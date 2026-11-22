using Microsoft.EntityFrameworkCore;
using Npgsql;
using Respawn;
using Warp.Core.Interceptors;

namespace Warp.Tests.Fixtures;

/// <summary>
/// Per-test-class PostgreSQL fixture. Each test class gets its own database inside the
/// shared PostgreSQL container, cloned from a template that already has the schema, so test classes run fully in parallel up to xunit's
/// MaxParallelThreads. Tests within a class share the database and rely on Respawn between
/// tests for isolation.
/// </summary>
public class PostgreSqlClassFixture : IAsyncLifetime, IDatabaseFixture
{
    private Respawner _respawner = null!;
    private string _connectionString = null!;

    public string ConnectionString => _connectionString;

    public async ValueTask InitializeAsync()
    {
        TestLifecycleTrace.Record("PostgreSqlClassFixture.InitializeAsync starting");
        var databaseName = $"warp_t_{Guid.NewGuid():N}";

        TestLifecycleTrace.Record("SharedPostgreSqlContainer.CreateDatabaseAsync starting");
        var rawConnectionString = await SharedPostgreSqlContainer.CreateDatabaseAsync(
            databaseName,
            Xunit.TestContext.Current.CancellationToken);
        TestLifecycleTrace.Record("SharedPostgreSqlContainer.CreateDatabaseAsync returned");

        // Bake MaxPoolSize into the per-fixture connection string. EF builds an independent
        // NpgsqlDataSource per options instance (fixture's CreateContext, WarpTestServer's
        // worker registration, etc.) and each gets its own pool. Without a per-pool cap the
        // aggregate easily exceeds the testcontainer's max_connections (500) under parallel
        // test-class load — a small cap (50) keeps every pool bounded so even 4 parallel
        // classes × 2 pools × 50 stays well under the cap.
        _connectionString = new NpgsqlConnectionStringBuilder(rawConnectionString)
        {
            MaxPoolSize = 50,
        }.ConnectionString;

        // No EnsureCreated here: the database is a clone of the shared container's template, which
        // already has the schema (see SharedPostgreSqlContainer).
        TestLifecycleTrace.Record("Respawner.CreateAsync starting");
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(Xunit.TestContext.Current.CancellationToken);
        _respawner = await Respawner.CreateAsync(conn, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
        });
        TestLifecycleTrace.Record("Respawner.CreateAsync returned");
    }

    public async Task ResetAsync()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(Xunit.TestContext.Current.CancellationToken);
        await _respawner.ResetAsync(conn);
    }

    public TestContext CreateContext()
    {
        return new TestContext(new DbContextOptionsBuilder<TestContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new SaveChangesConcurrencyTokenInterceptor())
            .Options);
    }

    public async ValueTask DisposeAsync()
    {
        // Two-step cleanup. Both are necessary:
        //
        // 1. ClearPool drains the legacy Npgsql connection-string-keyed pool used by the
        //    fixture's own raw NpgsqlConnections (Respawner setup, ResetAsync). Without this,
        //    those connectors sit idle in the pool, authenticated to a soon-to-be-dropped DB.
        // 2. DropDatabaseAsync issues DROP DATABASE WITH (FORCE) on the admin connection,
        //    which terminates any remaining server-side sessions and reclaims the warp_t_
        //    name. This is what actually frees connections back to max_connections — EF's
        //    per-options NpgsqlDataSource pools and any other client-side pool keyed on this
        //    connection string still hold idle connectors after the class finishes, and the
        //    server-side FORCE is the only way to close them deterministically.
        NpgsqlConnection.ClearPool(new NpgsqlConnection(_connectionString));

        var databaseName = new NpgsqlConnectionStringBuilder(_connectionString).Database!;
        await SharedPostgreSqlContainer.DropDatabaseAsync(databaseName, CancellationToken.None);
    }
}
