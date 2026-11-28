using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Warp.Core;

// Registration and options shared by Warp's own contexts (the server context and the Warp context, rule 2.14).
internal static class WarpContextOptions
{
    // Registers a Warp-owned context with options built ONCE (optionsLifetime Singleton), not per context: every
    // claim, flush and server-task pass creates one, and rebuilding the options (the configurator, the interceptor,
    // ConfigureWarnings cloning EF's options extensions) cost several KB per context. Nothing in them varies by scope.
    // Idempotent: AddWarp runs twice under AddWarpServer.
    public static void Register<TOwned>(IServiceCollection services, Action<IServiceProvider, DbContextOptionsBuilder> configure)
        where TOwned : DbContext
    {
        if (services.Any(x => x.ServiceType == typeof(TOwned)))
        {
            return;
        }

        // Snapshot the non-generic DbContextOptions forwarders present before the registration, so the one
        // AddDbContext is about to append can be identified (by reference) and removed.
        var forwardersBefore = services
            .Where(x => x.ServiceType == typeof(DbContextOptions))
            .ToList();

        services.AddDbContext<TOwned>(configure, contextLifetime: ServiceLifetime.Scoped, optionsLifetime: ServiceLifetime.Singleton);

        // AddDbContext also appends a non-generic DbContextOptions forwarder (plain Add, not TryAdd) carrying
        // TOwned as its ContextType. That enumeration — GetServices<DbContextOptions>().Select(o => o.ContextType)
        // — is the only vector EF's design-time tooling uses to discover this context (it's internal,
        // open-generic, and outside the user's startup assembly). Left in, `dotnet ef` counts the runtime-only
        // context and demands --context. Warp only ever resolves DbContextOptions<TOwned>, so dropping the
        // forwarder leaves runtime untouched; the user's own AddDbContext keeps its forwarder (→ TContext), the
        // correct design-time target. No property identifies a forwarder by its target context (the factory
        // closure is opaque), so it is removed by identity.
        var addedForwarder = services
            .Where(x => x.ServiceType == typeof(DbContextOptions))
            .Except(forwardersBefore)
            .Single();
        services.Remove(addedForwarder);
    }

    // The connection from the provider, Warp's save interceptor, and the logging both Warp contexts share.
    public static void Configure(
        DbContextOptionsBuilder options,
        IServiceProvider applicationServices,
        IWarpServerContextConfigurator configurator,
        bool demoteCommandLogging)
    {
        configurator.Configure(options, applicationServices);
        options.AddWarpInterceptors();

        // One ConfigureWarnings call rather than one per setting: each call clones EF's options extensions.
        options.ConfigureWarnings(x =>
        {
            // Keep Warp's own SQL out of the application's command logs: demote the command-executed event to
            // Debug (the application stays at Information). Opt back in with opt.EnableServerCommandLogging.
            if (demoteCommandLogging)
            {
                x.Log((RelationalEventId.CommandExecuted, LogLevel.Debug));
            }

            // The server's sweeps take a bounded slice with Take(batchSize) and no OrderBy, which EF warns about
            // on every execution — ExpirationCleanup alone runs nine of them per tick. Unordered is CORRECT for
            // these: "delete any N expired rows" has no meaningful order and the sweep repeats until the table
            // drains. EF offers no per-query suppression, so the suppression is scoped to Warp's own contexts.
            x.Ignore(CoreEventId.RowLimitingOperationWithoutOrderByWarning);
        });
    }

    // The application's command timeout, carried over so a host that raised it for a large dashboard keeps it.
    // The only setting of TContext's that a Warp context copies: it is a relational option with no effect on
    // the model or the SQL.
    public static void CopyCommandTimeout(DbContextOptionsBuilder options, DbContextOptions applicationOptions)
    {
        var timeout = applicationOptions.Extensions
            .OfType<RelationalOptionsExtension>()
            .FirstOrDefault()
            ?.CommandTimeout;

        var relational = options.Options.Extensions
            .OfType<RelationalOptionsExtension>()
            .FirstOrDefault();

        if (timeout is null || relational is null)
        {
            return;
        }

        ((IDbContextOptionsBuilderInfrastructure)options).AddOrUpdateExtension(relational.WithCommandTimeout(timeout));
    }
}

// Whether Warp's own contexts log their commands at Information. Registered by AddWarpServer from
// opt.EnableServerCommandLogging; absent in an AddWarp-only process, where Warp's SQL logs at Debug.
internal sealed class WarpCommandLogging
{
    public WarpCommandLogging(bool enabled)
    {
        Enabled = enabled;
    }

    public bool Enabled { get; }
}
