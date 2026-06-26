using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Warp.Core;

// A Warp-owned, runtime-only mirror of the Warp model used for all autonomous server-internal DB
// work (worker fetch/complete, server tasks, background-service host), so that work carries its own
// (quiet) ILoggerFactory instead of polluting the user's command logs. Maps to the same physical
// tables as TContext by pulling resolved names from TContext's model (see WarpServerModel) — so a
// naming convention on TContext is honoured without replaying it. Excluded from migrations: TContext
// remains the schema owner.
//
// Bootstrap: TContext's IModel is resolved at this context's model-build time via a scope from the
// injected IServiceScopeFactory. TContext's model is independent (no cycle) and cached after first
// build; OnModelCreating runs once per (cached) model.
internal sealed class WarpServerContext<TContext> : DbContext, IWarpServerContext
    where TContext : DbContext
{
    private readonly IServiceScopeFactory _scopeFactory;

    public WarpServerContext(DbContextOptions<WarpServerContext<TContext>> options, IServiceScopeFactory scopeFactory)
        : base(options)
    {
        _scopeFactory = scopeFactory;
    }

    public DbContext Context => this;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        using var scope = _scopeFactory.CreateScope();
        var serviceProvider = scope.ServiceProvider;

        // Mirror TContext's full model build (ApplyWarpModel + external EntityConfigurators, under the
        // configured schema) so the server context maps every entity TContext does — then pin the
        // resolved physical names so a naming convention on TContext is honoured without replay.
        var config = serviceProvider.GetService<IOptions<WarpConfiguration>>()?.Value;
        var schema = config != null ? config.Schema : "warp";
        modelBuilder.ApplyWarpModel(schema);
        if (config != null)
        {
            foreach (var configurator in config.EntityConfigurators)
            {
                configurator(modelBuilder, schema);
            }
        }

        var sourceModel = serviceProvider.GetRequiredService<TContext>().Model;
        WarpServerModel.MirrorNames(modelBuilder, sourceModel);
    }
}
