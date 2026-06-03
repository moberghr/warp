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
        modelBuilder.AddOutboxStateEntity(schema);

        // All addon entities are registered unconditionally regardless of whether the host
        // called opt.AddConcurrency() / AddCircuitBreaker() / AddRateLimit() / AddSagas().
        // Behaviors and services for those addons remain opt-in (configured by the builder
        // methods), but the schema must be present so migrations from a host that doesn't
        // opt in still cover a downstream host that does. Eliminates the
        // "schema-affecting opt-ins must mirror across hosts" footgun.
        ServiceConfiguration.AddConcurrencyLimitEntity(modelBuilder, schema);
        ServiceConfiguration.AddCircuitBreakerStateEntity(modelBuilder, schema);
        ServiceConfiguration.AddRateLimitBucketEntity(modelBuilder, schema);
        ServiceConfiguration.AddRateLimitOverrideEntity(modelBuilder, schema);
        ServiceConfiguration.AddSagaStateEntity(modelBuilder, schema);
        ServiceConfiguration.AddSagaJobLinkEntity(modelBuilder, schema);

        // External addons (provider packages, third-party extensions) can still contribute
        // entities via WarpConfiguration.EntityConfigurators. In-tree addons no longer use
        // this — they target the unconditional path above.
        if (config != null)
        {
            foreach (var configurator in config.EntityConfigurators)
            {
                configurator(modelBuilder, schema);
            }
        }

        modelBuilder.ApplyWarpUtcDateTimeConverters();
    }
}
