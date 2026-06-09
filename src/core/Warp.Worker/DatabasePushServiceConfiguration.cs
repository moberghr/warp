using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Warp.Core.Notifications;
using Warp.Worker.Services;

namespace Warp.Worker;

/// <summary>
/// Opt-in DB-push extension on the Warp builder. Call <c>opt.UseDatabasePush()</c> inside the
/// <c>AddWarp</c> or <c>AddWarpServer</c> lambda (after <c>UsePostgreSql()</c> or
/// <c>UseSqlServer()</c>) to replace the default polling wake-up on the dispatcher,
/// <c>MessageRouter</c>, and <c>Orchestrator</c> with push notifications delivered via
/// the provider's native mechanism (Postgres LISTEN/NOTIFY, SQL Server Service Broker). Provider
/// selection is transparent: the transport is constructed from whichever
/// <see cref="IWarpNotificationTransportFactory"/> the provider package registered.
/// </summary>
public static class DatabasePushServiceConfiguration
{
    public static Warp.Core.IWarpBuilder<TContext> UseDatabasePush<TContext>(
        this Warp.Core.IWarpBuilder<TContext> builder,
        Action<WarpDatabasePushConfiguration>? configure = null)
        where TContext : DbContext
    {
        // Fail fast at setup time if no provider has been opted into yet. Without this check
        // the error surfaces only when the IWarpNotificationTransport singleton is first
        // resolved (typically inside a hosted service at app startup), which is much harder
        // to diagnose. The ordering contract — UsePostgreSql()/UseSqlServer() must come
        // before UseDatabasePush() — is enforced here, in the lambda, in the same call order.
        if (!builder.Services.Any(x => x.ServiceType == typeof(IWarpNotificationTransportFactory)))
        {
            throw new InvalidOperationException(
                "UseDatabasePush requires a provider package. Call opt.UsePostgreSql() or opt.UseSqlServer() inside the AddWarp/AddWarpServer lambda BEFORE opt.UseDatabasePush().");
        }

        var options = new WarpDatabasePushConfiguration();
        configure?.Invoke(options);
        builder.Services.AddSingleton(options);

        // When push is enabled, signal-driven server tasks (MessageRouter, Orchestrator)
        // don't need a tight polling cadence — push wakes them immediately, polling is the
        // backstop. Bump still-at-default intervals so idle bookkeeping doesn't dominate.
        // Explicit override wins: set MessageRoutingInterval/OrchestrationInterval AFTER
        // UseDatabasePush() if a tighter cadence is desired.
        if (builder is WarpServerConfiguration workerConfig)
        {
            if (workerConfig.MessageRoutingInterval == TimeSpan.FromSeconds(1))
            {
                workerConfig.MessageRoutingInterval = TimeSpan.FromSeconds(30);
            }

            if (workerConfig.OrchestrationInterval == TimeSpan.FromSeconds(10))
            {
                workerConfig.OrchestrationInterval = TimeSpan.FromMinutes(1);
            }

            // The dispatcher's polling-backoff ceiling. With push wired up, polling is just
            // the silent-transport-failure safety net — 30s is far tighter than necessary.
            // 5min still meets PushFailurePollingBackstop's contract (max ~3× polling).
            if (workerConfig.MaxPollingInterval == TimeSpan.FromSeconds(30))
            {
                workerConfig.MaxPollingInterval = TimeSpan.FromMinutes(5);
            }
        }

        // Replace the default NullNotificationTransport with the provider-specific one.
        // RemoveAll is required because the null transport was added via TryAddSingleton in AddWarp.
        builder.Services.RemoveAll<IWarpNotificationTransport>();
        builder.Services.AddSingleton<IWarpNotificationTransport>(sp =>
        {
            var factory = sp.GetService<IWarpNotificationTransportFactory>()
                ?? throw new InvalidOperationException(
                    "UseDatabasePush requires a provider package. Call opt.UsePostgreSql() or opt.UseSqlServer() inside the AddWarp/AddWarpServer lambda before opt.UseDatabasePush().");

            // AddDbContext registers DbContextOptions<TContext> as Scoped, so the singleton
            // factory must resolve it inside a scope — otherwise ValidateScopes=true rejects
            // the resolution from the root provider (silently broken in Dev environments).
            using var scope = sp.CreateScope();
            var dbOptions = scope.ServiceProvider.GetRequiredService<DbContextOptions<TContext>>();
            var relationalExtension = dbOptions.Extensions.OfType<RelationalOptionsExtension>().FirstOrDefault();
            var connectionString = relationalExtension?.ConnectionString;

            // Factory-configured DbContexts (UseNpgsql(sp => ...)) have the extension present but
            // with a null connection string — fall back to the scoped context.
            if (string.IsNullOrEmpty(connectionString))
            {
                var context = scope.ServiceProvider.GetRequiredService<TContext>();
                connectionString = context.Database.GetConnectionString()
                    ?? throw new InvalidOperationException("Cannot resolve connection string for Warp DB push.");
            }

            return factory.Create(connectionString, options, sp.GetRequiredService<ILoggerFactory>());
        });

        builder.Services.AddHostedService<NotificationListenerTask<TContext>>();

        return builder;
    }
}
