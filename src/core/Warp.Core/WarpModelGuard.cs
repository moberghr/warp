using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
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
    // naming the property. Facet drift (max length, column type) is deliberately not validated
    // here: it cannot break Warp's own SQL, and a deliberate post-ApplyWarpModel override of a
    // facet remains the host's documented escape hatch.
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
            + "this usually means a runtime convention added via ConfigureConventions(c => "
            + "c.Conventions.Add(...)) is retyping Warp's properties at model finalization, or the "
            + "context reconfigures a Warp entity's conversion after ApplyWarpModel. Scope the "
            + "convention to your own entity types, or remove the conversion from Warp's properties.");
    }

    private static string? Validate(IReadOnlyProperty property)
    {
        var clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
        var providerType = property.GetProviderClrType();
        var converter = property.GetValueConverter();

        if (clrType.IsEnum)
        {
            return providerType == typeof(int)
                ? null
                : $"enum stored as {providerType?.Name ?? "its default"}, expected int";
        }

        if (clrType == typeof(DateTime))
        {
            var roundTrips = converter is not null
                && (Nullable.GetUnderlyingType(converter.ProviderClrType) ?? converter.ProviderClrType) == typeof(DateTime);

            return roundTrips
                ? null
                : $"DateTime stored as {converter?.ProviderClrType.Name ?? providerType?.Name ?? "its default without Warp's UTC converter"}, expected Warp's UTC converter";
        }

        // WebhookDelivery.RetrySchedule persists through Warp's own JSON converter.
        if (clrType == typeof(IReadOnlyList<TimeSpan>))
        {
            return null;
        }

        if (converter is not null || providerType is not null)
        {
            return $"stored as {providerType?.Name ?? converter?.ProviderClrType.Name}, expected the native type";
        }

        return null;
    }
}
