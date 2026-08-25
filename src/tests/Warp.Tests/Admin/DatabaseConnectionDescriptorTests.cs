using Shouldly;
using Warp.Core.Services;

namespace Warp.Tests.Admin;

/// <summary>
/// The dashboard footer's connection line. This had no coverage at all, which is how it shipped labelling
/// Postgres connection strings "SQL Server": the provider was inferred from which keys the connection
/// string used, and Npgsql accepts <c>Server=</c> as an alias for <c>Host=</c>.
/// </summary>
[Trait("Category", "NoDb")]
public class DatabaseConnectionDescriptorTests
{
    private const string Npgsql = "Npgsql.EntityFrameworkCore.PostgreSQL";
    private const string SqlServer = "Microsoft.EntityFrameworkCore.SqlServer";

    [TimedFact]
    public void Describe_PostgresWithHostKey_NamesPostgres()
    {
        DatabaseConnectionDescriptor.Describe(Npgsql, "Host=db-1.internal;Database=orders;Username=warp;Password=hunter2")
            .ShouldBe("PostgreSQL Server: Host: db-1.internal, DB: orders");
    }

    [TimedFact]
    public void Describe_PostgresWithServerAlias_StillNamesPostgres()
    {
        // The regression: Npgsql treats Server= as Host=, so this is an ordinary Postgres connection string.
        DatabaseConnectionDescriptor.Describe(Npgsql, "Server=db-1.internal;Database=orders")
            .ShouldBe("PostgreSQL Server: Host: db-1.internal, DB: orders");
    }

    [TimedFact]
    public void Describe_SqlServerWithServerKey_NamesSqlServer()
    {
        DatabaseConnectionDescriptor.Describe(SqlServer, "Server=sql-1;Database=orders;User Id=warp;Password=hunter2")
            .ShouldBe("SQL Server: Host: sql-1, DB: orders");
    }

    [TimedFact]
    public void Describe_SqlServerWithDataSourceAndInitialCatalog_ReadsBothAliases()
    {
        DatabaseConnectionDescriptor.Describe(SqlServer, "Data Source=sql-1;Initial Catalog=orders;Integrated Security=true")
            .ShouldBe("SQL Server: Host: sql-1, DB: orders");
    }

    [TimedFact]
    public void Describe_NeverEchoesCredentials()
    {
        var description = DatabaseConnectionDescriptor.Describe(
            Npgsql,
            "Host=db-1;Database=orders;Username=warp;Password=hunter2;Include Error Detail=true");

        description.ShouldNotBeNull();
        description.ShouldNotContain("hunter2", Case.Insensitive);
        description.ShouldNotContain("Password", Case.Insensitive);
        description.ShouldNotContain("Username", Case.Insensitive);
    }

    [TimedFact]
    public void Describe_DuplicateKeys_TakesTheLastValueLikeAdoNet()
    {
        // Per-server connection pools are scoped in tests by appending to an already-configured string,
        // which produces exactly this shape. A naive ToDictionary throws on the duplicate.
        DatabaseConnectionDescriptor.Describe(Npgsql, "Host=db-1;Database=orders;Host=db-2")
            .ShouldBe("PostgreSQL Server: Host: db-2, DB: orders");
    }

    [TimedFact]
    public void Describe_UnknownProvider_DoesNotGuessAProviderName()
    {
        DatabaseConnectionDescriptor.Describe("Some.Other.Provider", "Host=db-1;Database=orders")
            .ShouldBe("Database: Host: db-1, DB: orders");
    }

    [TimedFact]
    public void Describe_NullProvider_DoesNotGuessAProviderName()
    {
        DatabaseConnectionDescriptor.Describe(null, "Host=db-1;Database=orders")
            .ShouldBe("Database: Host: db-1, DB: orders");
    }

    [TimedFact]
    public void Describe_MissingHost_SaysUnknownRatherThanRenderingEmpty()
    {
        DatabaseConnectionDescriptor.Describe(Npgsql, "Database=orders")
            .ShouldBe("PostgreSQL Server: Host: unknown, DB: orders");
    }

    [TimedFact]
    public void Describe_MalformedSegments_AreSkippedRatherThanThrowing()
    {
        DatabaseConnectionDescriptor.Describe(Npgsql, ";;=novalue;Host=db-1;stray;Database=orders")
            .ShouldBe("PostgreSQL Server: Host: db-1, DB: orders");
    }

    [TimedTheory]
    [InlineData(null)]
    [InlineData("")]
    public void Describe_WithoutAConnectionString_ReturnsNull(string? connectionString)
    {
        DatabaseConnectionDescriptor.Describe(Npgsql, connectionString).ShouldBeNull();
    }
}
