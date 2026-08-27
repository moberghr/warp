using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Warp.Core.Data.Converters;
using Warp.Core.Entities;

namespace Warp.Core;

internal static class WarpModelGuard
{
    private static readonly System.Reflection.Assembly WarpCoreAssembly = typeof(WarpModelGuard).Assembly;

    // Single sentinel for "the Warp model has been applied to this model". Job is the first entity
    // ApplyWarpModel registers, and the remaining entities + UTC converters are added unconditionally
    // in the same call — so Job present means ApplyWarpModel ran. Shared by ApplyWarpModel's
    // idempotency early-return and EnsureWarpModelApplied so the sentinel decision lives in one place.
    public static bool IsModelApplied(IReadOnlyModel model)
    {
        return model.FindEntityType(typeof(Job)) is not null;
    }

    // Turns EF Core's cryptic "Cannot create a DbSet for 'Job'" — thrown deep inside the first
    // publish against a context that never got Warp's model — into an actionable error that names
    // both fixes. Called from Publisher construction (publish path, never the worker hot path).
    public static void EnsureWarpModelApplied(DbContext context)
    {
        if (IsModelApplied(context.Model))
        {
            return;
        }

        var contextName = context.GetType().Name;

        throw new InvalidOperationException(
            $"Warp's EF Core model is not present on '{contextName}'. Warp stages jobs on your own "
            + "DbContext (the transactional outbox), so the context must include Warp's entities. "
            + $"Fix it one of two ways: (1) call AddWarp<{contextName}>(...) before the context's "
            + "options are built so the model customizer is wired onto DbContextOptions, or "
            + $"(2) call modelBuilder.ApplyWarpModel(schema) inside {contextName}.OnModelCreating — "
            + "the sanctioned pattern when a separate migrator / design-time host builds the context "
            + "without registering Warp. Without one of these, the first publish fails with EF Core's "
            + "\"Cannot create a DbSet for 'Job'\".");
    }

    // Fail-fast on the finalized model for storage retyping that ApplyWarpModel's build-time
    // ownership pass cannot reach: a runtime convention the host added via
    // ConfigureConventions(c => c.Conventions.Add(...)) runs at model FINALIZATION, after
    // OnModelCreating entirely, and can mutate Warp's properties past every pin. Left unchecked
    // that resurfaces as per-tick server-task failures ("operator does not exist: text = integer")
    // or claim SQL comparing literals of the wrong type — this turns it into one startup error
    // naming the property. Max-length/unicode facet drift is deliberately not validated (it cannot
    // break Warp's own SQL); a post-ApplyWarpModel HasColumnType override is likewise left to the
    // host, and the docs are explicit that changing a Warp column's underlying TYPE that way
    // recreates the divergence this guard exists to catch.
    //
    // Each arm checks BOTH retype signals (converter and provider CLR type) — a retype can arrive
    // through either one alone — and converter identity is reference-equality against Warp's own
    // instances, so a foreign converter of the right CLR shape (e.g. a local-time DateTime
    // round-trip) is still rejected. Native storage with no annotations is accepted everywhere:
    // it is physically correct, and hard-failing it would newly break the documented
    // sentinel-skip shape (a model that referenced Job before ApplyWarpModel ran).
    public static void EnsureWarpStorageContract(DbContext context)
    {
        var violations = new List<string>();

        foreach (var entity in context.Model.GetEntityTypes())
        {
            if (entity.ClrType.Assembly != WarpCoreAssembly)
            {
                continue;
            }

            foreach (var property in entity.GetProperties())
            {
                var violation = Validate(property);
                if (violation is not null)
                {
                    violations.Add($"{entity.ClrType.Name}.{property.Name} ({violation})");
                }
            }
        }

        if (violations.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "The storage type of a Warp-owned column has been changed on "
            + $"'{context.GetType().Name}': {string.Join(", ", violations)}. How Warp stores its own "
            + "columns is a fixed contract - its providers compare against literals of these types in "
            + "their atomic claim SQL, and its internal server context maps the same physical columns. "
            + "Warp neutralizes model-wide conversion conventions on its own entities automatically, so "
            + "this usually means one of: a runtime convention added via ConfigureConventions(c => "
            + "c.Conventions.Add(...)) is retyping Warp's properties at model finalization (scope it to "
            + "your own entity types); the context reconfigures a Warp entity's conversion after "
            + "ApplyWarpModel (remove the conversion - Warp's storage cannot be overridden); or the "
            + "model referenced a Warp entity type before ApplyWarpModel ran, tripping its idempotency "
            + "sentinel so Warp's own configuration was skipped (call ApplyWarpModel before declaring "
            + "entities that reference Warp types).");
    }

    // Per-model memo so the Publisher/BatchPublisher constructor backstop (non-hosted usage that
    // never runs WarpModelValidationService) pays the property walk once per model, not per scope.
    public static void EnsureWarpStorageContractOnce(DbContext context)
    {
        var model = context.Model;
        if (ValidatedModels.TryGetValue(model, out _))
        {
            return;
        }

        EnsureWarpStorageContract(context);

        // AddOrUpdate, not Add: two scopes can race past the TryGetValue above and Add throws for the loser.
        ValidatedModels.AddOrUpdate(model, ValidatedSentinel);
    }

    private static readonly ConditionalWeakTable<IModel, object> ValidatedModels = [];
    private static readonly object ValidatedSentinel = new();

    private static string? Validate(IReadOnlyProperty property)
    {
        var clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
        var providerType = property.GetProviderClrType();
        var converter = property.GetValueConverter();

        if (clrType.IsEnum)
        {
            // Native enum storage IS int, so a bare property (no annotations) is correct; what breaks
            // the claim SQL is a converter or a non-int provider type arriving through either signal.
            var stored = converter is null && (providerType is null || providerType == typeof(int));

            return stored
                ? null
                : $"enum stored as {converter?.ProviderClrType.Name ?? providerType?.Name}, expected int";
        }

        if (clrType == typeof(DateTime))
        {
            // Warp's own UTC converter (reference-equality - a foreign DateTime round-trip converter
            // can carry local-time semantics), or bare native storage; never a provider retype.
            var stored = providerType is null
                && (converter is null
                    || ReferenceEquals(converter, WarpStorageTypes.UtcDateTime)
                    || ReferenceEquals(converter, WarpStorageTypes.UtcNullableDateTime));

            return stored
                ? null
                : $"DateTime stored via {converter?.GetType().Name ?? providerType?.Name}, expected Warp's UTC converter or the native timestamp";
        }

        // WebhookDelivery.RetrySchedule persists through Warp's own JSON converter - a replacement
        // diverges TContext from the server context on the one column both sides parse (§8.20).
        if (clrType == typeof(IReadOnlyList<TimeSpan>))
        {
            var stored = converter is null || ReferenceEquals(converter, RetryScheduleConverter.Converter);

            return stored
                ? null
                : $"stored via {converter!.GetType().Name}, expected Warp's retry-schedule converter";
        }

        if (converter is not null || providerType is not null)
        {
            return $"stored as {providerType?.Name ?? converter?.ProviderClrType.Name}, expected the native type";
        }

        return null;
    }
}
