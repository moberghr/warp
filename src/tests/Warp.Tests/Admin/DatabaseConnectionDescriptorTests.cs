using Shouldly;
using Warp.Core.Services;

namespace Warp.Tests.Admin;

/// <summary>
/// The dashboard footer's connection line. This had no coverage at all, which is how it shipped labelling
/// Postgres connection strings "SQL Server": the provider was inferred from which keys the connection
/// string used, and Npgsql accepts <c>Server=</c> as an alias for <c>Host=</c>. Parsing now goes through
/// <c>DbConnectionStringBuilder</c>, so quoting is honoured — a password can no longer smuggle a fake host.
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

    [TimedTheory]
    [InlineData("Address")]
    [InlineData("Addr")]
    [InlineData("Network Address")]
    public void Describe_SqlServerHostAliases_ResolveTheHost(string key)
    {
        DatabaseConnectionDescriptor.Describe(SqlServer, $"{key}=sql-1;Database=orders")
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
    public void Describe_QuotedPasswordContainingAHostKey_DoesNotLeakIntoTheHost()
    {
        // ADO.NET quoting: the whole quoted value is the password, ';Host=evil;' included. A split on
        // ';' read it as three segments and folded the host to "evil" — the footer showed a host
        // that appeared nowhere in the real connection.
        var description = DatabaseConnectionDescriptor.Describe(Npgsql, "Host=db-1;Database=orders;Password='x;Host=evil;y'");

        description.ShouldNotBeNull();
        description.ShouldNotContain("evil");
        description.ShouldBe("PostgreSQL Server: Host: db-1, DB: orders");
    }

    [TimedFact]
    public void Describe_DuplicateKeys_TakesTheLastValueLikeAdoNet()
    {
        // Per-server connection pools are scoped in tests by appending to an already-configured string,
        // which produces exactly this shape.
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
    public void Describe_MalformedConnectionString_NamesOnlyTheProvider()
    {
        // Not parseable as a connection string at all. The provider would have rejected it too, so
        // rather than guess at fragments the footer says the one thing it knows.
        DatabaseConnectionDescriptor.Describe(Npgsql, "this is not a connection string")
            .ShouldBe("PostgreSQL Server");
    }

    [TimedTheory]
    [InlineData(null)]
    [InlineData("")]
    public void Describe_WithoutAConnectionString_ReturnsNull(string? connectionString)
    {
        DatabaseConnectionDescriptor.Describe(Npgsql, connectionString).ShouldBeNull();
    }
}
