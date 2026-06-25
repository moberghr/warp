using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Warp.Core;

// PROTOTYPE (spec 2026-06-25-warp-server-context). A Warp-owned, runtime-only mirror of the Warp
// model used for autonomous server-internal DB work, so that work can carry its own (quiet)
// ILoggerFactory instead of polluting the user's command logs. Maps to the same physical tables as
// TContext by pulling resolved names from TContext's model (see WarpServerModel). Excluded from
// migrations — TContext remains the schema owner.
//
// Bootstrap under test here: resolving TContext's IModel at this context's model-build time via the
// injected application IServiceProvider. TContext's model is independent (no cycle) and cached after
// first build; OnModelCreating runs once per (cached) model.
internal sealed class WarpServerContext<TContext> : DbContext
    where TContext : DbContext
{
    private readonly IServiceProvider _applicationServices;

    public WarpServerContext(DbContextOptions<WarpServerContext<TContext>> options, IServiceProvider applicationServices)
        : base(options)
    {
        _applicationServices = applicationServices;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyWarpModel();

        using var scope = _applicationServices.CreateScope();
        var sourceModel = scope.ServiceProvider.GetRequiredService<TContext>().Model;
        WarpServerModel.MirrorNames(modelBuilder, sourceModel);
    }
}
