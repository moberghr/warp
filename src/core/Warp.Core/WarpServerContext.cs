using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Warp.Core.Data.Converters;

namespace Warp.Core;

// A Warp-owned, runtime-only mirror of the Warp model used for all autonomous server-internal DB
// work (worker fetch/complete, server tasks, background-service host), so that work carries its own
// (quiet) ILoggerFactory instead of polluting the user's command logs. Maps to the same physical
// tables as TContext by pinning resolved names from IWarpServerModelNames — so a naming convention
// on TContext is honoured without replaying it, and the context never resolves TContext itself.
// Excluded from migrations: TContext remains the schema owner.
internal sealed class WarpServerContext<TContext> : DbContext, IWarpServerContext
    where TContext : DbContext
{
    private readonly WarpConfiguration _configuration;
    private readonly IWarpServerModelNames _modelNames;

    public WarpServerContext(
        DbContextOptions<WarpServerContext<TContext>> options,
        IOptions<WarpConfiguration> configuration,
        IWarpServerModelNames modelNames)
        : base(options)
    {
        _configuration = configuration.Value;
        _modelNames = modelNames;
    }

    public DbContext Context => this;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Mirror TContext's full model build (ApplyWarpModel + external EntityConfigurators, under the
        // configured schema), then pin the resolved physical names so the server context maps to the
        // same tables.
        var schema = _configuration.Schema;
        modelBuilder.ApplyWarpModel(schema);
        foreach (var configurator in _configuration.EntityConfigurators)
        {
            configurator(modelBuilder, schema);
        }

        // Same re-pin as WarpModelCustomizer: a configurator-added property on a Warp-owned entity
        // must store identically on both contexts.
        modelBuilder.PinWarpStorageTypes();

        WarpServerModel.MirrorNames(modelBuilder, _modelNames);
    }
}
