using Microsoft.EntityFrameworkCore;
using Warp.Core.Data.Converters;
using Warp.Core.Entities;

namespace Warp.Core;

/// <summary>
/// Public entry point for contributing Warp's EF Core model to a <see cref="DbContext"/>.
/// </summary>
public static class WarpModelExtensions
{
    /// <summary>
    /// Adds Warp's entire EF Core model — the job-store entities, the unconditionally-registered
    /// addon entities, and the UTC <see cref="DateTime"/> converters — to <paramref name="modelBuilder"/>
    /// under <paramref name="schema"/> (default <c>"warp"</c>; pass <c>null</c> for the database's
    /// default schema).
    /// <para>
    /// Call this inside your <c>DbContext.OnModelCreating</c> to make Warp's model contribution
    /// explicit and visible to design-time tooling. When the model is declared in the context's own
    /// <c>OnModelCreating</c>, every runtime host and <c>dotnet ef</c> see an identical model — there
    /// is no <c>DbContextOptions</c> divergence between a host that registered <c>AddWarp</c> and a bare
    /// migrator that did not, which is the usual cause of empty/under-specified migrations.
    /// </para>
    /// <para>
    /// Idempotent: a no-op when the Warp model is already present, so it composes safely with the
    /// implicit <c>IModelCustomizer</c> that <c>AddWarp</c> still wires by default. External addon
    /// entities contributed via <see cref="WarpConfiguration.EntityConfigurators"/> are applied only
    /// through that DI customizer path; the in-tree entities are covered here.
    /// </para>
    /// <para>
    /// Presence of the <c>Job</c> entity is the idempotency sentinel — call this before your own
    /// model references any Warp entity type (e.g. a navigation to <c>Warp.Core.Entities.Job</c>),
    /// otherwise the early-return treats the model as already built and skips the remaining entities.
    /// </para>
    /// </summary>
    public static ModelBuilder ApplyWarpModel(this ModelBuilder modelBuilder, string? schema = "warp")
    {
        // Idempotency guard: lets the explicit OnModelCreating call and the DI customizer both route
        // through here without double-registering entities/indexes. Shares the single Job sentinel
        // with WarpModelGuard (see WarpModelGuard.IsModelApplied).
        if (WarpModelGuard.IsModelApplied(modelBuilder.Model))
        {
            return modelBuilder;
        }

        modelBuilder.AddOutboxStateEntity(schema);

        // Addon entities are registered unconditionally regardless of which addons the host opts into
        // (§2.11) — the migration story must not depend on opt-in mirroring across hosts.
        ServiceConfiguration.AddConcurrencyLimitEntity(modelBuilder, schema);
        ServiceConfiguration.AddCircuitBreakerStateEntity(modelBuilder, schema);
        ServiceConfiguration.AddRateLimitBucketEntity(modelBuilder, schema);
        ServiceConfiguration.AddRateLimitOverrideEntity(modelBuilder, schema);
        ServiceConfiguration.AddSagaStateEntity(modelBuilder, schema);
        ServiceConfiguration.AddSagaJobLinkEntity(modelBuilder, schema);

        // Scoped to Warp.Core's own entity CLR types, so safe to run before external configurators.
        modelBuilder.ApplyWarpUtcDateTimeConverters();

        return modelBuilder;
    }
}
