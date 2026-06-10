using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Data;
using Warp.Core.Data.Queries;
using Warp.Core.Notifications;
using Warp.Worker;

namespace Warp.Provider.SqlServer;

/// <summary>
/// Registers the SQL Server-specific provider services (row-lock SQL, exception classifier)
/// for Warp. Call <c>opt.UseSqlServer()</c> inside the <c>AddWarp</c> or
/// <c>AddWarpServer</c> lambda to opt in.
/// </summary>
public static class SqlServerServiceConfiguration
{
    public static IWarpBuilder<TContext> UseSqlServer<TContext>(this IWarpBuilder<TContext> builder)
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

            return new SqlServerWarpSqlQueries<TContext>(names);
        });

        builder.Services.TryAddSingleton<IDatabaseExceptionClassifier, SqlServerExceptionClassifier>();
        builder.Services.TryAddSingleton<IWarpNotificationTransportFactory, SqlServerNotificationTransportFactory>();

        builder.Services.TryAddSingleton<IWarpLockProvider>(sp =>
            new SqlServerLockProvider(ResolveConnectionString<TContext>(sp)));

        builder.Services.TryAddSingleton<IWarpSemaphoreProvider>(sp =>
            new SqlServerSemaphoreProvider(ResolveConnectionString<TContext>(sp)));

        return builder;
    }

    private static string ResolveConnectionString<TContext>(IServiceProvider sp)
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
                ?? throw new InvalidOperationException("Cannot resolve connection string for Warp SQL Server provider.");
        }

        return connectionString;
    }
}
