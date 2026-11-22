using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Warp.Tests.Fixtures;

// Process-wide shared PostgreSqlContainer. See SharedSqlServerContainer for rationale.
// PG containers are lighter than SQL Server (~200MB vs ~2GB), but consolidating for
// symmetry also halves container boot time (1×N seconds instead of 4×N seconds).
//
// The schema is built ONCE, into a template database, and every test class's database is a clone of
// it (CREATE DATABASE ... TEMPLATE), the approach straumur-backend uses. Building it per class with
// EnsureCreated cost ~300 ms against ~30 ms for a clone, measured on this schema, across 212 database
// test classes. Tests within a class still share the clone and reset it with Respawn (~30 ms): a clone
// per TEST would cost ~150 ms each with its drop, slower than the reset for tests that start from empty
// tables.
internal static class SharedPostgreSqlContainer
{
    private const string TemplateDatabaseName = "warp_template";

#pragma warning disable IDE0052, S4487
    private static PostgreSqlContainer? _container;
#pragma warning restore IDE0052, S4487
    private static string? _adminConnectionString;
    private static readonly SemaphoreSlim _initLock = new(1, 1);

    public static async Task<string> CreateDatabaseAsync(string databaseName, CancellationToken ct)
    {
        var adminConn = await EnsureContainerAsync(ct);

        await using var conn = new NpgsqlConnection(adminConn);
        await conn.OpenAsync(ct);

        // Identifier comes from our own fixture code, never user input. PG's CREATE DATABASE
        // can't be parameterized, so we check existence via pg_database and skip if present
        // — idempotent for repeated test-host invocations while the container persists.
        await using (var checkCmd = new NpgsqlCommand(
            "SELECT 1 FROM pg_database WHERE datname = @name",
            conn))
        {
            checkCmd.Parameters.AddWithValue("name", databaseName);
            var exists = await checkCmd.ExecuteScalarAsync(ct);
            if (exists == null)
            {
                await using var createCmd = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\" TEMPLATE \"{TemplateDatabaseName}\"", conn);
                await createCmd.ExecuteNonQueryAsync(ct);
            }
        }

        return new NpgsqlConnectionStringBuilder(adminConn) { Database = databaseName }.ConnectionString;
    }

    // Sibling to CreateDatabaseAsync — releases the per-fixture database when the class
    // fixture is disposed. DROP DATABASE WITH (FORCE) (PG 13+) terminates any sessions still
    // connected to it, which is what reclaims connections back to the testcontainer's
    // max_connections budget. Without this every fixture leaks its full MaxPoolSize=50 quota
    // server-side until the test process exits — see PostgreSqlClassFixture.DisposeAsync.
    public static async Task DropDatabaseAsync(string databaseName, CancellationToken ct)
    {
        var adminConn = await EnsureContainerAsync(ct);

        await using var conn = new NpgsqlConnection(adminConn);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)",
            conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string> EnsureContainerAsync(CancellationToken ct)
    {
        if (_adminConnectionString != null)
        {
            return _adminConnectionString;
        }

        await _initLock.WaitAsync(ct);
        try
        {
            if (_adminConnectionString == null)
            {
                var container = new PostgreSqlBuilder()
                    .WithImage("postgres:latest")
                    .WithCommand("-c", "max_connections=500")
                    .Build();
                await container.StartAsync(ct);
                _container = container;
                await BuildTemplateAsync(container.GetConnectionString(), ct);

                // Published last: a caller that sees it may clone the template straight away.
                _adminConnectionString = container.GetConnectionString();
            }

            return _adminConnectionString;
        }
        finally
        {
            _initLock.Release();
        }
    }

    // The same options PostgreSqlClassFixture.CreateContext uses, minus the interceptor, which does not
    // affect the schema.
    private static async Task BuildTemplateAsync(string adminConnectionString, CancellationToken ct)
    {
        await using (var admin = new NpgsqlConnection(adminConnectionString))
        {
            await admin.OpenAsync(ct);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{TemplateDatabaseName}\"", admin);
            await create.ExecuteNonQueryAsync(ct);
        }

        var templateConnectionString = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = TemplateDatabaseName }.ConnectionString;
        await using (var context = new TestContext(new DbContextOptionsBuilder<TestContext>()
            .UseNpgsql(templateConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options))
        {
            await context.Database.EnsureCreatedAsync(ct);
        }

        // A template cannot be cloned while anyone is connected to it: drop the pooled connections the
        // schema build left, then refuse new ones.
        NpgsqlConnection.ClearAllPools();

        await using var seal = new NpgsqlConnection(adminConnectionString);
        await seal.OpenAsync(ct);
        await using var sealCmd = new NpgsqlCommand(
            $"ALTER DATABASE \"{TemplateDatabaseName}\" WITH IS_TEMPLATE true ALLOW_CONNECTIONS false",
            seal);
        await sealCmd.ExecuteNonQueryAsync(ct);
    }
}
