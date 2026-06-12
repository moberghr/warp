using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core.Notifications;
using Warp.Provider.PostgreSql;
using Warp.Worker;

namespace Warp.Tests.Notifications;

// Pinned behavior: UseDatabasePush must be called AFTER a provider's UseX() extension,
// because it needs IWarpNotificationTransportFactory to be registered. Without the setup-
// time check, the error would surface only when DI resolution kicks the singleton factory
// at app-startup, which is much harder to diagnose.
[Trait("Category", "NoDb")]
public class UseDatabasePushValidationTests
{
    [Fact]
    public void UseDatabasePush_WithoutProvider_ThrowsAtSetupTime()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestContext>(o => o.UseNpgsql("Host=x;Database=x;Username=x;Password=x"));

        var ex = Should.Throw<InvalidOperationException>(() =>
        {
            services.AddWarpServer<TestContext>(opt =>
            {
                // Deliberately no UsePostgreSql / UseSqlServer.
                opt.UseDatabasePush();
            });
        });

        ex.Message.ShouldContain("UsePostgreSql");
    }

    [Fact]
    public void UseDatabasePush_AfterUsePostgreSql_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestContext>(o => o.UseNpgsql("Host=x;Database=x;Username=x;Password=x"));

        services.AddWarpServer<TestContext>(opt =>
        {
            opt.UsePostgreSql();
            opt.UseDatabasePush();
        });

        // Pin: both services are registered. The actual transport resolution is tested
        // end-to-end by the PostgresDatabasePushIntegrationTests — here we just confirm the
        // correct ordering completes setup without the eager-validation throw.
        services.Any(x => x.ServiceType == typeof(IWarpNotificationTransportFactory)).ShouldBeTrue();
        services.Any(x => x.ServiceType == typeof(IWarpNotificationTransport)).ShouldBeTrue();
    }

    [Fact]
    public void UseDatabasePush_BeforeUsePostgreSql_ThrowsAtSetupTime()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestContext>(o => o.UseNpgsql("Host=x;Database=x;Username=x;Password=x"));

        var ex = Should.Throw<InvalidOperationException>(() =>
        {
            services.AddWarpServer<TestContext>(opt =>
            {
                opt.UseDatabasePush();  // out of order
                opt.UsePostgreSql();
            });
        });

        ex.Message.ShouldContain("UsePostgreSql");
    }
}
