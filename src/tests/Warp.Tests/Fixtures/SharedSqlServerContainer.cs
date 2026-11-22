using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace Warp.Tests.Fixtures;

// Process-wide shared MsSqlContainer. xUnit parallelizes across collection fixtures, so
// without this each SqlServer* fixture would boot its own SQL Server (~2GB RSS each). On a
// resource-constrained CI runner that starves every test of CPU/IO and causes flaky time-
// sensitive tests. Each fixture now creates its own uniquely-named database on the single
// shared instance, so DB-level isolation is preserved.
//
// The container is not explicitly disposed — Testcontainers' Ryuk reaper cleans it up when
// the test process exits. This mirrors the original per-fixture behavior under dotnet test.
internal static class SharedSqlServerContainer
{
    // Held so the container object is kept alive for the process lifetime. Testcontainers'
    // Ryuk daemon reaps the Docker container on process exit; we only need to stop this
    // field from being eligible for GC while tests are still running.
#pragma warning disable IDE0052, S4487
    private static MsSqlContainer? _container;
#pragma warning restore IDE0052, S4487
    private static string? _masterConnectionString;
    private static readonly SemaphoreSlim _initLock = new(1, 1);

    public static async Task<string> CreateDatabaseAsync(string databaseName, bool enableServiceBroker, CancellationToken ct)
    {
        var masterConn = await EnsureContainerAsync(ct);

        await using var conn = new SqlConnection(masterConn);
        await conn.OpenAsync(ct);

        // Database names can't be parameterized in T-SQL DDL; each caller passes a literal
        // from our own fixture code, so no injection risk. Using IF DB_ID IS NULL keeps the
        // call idempotent for repeated test-host invocations while the container persists.
        var brokerStmt = enableServiceBroker
            ? $"ALTER DATABASE [{databaseName}] SET ENABLE_BROKER WITH ROLLBACK IMMEDIATE;"
            : string.Empty;

        await using var cmd = new SqlCommand(
            $@"IF DB_ID('{databaseName}') IS NULL EXEC('CREATE DATABASE [{databaseName}]');
               {brokerStmt}",
            conn);
        await cmd.ExecuteNonQueryAsync(ct);

        return new SqlConnectionStringBuilder(masterConn) { InitialCatalog = databaseName }.ConnectionString;
    }

    // Sibling to CreateDatabaseAsync — releases the per-fixture database when the class
    // fixture is disposed. SET SINGLE_USER WITH ROLLBACK IMMEDIATE kills any sessions still
    // connected to the DB (the SQL Server analogue of PG's WITH (FORCE)); the subsequent
    // DROP runs cleanly. Same hygiene as the PG container — see SqlServerClassFixture.
    //
    // The sessions are KILLed explicitly first, and that is the difference between 3 seconds and 50 ms
    // per test class. A class always leaves at least one idle pooled connection on its database
    // (ClearPool on the fixture's connection string does not close it, and cannot reach the pools
    // WarpTestServer creates under its own Application Name), and SET SINGLE_USER WITH ROLLBACK IMMEDIATE
    // takes a flat ~3,000 ms to force even one idle session off. Measured with one idle pooled
    // connection: 3,013 ms for the ALTER as it was, 12 ms after the KILL (which itself takes ~5 ms).
    // With 212 database test classes that was about ten minutes of thread time per SQL Server run.
    // SINGLE_USER stays as the backstop against a session that connects between the KILL and the DROP.
    public static async Task DropDatabaseAsync(string databaseName, CancellationToken ct)
    {
        var masterConn = await EnsureContainerAsync(ct);

        await using var conn = new SqlConnection(masterConn);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(
            $@"IF DB_ID('{databaseName}') IS NOT NULL
               BEGIN
                   -- Each KILL in its own TRY: a session can end by itself between this SELECT and the
                   -- KILL (its pool is being cleared at the same moment), and KILL on a session that is
                   -- already gone raises 'Process ID n is not an active process ID', which would abort
                   -- the batch and leave the database undropped.
                   DECLARE @kill nvarchar(max) = N'';
                   SELECT @kill += N'BEGIN TRY KILL ' + CAST(session_id AS nvarchar(10)) + N' END TRY BEGIN CATCH END CATCH;'
                   FROM sys.dm_exec_sessions
                   WHERE database_id = DB_ID('{databaseName}') AND session_id <> @@SPID;
                   EXEC(@kill);

                   ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                   DROP DATABASE [{databaseName}];
               END",
            conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string> EnsureContainerAsync(CancellationToken ct)
    {
        if (_masterConnectionString != null)
        {
            return _masterConnectionString;
        }

        await _initLock.WaitAsync(ct);
        try
        {
            if (_masterConnectionString == null)
            {
                var container = new MsSqlBuilder()
                    .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
                    .Build();
                await container.StartAsync(ct);
                _container = container;
                _masterConnectionString = container.GetConnectionString() + ";Encrypt=False;";
            }

            return _masterConnectionString;
        }
        finally
        {
            _initLock.Release();
        }
    }
}
