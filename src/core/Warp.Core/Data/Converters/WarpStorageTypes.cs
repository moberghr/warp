using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Warp.Core.Data.Converters;

// How Warp's own columns are stored is a contract, not a preference: the providers' atomic claim
// statements compare against literals of a fixed type (§1.6), and WarpServerContext mirrors table
// and column NAMES from TContext without replaying its ConfigureConventions — so a consuming
// context whose convention retypes a Warp column leaves the two contexts disagreeing about what is
// physically there, and the server tasks fail every tick against their own tables. This pass runs
// last in ApplyWarpModel and pins the three families a global convention typically retargets, so
// `Properties<Enum>().HaveConversion<string>()`, `Properties<DateTime>().HaveConversion<long>()`
// and friends stay confined to the consumer's own entities.
//
// UTC stamping is part of the same pass. SQL Server datetime/datetime2 columns carry no timezone
// marker, so EF Core materializes DateTime values with Kind=Unspecified. System.Text.Json then
// serializes them without a 'Z' suffix, and JavaScript Date() parses the string as local time.
// These converters stamp Kind=Utc on read so JSON output stays unambiguous and §5.7's UTC invariant
// holds end-to-end. On Postgres the read-side stamp is needed for `timestamp` (without time zone)
// columns too — it's only a no-op on `timestamptz`, where Npgsql already returns Kind=Utc.
internal static class WarpStorageTypes
{
    private static readonly System.Reflection.Assembly WarpCoreAssembly = typeof(WarpStorageTypes).Assembly;

    internal static readonly ValueConverter<DateTime, DateTime> UtcDateTime = new(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    internal static readonly ValueConverter<DateTime?, DateTime?> UtcNullableDateTime = new(
        v => v.HasValue ? ToUtcOnWrite(v.Value) : v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

    // Applied by WarpModelCustomizer in production and by TestContext.OnModelCreating in tests.
    // Scoped to Warp.Core's own entity CLR types (assembly-equality rather than namespace prefix)
    // so it can't bleed into a user's entity that happens to live under Warp.*.
    internal static void PinWarpStorageTypes(this ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (entityType.ClrType.Assembly != WarpCoreAssembly)
            {
                continue;
            }

            foreach (var property in entityType.GetProperties())
            {
                PinProperty(property);
            }
        }
    }

    private static void PinProperty(IMutableProperty property)
    {
        var clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;

        // Clearing the provider type before setting a converter matters: a convention that retyped
        // the property leaves one behind, and it would otherwise outrank what we set here.
        if (clrType == typeof(DateTime))
        {
            property.SetProviderClrType(null);
            property.SetValueConverter(property.ClrType == typeof(DateTime) ? UtcDateTime : UtcNullableDateTime);

            return;
        }

        if (clrType == typeof(Guid))
        {
            property.SetProviderClrType(null);
            property.SetValueConverter((ValueConverter?)null);

            return;
        }

        // Enums are also pinned explicitly at each entity declaration (§5.12) — that is the readable
        // record of the contract, this is the backstop that keeps a new entity from reopening the
        // hole. A Warp enum column stored as anything but an integer breaks the claim SQL outright.
        if (clrType.IsEnum)
        {
            property.SetValueConverter((ValueConverter?)null);
            property.SetProviderClrType(typeof(int));
        }
    }

    private static DateTime ToUtcOnWrite(DateTime value)
    {
        return value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
    }
}
