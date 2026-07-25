using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Warp.Core;
using Warp.Core.Data;
using Warp.Core.Data.Queries;
using Warp.Core.Notifications;
using Warp.Worker;

namespace Warp.Provider.PostgreSql;

/// <summary>
/// Registers the PostgreSQL-specific provider services (row-lock SQL, exception classifier)
/// for Warp. Call <c>opt.UsePostgreSql()</c> inside the <c>AddWarp</c> or
/// <c>AddWarpServer</c> lambda to opt in.
/// </summary>
public static class PostgreSqlServiceConfiguration
{
    public static IWarpBuilder<TContext> UsePostgreSql<TContext>(this IWarpBuilder<TContext> builder)
        where TContext : DbContext
    {
        builder.Services.TryAddSingleton<IWarpSqlQueries<TContext>>(sp =>
        {
            using var scope = sp.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<TContext>();

            // Propagate the configured lease TTL so the heartbeat SQL renewal window matches
            // what SingletonServiceStrategy uses when it first acquires the lease.
            var workerConfig = sp.GetService<IOptions<WarpServerConfiguration>>();
            var leaseTtl = workerConfig?.Value.BackgroundServiceLeaseTtl ?? TimeSpan.FromSeconds(30);
            var names = WarpJobTableNames.FromModel(context.Model, (int)leaseTtl.TotalSeconds);

            return new PostgresWarpSqlQueries<TContext>(names);
        });

        builder.Services.TryAddSingleton<IDatabaseExceptionClassifier, PostgresExceptionClassifier>();

        builder.Services.TryAddSingleton<IWarpNotificationTransportFactory>(sp =>
            new PostgresNotificationTransportFactory(ResolveDataSource<TContext>(sp)));

        builder.Services.TryAddSingleton<IWarpLockProvider>(sp =>
            ResolveDataSource<TContext>(sp) is { } dataSource
                ? new PostgresLockProvider(dataSource)
                : new PostgresLockProvider(ResolveConnectionString<TContext>(sp)));

        builder.Services.TryAddSingleton<IWarpSemaphoreProvider>(sp =>
            ResolveDataSource<TContext>(sp) is { } dataSource
                ? new PostgresSemaphoreProvider(dataSource)
                : new PostgresSemaphoreProvider(ResolveConnectionString<TContext>(sp)));

        // Points the Warp server context at the same database as TContext (data source if the user
        // registered one, else the connection string), inheriting auth/SSL settings.
        builder.Services.TryAddSingleton<IWarpServerContextConfigurator>(new PostgresServerContextConfigurator<TContext>());

        return builder;
    }

    internal static string ResolveConnectionString<TContext>(IServiceProvider sp)
        where TContext : DbContext
    {
        using var scope = sp.CreateScope();
        var dbOptions = scope.ServiceProvider.GetRequiredService<DbContextOptions<TContext>>();
        var relationalExtension = dbOptions.Extensions.OfType<RelationalOptionsExtension>().FirstOrDefault();
        var connectionString = relationalExtension?.ConnectionString;

        if (connectionString is null)
        {
            var context = scope.ServiceProvider.GetRequiredService<TContext>();
            connectionString = context.Database.GetConnectionString()
                ?? throw new InvalidOperationException("Cannot resolve connection string for Warp PostgreSQL provider.");
        }

        return connectionString;
    }

    // When the user registered the DbContext with UseNpgsql(NpgsqlDataSource) — e.g. via
    // Aspire's AddAzureNpgsqlDataSource, or any setup that needs Managed Identity tokens
    // or SSL settings attached to the data source — surface that data source so our lock,
    // semaphore, and notification connections inherit the same auth/encryption configuration
    // instead of being opened from a raw connection string that may be missing them.
    internal static NpgsqlDataSource? ResolveDataSource<TContext>(IServiceProvider sp)
        where TContext : DbContext
    {
        // AddDbContext registers DbContextOptions<TContext> as Scoped (only AddDbContextPool
        // makes it Singleton), so we have to resolve it through a scope — otherwise providers
        // built with ValidateScopes=true reject the resolution from the root provider.
        using var scope = sp.CreateScope();
        var dbOptions = scope.ServiceProvider.GetRequiredService<DbContextOptions<TContext>>();

        // EF1001: NpgsqlOptionsExtension is in an Infrastructure.Internal namespace, but it's the
        // documented extension point exposing the DataSource bound to a DbContext — there is no
        // public alternative. Same pattern Aspire's Npgsql component uses to read this back.
#pragma warning disable EF1001
        var optionsDataSource = dbOptions.Extensions
            .OfType<NpgsqlOptionsExtension>()
            .FirstOrDefault()?.DataSource as NpgsqlDataSource;
#pragma warning restore EF1001

        // Prefer the data source EF actually uses for TContext (set only when the DbContext was configured
        // with UseNpgsql(dataSource)). When it's null — the DbContext was configured with a bare connection
        // string, or UseNpgsql() with the data source resolved from DI — fall back to a DI-registered
        // NpgsqlDataSource (Aspire's AddNpgsqlDataSource / AddAzureNpgsqlDataSource, or a manual
        // AddNpgsqlDataSource). This lets Warp's lock / semaphore / notification / server-context connections
        // inherit that data source's auth + SSL (RDS IAM tokens, Cloud SQL, client certs) instead of opening
        // from a raw connection string that lacks them. Without it, a DI-only data source was silently ignored.
        return optionsDataSource ?? sp.GetService<NpgsqlDataSource>();
    }
}

internal sealed class PostgresServerContextConfigurator<TContext> : IWarpServerContextConfigurator
    where TContext : DbContext
{
    public void Configure(DbContextOptionsBuilder optionsBuilder, IServiceProvider applicationServices)
    {
        var dataSource = PostgreSqlServiceConfiguration.ResolveDataSource<TContext>(applicationServices);
        if (dataSource is not null)
        {
            optionsBuilder.UseNpgsql(dataSource);

            return;
        }

        optionsBuilder.UseNpgsql(PostgreSqlServiceConfiguration.ResolveConnectionString<TContext>(applicationServices));
    }
}
