using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Warp.Core;

// Decides, once per process, whether IWarpContext is the Warp context or the scoped TContext. The Warp context
// needs a provider (UsePostgreSql / UseSqlServer registers the configurator) whose provider TContext actually
// runs on: an application's tests swap TContext to EF in-memory or SQLite while keeping UsePostgreSql in
// Program.cs (#359), and the Warp context would then open PostgreSQL against a connection string that is not
// there. Those processes keep the services on TContext, so a test reads back what it published.
internal sealed class WarpContextSelection<TContext>
    where TContext : DbContext
{
    private readonly Lazy<bool> _useWarpContext;

    public WarpContextSelection(
        IEnumerable<IWarpServerContextConfigurator> configurators,
        WarpDatabaseConnection<TContext> connection,
        ILogger<WarpContextSelection<TContext>> logger)
    {
        _useWarpContext = new Lazy<bool>(
            () => Decide(configurators.LastOrDefault(), connection, logger),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool UseWarpContext => _useWarpContext.Value;

    private static bool Decide(
        IWarpServerContextConfigurator? configurator,
        WarpDatabaseConnection<TContext> connection,
        ILogger logger)
    {
        if (configurator is null)
        {
            logger.LogDebug(
                "No Warp provider is registered; the dashboard and admin services use {Context}.",
                typeof(TContext).Name);

            return false;
        }

        if (!configurator.Supports(connection.Options))
        {
            logger.LogDebug(
                "{Context} does not run on the Warp provider's database; the dashboard and admin services use it.",
                typeof(TContext).Name);

            return false;
        }

        return true;
    }
}
