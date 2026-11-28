using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Warp.Core;

// The Warp context (#344): the runtime-only mirror of the Warp model that the dashboard query, command and
// admin services use, registered by AddWarp so it exists in every process (a dashboard-only or publisher-only
// host included). Same model build and connection as the server context, but a separate type: the server
// context makes Job.RunId a concurrency token for single-worker finalization (#347), which must not apply to
// the dashboard's tracked job saves (two concurrent bulk requeues deadlocked on SQL Server with it).
// Never resolves TContext; excluded from migrations (TContext stays the schema owner).
internal sealed class WarpContext<TContext> : DbContext
    where TContext : DbContext
{
    private readonly WarpConfiguration _configuration;
    private readonly IWarpServerModelNames _modelNames;

    public WarpContext(
        DbContextOptions<WarpContext<TContext>> options,
        IOptions<WarpConfiguration> configuration,
        IWarpServerModelNames modelNames)
        : base(options)
    {
        _configuration = configuration.Value;
        _modelNames = modelNames;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        WarpMirrorModel.Apply(modelBuilder, _configuration, _modelNames);
    }
}
