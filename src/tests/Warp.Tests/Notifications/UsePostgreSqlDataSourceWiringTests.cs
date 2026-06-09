using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Warp.Core.Notifications;
using Warp.Provider.PostgreSql;
using Warp.Worker;

namespace Warp.Tests.Notifications;

// Pins the data-source resolution wiring added so callers using
// UseNpgsql(NpgsqlDataSource) — Aspire's AddAzureNpgsqlDataSource, Managed Identity,
// custom SSL/password providers — get the data source threaded through to the Warp
// transport/lock/semaphore instead of having connections opened from a raw string that
// silently drops auth and encryption settings.
[Trait("Category", "NoDb")]
public class UsePostgreSqlDataSourceWiringTests
{
    private const string DummyConnectionString = "Host=x;Database=x;Username=x;Password=x";

    [Fact]
    public void UsePostgreSql_WhenDbContextHasDataSource_FactoryUsesDataSourcePath()
    {
        var services = new ServiceCollection();
        using var dataSource = NpgsqlDataSource.Create(DummyConnectionString);
        services.AddDbContext<TestContext>(o => o.UseNpgsql(dataSource));

        services.AddWarpServer<TestContext>(opt => opt.UsePostgreSql());

        using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var factory = sp.GetRequiredService<IWarpNotificationTransportFactory>();

        var transport = factory.Create(
            "ignored — data source path should win",
            new WarpDatabasePushConfiguration { ChannelName = "x" },
            NullLoggerFactory.Instance);

        ReadDataSource(transport).ShouldBeSameAs(dataSource);
    }

    [Fact]
    public void UsePostgreSql_WhenDbContextHasOnlyConnectionString_FactoryUsesStringPath()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestContext>(o => o.UseNpgsql(DummyConnectionString));

        services.AddWarpServer<TestContext>(opt => opt.UsePostgreSql());

        using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var factory = sp.GetRequiredService<IWarpNotificationTransportFactory>();

        var transport = factory.Create(
            DummyConnectionString,
            new WarpDatabasePushConfiguration { ChannelName = "x" },
            NullLoggerFactory.Instance);

        ReadDataSource(transport).ShouldBeNull();
    }

    [Fact]
    public void UseDatabasePush_WithDataSource_ResolvedTransportUsesDataSourcePath()
    {
        // The actual consumer of IWarpNotificationTransportFactory is
        // DatabasePushServiceConfiguration's singleton lambda, which resolves a
        // connection string and calls factory.Create(connectionString, ...). This test
        // boots the full UseDatabasePush stack and resolves IWarpNotificationTransport
        // (the singleton — not the factory) to pin that the consumer path doesn't drop
        // the data source somewhere between the factory and the transport.
        var services = new ServiceCollection();
        using var dataSource = NpgsqlDataSource.Create(DummyConnectionString);
        services.AddDbContext<TestContext>(o => o.UseNpgsql(dataSource));

        services.AddWarpServer<TestContext>(opt =>
        {
            opt.UsePostgreSql();
            opt.UseDatabasePush();
        });

        using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var transport = sp.GetRequiredService<IWarpNotificationTransport>();

        ReadDataSource(transport).ShouldBeSameAs(dataSource);
    }

    private static NpgsqlDataSource? ReadDataSource(IWarpNotificationTransport transport)
    {
        var concrete = transport.ShouldBeOfType<PostgresNotificationTransport>();
        var field = typeof(PostgresNotificationTransport)
            .GetField("_dataSource", BindingFlags.Instance | BindingFlags.NonPublic);
        field.ShouldNotBeNull();

        return field.GetValue(concrete) as NpgsqlDataSource;
    }
}
