using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Warp.Core;
using Warp.Provider.PostgreSql;
using Warp.Worker;

namespace Warp.Tests.Features.Concurrency;

/// <summary>
/// Rot guard for Warp's own server-context pool (rule 6.19). On the shared pool every checkout of a
/// server-context connection cost a <c>DISCARD ALL</c>, about a third of an idle server's statements;
/// nothing in behaviour shows whether the pool is split, so only these tests catch a refactor that hands
/// the server context the DbContext's connection string again.
/// </summary>
[Trait("Category", "NoDb")]
public class ServerConnectionPoolTests
{
    private const string BaseConnectionString = "Host=localhost;Port=5432;Database=warp_guard;Username=warp";

    [TimedFact]
    public void BuildServerConnectionString_SamePoolTarget_OwnPoolWithoutReset()
    {
        var context = new NpgsqlConnectionStringBuilder(BaseConnectionString);

        // Act
        var server = new NpgsqlConnectionStringBuilder(PostgreSqlServiceConfiguration.BuildServerConnectionString(BaseConnectionString));

        // Assert
        server.ApplicationName.ShouldBe("warp-server");
        server.NoResetOnClose.ShouldBeTrue();
        server.MinPoolSize.ShouldBe(0);
        server.Host.ShouldBe(context.Host);
        server.Port.ShouldBe(context.Port);
        server.Database.ShouldBe(context.Database);
        server.Username.ShouldBe(context.Username);
    }

    [TimedFact]
    public void BuildServerConnectionString_HostApplicationName_IsSuffixedNotReplaced()
    {
        // Act
        var server = new NpgsqlConnectionStringBuilder(
            PostgreSqlServiceConfiguration.BuildServerConnectionString($"{BaseConnectionString};Application Name=checkout-api"));

        // Assert
        server.ApplicationName.ShouldBe("checkout-api:warp-server");
    }

    [TimedFact]
    public void ServerContext_UsePostgreSqlWithAConnectionString_OpensOnWarpsOwnPool()
    {
        // The wiring half: the provider's configurator must hand the server context the derived string,
        // not the DbContext's own.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TestContext>(x => x.UseNpgsql(BaseConnectionString).UseSnakeCaseNamingConvention());
        services.AddWarpServer<TestContext>(x => x.UsePostgreSql());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        // Act
        var connection = scope.ServiceProvider.GetRequiredService<IWarpServerContext>().Context.Database.GetConnectionString();

        // Assert
        var server = new NpgsqlConnectionStringBuilder(connection);
        server.ApplicationName.ShouldBe("warp-server");
        server.NoResetOnClose.ShouldBeTrue();
    }
}
