using Microsoft.Data.SqlClient;
using Shouldly;
using Warp.Provider.SqlServer;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Core;

// The batched completion and keep-alive read JSON with OPENJSON, which needs database compatibility level
// 130. On a database left lower after a server upgrade the claim works and the completion fails after the
// handler ran, so jobs run and are never finalized: a server must refuse to start instead.
[Trait("Category", "SqlServer")]
public class SqlServerCompatibilityLevelTests : IClassFixture<SqlServerPushClassFixture>
{
    private readonly SqlServerPushClassFixture _fixture;

    public SqlServerCompatibilityLevelTests(SqlServerPushClassFixture fixture) => _fixture = fixture;

    [TimedFact]
    public async Task EnsureAsync_DatabaseBelowLevel130_ThrowsNamingTheFix()
    {
        // Arrange: a database of its own, so the shared test database keeps its level.
        var ct = Xunit.TestContext.Current.CancellationToken;
        var database = $"warp_compat_{Guid.NewGuid():N}";
        await ExecuteAsync(_fixture.ConnectionString, $"CREATE DATABASE [{database}]; ALTER DATABASE [{database}] SET COMPATIBILITY_LEVEL = 120;");
        var connectionString = new SqlConnectionStringBuilder(_fixture.ConnectionString) { InitialCatalog = database }.ConnectionString;
        try
        {
            // Act
            var exception = await Should.ThrowAsync<InvalidOperationException>(() => SqlServerCompatibilityCheck.EnsureAsync(connectionString, ct));

            // Assert
            exception.Message.ShouldContain("130");
            exception.Message.ShouldContain("ALTER DATABASE");
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await ExecuteAsync(_fixture.ConnectionString, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
        }
    }

    [TimedFact]
    public async Task EnsureAsync_DatabaseAtTheDefaultLevel_Passes()
    {
        await SqlServerCompatibilityCheck.EnsureAsync(_fixture.ConnectionString, Xunit.TestContext.Current.CancellationToken);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(Xunit.TestContext.Current.CancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Xunit.TestContext.Current.CancellationToken);
    }
}
