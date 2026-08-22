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
    /// addon entities, and the storage types Warp pins on its own columns — to
    /// <paramref name="modelBuilder"/> under <paramref name="schema"/> (default <c>"warp"</c>; pass
    /// <c>null</c> for the database's default schema).
    /// <para>
    /// The pinned storage is a contract, not a preference: Warp's providers compare against literals
    /// of a fixed type in their atomic claim statements, and the internal server context maps the same
    /// physical columns without replaying your <c>ConfigureConventions</c>. So enums are stored as
    /// <c>int</c>, <see cref="DateTime"/> as the provider's native timestamp (carrying Warp's UTC
    /// <c>Kind</c> converter), and <see cref="Guid"/> as the native uuid type — on Warp's own entity
    /// types only, never yours. A model-wide conversion convention
    /// (<c>Properties&lt;Enum&gt;().HaveConversion&lt;string&gt;()</c> and friends) therefore applies
    /// to your entities and stops at Warp's — facet conventions (max length, column type, unicode,
    /// precision) and conversion comparers included. <strong>A conversion set by hand on a Warp entity
    /// property is not supported from any position</strong>: one declared before this call is
    /// reclaimed, and one that survives to the finalized model (declared after it, or injected by a
    /// runtime convention) fails host startup with an error naming the property. A deliberate
    /// NON-type-changing facet override placed after this call remains the supported escape hatch.
    /// </para>
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

        // A consumer's pre-convention configuration (ConfigureConventions) is applied to each
        // property AT CREATION with the same configuration source as explicit fluent calls, so it
        // cannot be told apart from Warp's own declarations afterwards. Ownership of Warp's columns
        // is therefore enforced by ordering: declare the model (user defaults land on creation, Warp's
        // explicit facets overwrite where declared), strip every storage-affecting setting from Warp's
        // properties, re-declare so Warp's own facets and converters come back, then pin the storage
        // types. Net effect: conventions keep applying to the consumer's entities and never to Warp's
        // (§5.12). Naming conventions are untouched throughout — they are honoured by design (§2.14).
        AddWarpEntities(modelBuilder, schema);
        modelBuilder.ReclaimWarpStorage();
        AddWarpEntities(modelBuilder, schema);
        modelBuilder.PinWarpStorageTypes();

        return modelBuilder;
    }

    private static void AddWarpEntities(ModelBuilder modelBuilder, string? schema)
    {
        modelBuilder.AddOutboxStateEntity(schema);

        // Addon entities are registered unconditionally regardless of which addons the host opts into
        // (§2.11) — the migration story must not depend on opt-in mirroring across hosts.
        ServiceConfiguration.AddConcurrencyLimitEntity(modelBuilder, schema);
        ServiceConfiguration.AddCircuitBreakerStateEntity(modelBuilder, schema);
        ServiceConfiguration.AddRateLimitBucketEntity(modelBuilder, schema);
        ServiceConfiguration.AddRateLimitOverrideEntity(modelBuilder, schema);
        ServiceConfiguration.AddSagaStateEntity(modelBuilder, schema);
        ServiceConfiguration.AddSagaJobLinkEntity(modelBuilder, schema);
    }
}
