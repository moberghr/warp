using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Warp.Core.Data.Converters;

// How Warp's own columns are stored is a contract, not a preference: the providers' atomic claim
// statements compare against literals of a fixed type (§1.6), and WarpServerContext mirrors table
// and column NAMES from TContext without replaying its ConfigureConventions — so a consuming
// context whose convention retypes a Warp column leaves the two contexts disagreeing about what is
// physically there, and the server tasks fail every tick against their own tables.
//
// Pre-convention model configuration (ModelConfigurationBuilder) is applied to each property AT
// CREATION with Explicit configuration source — indistinguishable from Warp's own fluent calls, so
// it cannot be filtered by source. Ownership is therefore enforced by ordering instead:
// ApplyWarpModel applies Warp's entity declarations, RESETS every storage-affecting facet and
// conversion this pass knows about, re-applies the declarations (restoring Warp's own explicit
// facets and converters), and finishes with the pins below. The consumer's conventions keep
// applying to the consumer's own entities — both passes are scoped to Warp.Core's entity CLR types.
//
// UTC stamping is part of the final pass. SQL Server datetime/datetime2 columns carry no timezone
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

    // Strips every storage-affecting setting a consumer's pre-convention configuration injected
    // into Warp's own properties: conversions, comparers AND facets (column type, max length, unicode,
    // precision/scale, fixed length, collation). Column NAMES are deliberately untouched — naming
    // conventions are honoured by design (§2.14 mirrors them). Warp's own declarations are wiped
    // too; ApplyWarpModel re-applies them immediately after.
    internal static void ReclaimWarpStorage(this ModelBuilder modelBuilder)
    {
        foreach (var property in WarpProperties(modelBuilder))
        {
            property.SetValueConverter((ValueConverter?)null);
            property.SetValueComparer((ValueComparer?)null);
            property.SetProviderValueComparer((ValueComparer?)null);
            property.SetProviderClrType(null);
            property.SetColumnType(null);
            property.SetMaxLength(null);
            property.SetIsUnicode(null);
            property.SetPrecision(null);
            property.SetScale(null);
            property.SetIsFixedLength(null);
            property.SetCollation(null);
        }
    }

    // Applied by WarpModelCustomizer in production and by TestContext.OnModelCreating in tests.
    // Scoped to Warp.Core's own entity CLR types (assembly-equality rather than namespace prefix)
    // so it can't bleed into a user's entity that happens to live under Warp.*.
    internal static void PinWarpStorageTypes(this ModelBuilder modelBuilder)
    {
        foreach (var property in WarpProperties(modelBuilder))
        {
            PinProperty(property);
        }
    }

    private static IEnumerable<IMutableProperty> WarpProperties(ModelBuilder modelBuilder)
    {
        return modelBuilder.Model.GetEntityTypes()
            .Where(x => x.ClrType.Assembly == WarpCoreAssembly)
            .SelectMany(x => x.GetProperties());
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
