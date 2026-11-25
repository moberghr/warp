using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Provider.PostgreSql;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Core;

// The claim calls gen_random_uuid(), built in from PostgreSQL 13. On an older server every claim fails and no
// job runs, so a Warp server must refuse to start instead.
[Trait("Category", "NoDb")]
public class PostgresVersionCheckTests
{
    [Theory]
    [InlineData(120022)]
    [InlineData(90624)]
    public void Ensure_ServerOlderThan13_ThrowsNamingTheVersion(int versionNumber)
    {
        var exception = Should.Throw<InvalidOperationException>(() => PostgresVersionCheck.Ensure(versionNumber));

        exception.Message.ShouldContain("PostgreSQL 13");
        exception.Message.ShouldContain(versionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(130000)]
    [InlineData(180007)]
    public void Ensure_Server13OrNewer_Passes(int versionNumber)
    {
        Should.NotThrow(() => PostgresVersionCheck.Ensure(versionNumber));
    }
}

[Trait("Category", "PostgreSql")]
public class PostgresVersionCheckDatabaseTests : IClassFixture<PostgreSqlClassFixture>
{
    private readonly PostgreSqlClassFixture _fixture;

    public PostgresVersionCheckDatabaseTests(PostgreSqlClassFixture fixture) => _fixture = fixture;

    [TimedFact]
    public async Task EnsureAsync_TheTestServer_ReadsItsVersionAndPasses()
    {
        await using var context = _fixture.CreateContext();

        await PostgresVersionCheck.EnsureAsync(context, Xunit.TestContext.Current.CancellationToken);
    }
}
