using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
        }
    }
}
