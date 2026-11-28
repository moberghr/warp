using Microsoft.EntityFrameworkCore;
using Warp.Core.Data;
using Warp.Core.Data.Converters;

namespace Warp.Core;

// The model build shared by Warp's own contexts (the server context and the Warp context): TContext's
// full Warp model (ApplyWarpModel + external EntityConfigurators, under the configured schema), the storage
// re-pin, the resolved physical names and the partial-index filters. Never TContext's conventions: names only
// (rule 2.14), and §5.12 guarantees the physical columns agree.
internal static class WarpMirrorModel
{
    public static void Apply(ModelBuilder modelBuilder, WarpConfiguration configuration, IWarpServerModelNames modelNames)
    {
        var schema = configuration.Schema;
        modelBuilder.ApplyWarpModel(schema);
        foreach (var configurator in configuration.EntityConfigurators)
        {
            configurator(modelBuilder, schema);
        }

        // Same re-pin as WarpModelCustomizer: a configurator-added property on a Warp-owned entity
        // must store identically on every context.
        modelBuilder.PinWarpStorageTypes();

        WarpServerModel.MirrorNames(modelBuilder, modelNames);

        // Like WarpModelCustomizer: the filters name columns, so they follow the names just pinned.
        WarpPartialIndexes.Apply(modelBuilder);
    }
}
