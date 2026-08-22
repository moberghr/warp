using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Warp.Core.Data.Converters;

namespace Warp.Core;

internal sealed class WarpModelCustomizer : RelationalModelCustomizer
{
    public WarpModelCustomizer(ModelCustomizerDependencies dependencies)
        : base(dependencies)
    {
    }

    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);

        var config = context.GetService<IOptions<WarpConfiguration>>()?.Value;
        var schema = config != null ? config.Schema : "warp";

        // The full in-tree model (job-store + unconditional addon entities + UTC converters) lives
        // in the public ApplyWarpModel extension. Idempotent: if the user already called it from
        // their own OnModelCreating (the sanctioned design-time pattern), this is a no-op and theirs
        // wins — no double registration.
        modelBuilder.ApplyWarpModel(schema);

        // External addons (provider packages, third-party extensions) can still contribute
        // entities via WarpConfiguration.EntityConfigurators. In-tree addons target ApplyWarpModel.
        if (config != null)
        {
            foreach (var configurator in config.EntityConfigurators)
            {
                configurator(modelBuilder, schema);
            }

            // A configurator may add properties to a WARP-owned entity (its own entities are outside
            // the assembly filter and untouched). Those additions land after ApplyWarpModel's
            // ownership pass, so re-pin: an added enum stores as int and an added DateTime carries
            // the UTC converter, keeping the boot-time storage contract satisfiable for addons.
            modelBuilder.PinWarpStorageTypes();
        }
    }
}
