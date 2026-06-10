using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Provider.PostgreSql;
using Warp.Provider.SqlServer;
using Warp.Worker;

namespace Warp.Tests.Reliability;

// Medallion (IDistributedLockProvider) is now an implementation detail of each provider
// package — only IWarpLockProvider crosses the package boundary. These tests assert the
// contract: after UsePostgreSql() / UseSqlServer(), an IWarpLockProvider resolves and is
// of the provider-specific adapter type. No Medallion types appear in test code.
[Trait("Category", "NoDb")]
public class DistributedLockRegistrationTests
{
    [TimedFact]
    public void AddWarpServer_PostgreSql_RegistersPostgresLockProvider()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestContext>(o => o.UseNpgsql("Host=localhost;Database=test;Username=user;Password=secret"));
        services.AddWarpServer<TestContext>(opt => opt.UsePostgreSql());

        using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var provider = sp.GetRequiredService<IWarpLockProvider>();

        provider.GetType().Name.ShouldBe("PostgresLockProvider");
    }

    [TimedFact]
    public void AddWarpServer_SqlServer_RegistersSqlServerLockProvider()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestContext>(o => o.UseSqlServer("Server=localhost;Database=test;User Id=sa;Password=secret;TrustServerCertificate=True"));
        services.AddWarpServer<TestContext>(opt => opt.UseSqlServer());

        using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var provider = sp.GetRequiredService<IWarpLockProvider>();

        provider.GetType().Name.ShouldBe("SqlServerLockProvider");
    }

    /// <summary>
    /// Verifies the provider reads the connection string from DbContextOptions extensions
    /// instead of resolving a DbContext. The old code created a scope and resolved the
    /// context — which, after Npgsql opens a pooled connection, can strip the password
    /// via PersistSecurityInfo=false.
    ///
    /// Poison the TestContext registration so any attempt to resolve it throws.
    /// The provider factory must succeed without ever resolving a DbContext.
    /// </summary>
    [TimedFact]
    public void AddWarpServer_PostgreSql_ResolvesFromOptionsExtension_NotFromContext()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestContext>(o => o.UseNpgsql("Host=localhost;Database=test;Username=user;Password=secret"));
        services.AddWarpServer<TestContext>(opt => opt.UsePostgreSql());

        // Poison: any attempt to resolve TestContext will throw
        services.AddScoped<TestContext>(_ => throw new InvalidOperationException("DbContext should not be resolved for connection string"));

        using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var provider = sp.GetRequiredService<IWarpLockProvider>();

        provider.GetType().Name.ShouldBe("PostgresLockProvider");
    }

    /// <summary>
    /// Same as above but for SQL Server.
    /// </summary>
    [TimedFact]
    public void AddWarpServer_SqlServer_ResolvesFromOptionsExtension_NotFromContext()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestContext>(o => o.UseSqlServer("Server=localhost;Database=test;User Id=sa;Password=secret;TrustServerCertificate=True"));
        services.AddWarpServer<TestContext>(opt => opt.UseSqlServer());

        // Poison: any attempt to resolve TestContext will throw
        services.AddScoped<TestContext>(_ => throw new InvalidOperationException("DbContext should not be resolved for connection string"));

        using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var provider = sp.GetRequiredService<IWarpLockProvider>();

        provider.GetType().Name.ShouldBe("SqlServerLockProvider");
    }

    [TimedFact]
    public void AddWarpServer_OptionsExtensionPreservesPassword_AfterWarpWrapsOptions()
    {
        const string connectionString = "Host=localhost;Database=test;Username=user;Password=secret123";
        var services = new ServiceCollection();
        services.AddDbContext<TestContext>(o => o.UseNpgsql(connectionString));
        services.AddWarpServer<TestContext>(opt => opt.UsePostgreSql());

        using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = sp.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<TestContext>>();
        var extension = options.Extensions.OfType<RelationalOptionsExtension>().FirstOrDefault();

        extension.ShouldNotBeNull();
        extension.ConnectionString.ShouldBe(connectionString);
    }
}
