using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;
using Npgsql;
using Shouldly;
using Warp.Core;
using Warp.Core.BackgroundServices;
using Warp.Core.Concurrency;
using Warp.Core.Data.Queries;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.RateLimit;
using Warp.Core.Sagas;
using Warp.Core.Services;
using Warp.Core.Testing;
using Warp.Core.Webhooks;
using Warp.Provider.PostgreSql;
using Warp.Provider.SqlServer;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Core;

/// <summary>
/// #344, rule 2.14: <c>AddWarp</c> registers Warp's own context for the dashboard query, command and admin
/// services, and falls back to the application's context when no provider supports it (an application's tests on
/// EF in-memory, #359). Nothing here opens a connection.
/// </summary>
[Trait("Category", "NoDb")]
public sealed class WarpContextSelectionTests
{
    private const string Unreachable = "Host=unreachable.invalid;Database=app;Timeout=1";

    // EF caches a context type's model once per process (per provider): every Npgsql TestContext here uses the
    // PostgreSQL fixture's snake_case naming, so a model built here is the one the PostgreSQL tests expect.

    // Every service #344 moved, resolved from an AddWarp-only process, holds the Warp context.
    private static readonly Type[] MovedServices =
    [
        typeof(IJobQueryService),
        typeof(IJobLogQueryService),
        typeof(IJobGroupQueryService),
        typeof(IDashboardStatsService),
        typeof(IBackgroundServiceQueryService),
        typeof(IAdapterQueryService),
        typeof(IApplicationQueryService),
        typeof(IApplicationDeployQueryService),
        typeof(IEndpointQueryService),
        typeof(IClientEventQueryService),
        typeof(ITraceQueryService),
        typeof(IErrorGroupQueryService),
        typeof(ISloQueryService),
        typeof(IMetricQueryService),
        typeof(IMetricBoardQueryService),
        typeof(IMetricAnnotationQueryService),
        typeof(IWebhookQueryService),
        typeof(ISagaQueryService),
        typeof(IJobCommandService),
        typeof(IRecurringJobService),
        typeof(IServerCommandService),
        typeof(IErrorGroupCommandService),
        typeof(ISloCommandService),
        typeof(IMetricBoardCommandService),
        typeof(IMetricAnnotationCommandService),
        typeof(IWebhookCommandService),
        typeof(ISagaCommandService),
        typeof(IConcurrencyLimitManager),
        typeof(IRateLimitManager),
    ];

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public void MovedServices_AddWarpOnlyOnPostgreSql_HoldTheWarpContext()
    {
        using var sp = AddWarpOnly(x => x.UseNpgsql(Unreachable).UseSnakeCaseNamingConvention(), x => x.UsePostgreSql()).BuildServiceProvider();
        using var scope = sp.CreateScope();

        var warpContext = scope.ServiceProvider.GetRequiredService<WarpContext<TestContext>>();
        foreach (var serviceType in MovedServices)
        {
            ContextOf(scope.ServiceProvider.GetRequiredService(serviceType)).ShouldBeSameAs(warpContext, serviceType.Name);
        }
    }

    [TimedFact]
    public void MovedServices_AddWarpOnlyOnSqlServer_HoldTheWarpContext()
    {
        using var sp = AddWarpOnly(x => x.UseSqlServer("Server=unreachable.invalid;Database=app"), x => x.UseSqlServer()).BuildServiceProvider();
        using var scope = sp.CreateScope();

        var warpContext = scope.ServiceProvider.GetRequiredService<WarpContext<TestContext>>();
        foreach (var serviceType in MovedServices)
        {
            ContextOf(scope.ServiceProvider.GetRequiredService(serviceType)).ShouldBeSameAs(warpContext, serviceType.Name);
        }
    }

    // The application's tests keep UsePostgreSql from Program.cs and swap the context to EF in-memory (#359): the
    // provider does not support it, so the services stay on the application's context.
    [TimedFact]
    public void MovedServices_ProviderDoesNotSupportTheContext_HoldTheApplicationContext()
    {
        var services = AddWarpOnly(x => x.UseInMemoryDatabase($"selection-{Guid.NewGuid():N}"), x => x.UsePostgreSql());

        // The provider's IWarpSqlQueries and lock provider read relational names and the connection (before #344 too).
        services.Replace(ServiceDescriptor.Singleton(Mock.Of<IWarpSqlQueries<TestContext>>()));
        services.Replace(ServiceDescriptor.Singleton(Mock.Of<IWarpLockProvider>()));
        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();

        var applicationContext = scope.ServiceProvider.GetRequiredService<TestContext>();
        foreach (var serviceType in MovedServices)
        {
            ContextOf(scope.ServiceProvider.GetRequiredService(serviceType)).ShouldBeSameAs(applicationContext, serviceType.Name);
        }
    }

    [TimedFact]
    public void WarpContext_NoProviderRegistered_IsTheApplicationContext()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<IWarpLockProvider>());
        services.AddLogging();
        services.AddDbContext<TestContext>(x => x.UseInMemoryDatabase($"selection-{Guid.NewGuid():N}"));
        services.AddWarp<TestContext>();
        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();

        scope.ServiceProvider.GetRequiredService<IWarpContext>().Context
            .ShouldBeSameAs(scope.ServiceProvider.GetRequiredService<TestContext>());
    }

    // Resolving a moved service never builds the application's context: only the Warp context's model reads its
    // names from it, once, on first use.
    [TimedFact]
    public void MovedServices_AddWarpOnly_DoNotBuildTheApplicationContext()
    {
        var built = 0;
        var services = AddWarpOnly(x => x.UseNpgsql(Unreachable).UseSnakeCaseNamingConvention(), x => x.UsePostgreSql());
        services.Replace(ServiceDescriptor.Scoped(sp =>
        {
            Interlocked.Increment(ref built);

            return new TestContext(sp.GetRequiredService<DbContextOptions<TestContext>>());
        }));

        // The provider's IWarpSqlQueries and lock provider read TContext's model and connection once (before #344 too).
        services.Replace(ServiceDescriptor.Singleton(Mock.Of<IWarpSqlQueries<TestContext>>()));
        services.Replace(ServiceDescriptor.Singleton(Mock.Of<IWarpLockProvider>()));
        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();

        foreach (var serviceType in MovedServices)
        {
            var before = built;
            scope.ServiceProvider.GetRequiredService(serviceType);
            built.ShouldBe(before, serviceType.Name);
        }
    }

    // The documented #359 setup end to end: Program.cs on PostgreSQL, the test swaps the context to EF in-memory and
    // removes Warp's hosted services. A published job reads back through the dashboard query service.
    [TimedFact]
    public async Task GetJobsList_AppTestOnAnInMemoryContext_ReadsWhatThePublisherStaged()
    {
        var database = $"app-tests-{Guid.NewGuid():N}";
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddLogging();
        builder.Services.AddDbContext<TestContext>(x => x.UseNpgsql(Unreachable).UseSnakeCaseNamingConvention());
        builder.Services.AddWarpServer<TestContext>(x => x.UsePostgreSql());
        builder.Services.RemoveAll<DbContextOptions<TestContext>>();
        builder.Services.RemoveAll<IDbContextOptionsConfiguration<TestContext>>();
        builder.Services.AddDbContext<TestContext>(x => x.UseInMemoryDatabase(database));
        builder.Services.RemoveWarpHostedServices();
        using var host = builder.Build();
        await host.StartAsync(Ct);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPublisher>().Enqueue(new UnitRequest());
            await scope.ServiceProvider.GetRequiredService<TestContext>().SaveChangesAsync(Ct);
        }

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var jobs = await scope.ServiceProvider.GetRequiredService<IJobQueryService>()
                .GetJobsList(new BaseListRequest { PageSize = 10 }, State.Enqueued);
            jobs.Items.ShouldHaveSingleItem();
        }

        await host.StopAsync(CancellationToken.None);
    }

    // dotnet ef discovers contexts through the non-generic DbContextOptions forwarders: the Warp context's is removed.
    [TimedFact]
    public void DbContextOptionsForwarders_AddWarpOnly_NameOnlyTheApplicationContext()
    {
        using var sp = AddWarpOnly(x => x.UseNpgsql(Unreachable).UseSnakeCaseNamingConvention(), x => x.UsePostgreSql()).BuildServiceProvider();

        sp.GetServices<DbContextOptions>().Select(x => x.ContextType).ShouldBe([typeof(TestContext)]);
    }

    [TimedFact]
    public void Supports_PostgreSqlConfigurator_OnlyANpgsqlContext()
    {
        var configurator = new PostgresServerContextConfigurator<TestContext>();

        configurator.Supports(Options(x => x.UseNpgsql(Unreachable).UseSnakeCaseNamingConvention())).ShouldBeTrue();
        configurator.Supports(Options(x => x.UseSqlServer("Server=x;Database=app"))).ShouldBeFalse();
        configurator.Supports(Options(x => x.UseInMemoryDatabase("x"))).ShouldBeFalse();
    }

    [TimedFact]
    public void Supports_SqlServerConfigurator_OnlyASqlServerContext()
    {
        var configurator = new SqlServerServerContextConfigurator<TestContext>();

        configurator.Supports(Options(x => x.UseSqlServer("Server=x;Database=app"))).ShouldBeTrue();
        configurator.Supports(Options(x => x.UseNpgsql(Unreachable).UseSnakeCaseNamingConvention())).ShouldBeFalse();
        configurator.Supports(Options(x => x.UseInMemoryDatabase("x"))).ShouldBeFalse();
    }

    // The Warp context opens on Warp's own PostgreSQL pool (rule 6.22) and keeps the application's command timeout.
    [TimedFact]
    public void WarpContext_PostgreSqlConnectionString_UsesTheWarpPoolAndTheApplicationCommandTimeout()
    {
        using var sp = AddWarpOnly(x => x.UseNpgsql(Unreachable, y => y.CommandTimeout(123)).UseSnakeCaseNamingConvention(), x => x.UsePostgreSql()).BuildServiceProvider();
        using var scope = sp.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<WarpContext<TestContext>>();

        var connection = new NpgsqlConnectionStringBuilder(context.Database.GetConnectionString());
        connection.ApplicationName.ShouldNotBeNull().ShouldEndWith("warp-server");
        connection.NoResetOnClose.ShouldBeTrue();
        context.Database.GetCommandTimeout().ShouldBe(123);
    }

    [TimedFact]
    public void WarpContext_SqlServer_KeepsTheApplicationCommandTimeout()
    {
        using var sp = AddWarpOnly(x => x.UseSqlServer("Server=unreachable.invalid;Database=app", y => y.CommandTimeout(77)), x => x.UseSqlServer()).BuildServiceProvider();
        using var scope = sp.CreateScope();

        scope.ServiceProvider.GetRequiredService<WarpContext<TestContext>>().Database.GetCommandTimeout().ShouldBe(77);
    }

    [TimedFact]
    public void WarpContext_ApplicationSetsNoCommandTimeout_KeepsTheProviderDefault()
    {
        using var sp = AddWarpOnly(x => x.UseNpgsql(Unreachable).UseSnakeCaseNamingConvention(), x => x.UsePostgreSql()).BuildServiceProvider();
        using var scope = sp.CreateScope();

        scope.ServiceProvider.GetRequiredService<WarpContext<TestContext>>().Database.GetCommandTimeout().ShouldBeNull();
    }

    // #347: the run id is a concurrency token for single-worker finalization on the server context only. On the
    // context the dashboard saves jobs through it would bring back the bulk-requeue deadlock on SQL Server.
    [TimedFact]
    public void RunIdConcurrencyToken_OnlyOnTheServerContext()
    {
        var services = AddWarpOnly(x => x.UseNpgsql(Unreachable).UseSnakeCaseNamingConvention(), x => x.UsePostgreSql());
        services.AddWarpServer<TestContext>();
        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();

        RunId(scope.ServiceProvider.GetRequiredService<WarpContext<TestContext>>()).IsConcurrencyToken.ShouldBeFalse();
        RunId(scope.ServiceProvider.GetRequiredService<IWarpServerContext>().Context).IsConcurrencyToken.ShouldBeTrue();
    }

    private static ServiceCollection AddWarpOnly(Action<DbContextOptionsBuilder> configureContext, Action<WarpBuilder<TestContext>> configureWarp)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TestContext>(configureContext);
        services.AddWarp<TestContext>(x =>
        {
            configureWarp(x);
            x.AddConcurrency();
            x.AddRateLimit();
            x.AddSagas();
        });

        return services;
    }

    private static DbContextOptions<TestContext> Options(Action<DbContextOptionsBuilder> configure)
    {
        var builder = new DbContextOptionsBuilder<TestContext>();
        configure(builder);

        return builder.Options;
    }

    private static Microsoft.EntityFrameworkCore.Metadata.IProperty RunId(DbContext context)
        => context.Model.FindEntityType(typeof(Job))!.FindProperty(nameof(Job.RunId))!;

    // The context a service holds, unwrapping Warp's decorators (the rate-limit manager's gate).
    private static object? ContextOf(object service)
    {
        var fields = service.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
        var context = fields.FirstOrDefault(x => typeof(DbContext).IsAssignableFrom(x.FieldType));
        if (context is not null)
        {
            return context.GetValue(service);
        }

        var inner = fields.FirstOrDefault(x => string.Equals(x.Name, "_inner", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"{service.GetType().Name} holds no context.");

        return ContextOf(inner.GetValue(service)!);
    }
}
