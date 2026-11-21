using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Core.Notifications;

namespace Warp.Tests.Core;

// WarpDatabaseConnection is where every Warp component reads TContext's connection from. Its options
// must be the application's own, without Warp's interceptors: building those from a singleton factory
// is what made the push transport and the enqueue interceptor deadlock at startup (#322).
[Trait("Category", "NoDb")]
public class WarpDatabaseConnectionTests
{
    private const string ConnectionString = "Host=warp-connection-test;Database=warp;Username=u;Password=p";

    [TimedFact]
    public void ConnectionString_ContextConfiguredWithString_ReturnsIt()
    {
        using var provider = BuildProvider(services => services.AddWarp<TestContext>());

        provider.GetRequiredService<WarpDatabaseConnection<TestContext>>().ConnectionString.ShouldBe(ConnectionString);
    }

    [TimedFact]
    public void Options_AreTheApplicationsOwn_WithoutWarpInterceptors()
    {
        using var provider = BuildProvider(services => services.AddWarp<TestContext>());
        using var scope = provider.CreateScope();

        var stored = provider.GetRequiredService<WarpDatabaseConnection<TestContext>>().Options;
        var decorated = scope.ServiceProvider.GetRequiredService<DbContextOptions<TestContext>>();

        Interceptors(decorated).ShouldContain(x => x is EnqueueNotificationInterceptor<TestContext>);
        Interceptors(stored).ShouldNotContain(x => x is EnqueueNotificationInterceptor<TestContext>);
    }

    [TimedFact]
    public void Options_AddWarpCalledTwice_StillTheApplicationsOwn()
    {
        // AddWarpServer calls AddWarp again, which wraps the already-wrapped options factory.
        using var provider = BuildProvider(services =>
        {
            services.AddWarp<TestContext>();
            services.AddWarp<TestContext>();
        });

        var stored = provider.GetRequiredService<WarpDatabaseConnection<TestContext>>().Options;

        Interceptors(stored).ShouldNotContain(x => x is EnqueueNotificationInterceptor<TestContext>);
    }

    [TimedFact]
    public void ConnectionString_ReadTwice_IsTheSameInstance()
    {
        using var provider = BuildProvider(services => services.AddWarp<TestContext>());
        var connection = provider.GetRequiredService<WarpDatabaseConnection<TestContext>>();

        connection.ConnectionString.ShouldBeSameAs(connection.ConnectionString);
    }

    private static List<Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor> Interceptors(DbContextOptions options) =>
        options.FindExtension<CoreOptionsExtension>()?.Interceptors?.ToList() ?? [];

    private static ServiceProvider BuildProvider(Action<IServiceCollection> addWarp)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TestContext>(x => x.UseNpgsql(ConnectionString).UseSnakeCaseNamingConvention());
        addWarp(services);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
