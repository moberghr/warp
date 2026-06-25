using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Warp.Core.Entities;

namespace Warp.Core;

internal static class WarpModelGuard
{
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
}
