using System.Text.Json;
using Shouldly;
using Warp.Core.Handlers;

namespace Warp.Tests.Core;

[Trait("Category", "NoDb")]
public class MetadataConvertTests
{
    private enum SampleMode
    {
        First = 1,
        Second = 2,
    }

    [TimedFact]
    public void To_Enum_FromLong_ReturnsEnumValue()
    {
        // NativeObjectConverter deserializes JSON numbers as long, so this is the dominant path.
        var result = MetadataConvert.To<SampleMode>(2L);

        result.ShouldBe(SampleMode.Second);
    }

    [TimedFact]
    public void To_NullableEnum_FromLong_ReturnsEnumValue()
    {
        var result = MetadataConvert.To<SampleMode?>(1L);

        result.ShouldBe(SampleMode.First);
    }

    [TimedFact]
    public void To_NullableEnum_FromNull_ReturnsNull()
    {
        var result = MetadataConvert.To<SampleMode?>(null);

        result.ShouldBeNull();
    }

    [TimedFact]
    public void To_Enum_FromBoxedEnum_ReturnsSameValue()
    {
        // In-memory path (publish-time write, read before any DB round trip).
        object boxed = SampleMode.Second;

        var result = MetadataConvert.To<SampleMode>(boxed);

        result.ShouldBe(SampleMode.Second);
    }

    [TimedFact]
    public void To_Enum_FromJsonElement_ReturnsEnumValue()
    {
        // After a JSON round trip without NativeObjectConverter (e.g. nested objects), the inner
        // values surface as JsonElement. The JsonElement branch handles that.
        var doc = JsonDocument.Parse("2");
        var element = doc.RootElement;

        var result = MetadataConvert.To<SampleMode>(element);

        result.ShouldBe(SampleMode.Second);
    }

    [TimedFact]
    public void To_Enum_FromString_ParsesEnumName()
    {
        // Enums round-trip through JSON as their declared name (JsonStringEnumConverter
        // is registered on MetadataSerializer.Options). Reading the metadata back via
        // the generated accessor hands a string to MetadataConvert; case-insensitive
        // Enum.TryParse turns it into the typed value.
        var result = MetadataConvert.To<SampleMode>("Second");

        result.ShouldBe(SampleMode.Second);
    }

    [TimedFact]
    public void To_NullableEnum_FromString_ParsesEnumName()
    {
        var result = MetadataConvert.To<SampleMode?>("Second");

        result.ShouldBe(SampleMode.Second);
    }

    [TimedFact]
    public void To_Enum_FromUnknownString_ReturnsDefault()
    {
        // A string that doesn't match any declared name on the enum falls through to
        // default(T) rather than throwing, preserving the metadata accessor's
        // can't-blow-up contract.
        var result = MetadataConvert.To<SampleMode>("NotARealValue");

        result.ShouldBe(default);
    }

    [TimedFact]
    public void To_DateTime_FromIsoString_ReturnsDateTime()
    {
        // JsonSerializer.Serialize writes DateTime as ISO 8601; NativeObjectConverter
        // surfaces JsonTokenType.String as a string. Without this branch, Total-scope
        // timeout would silently lose its DeadlineUtc after the first JSON roundtrip.
        var utc = new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc);
        var iso = utc.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

        var result = MetadataConvert.To<DateTime>(iso);

        result.ShouldBe(utc);
        result.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [TimedFact]
    public void To_NullableDateTime_FromIsoString_ReturnsDateTime()
    {
        var utc = new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc);
        var iso = utc.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

        var result = MetadataConvert.To<DateTime?>(iso);

        result.ShouldNotBeNull();
        result!.Value.ShouldBe(utc);
    }

    [TimedFact]
    public void To_NullableDateTime_FromNull_ReturnsNull()
    {
        var result = MetadataConvert.To<DateTime?>(null);

        result.ShouldBeNull();
    }

    [TimedFact]
    public void To_DateTime_FromMalformedString_ReturnsDefault()
    {
        // Defensive: a non-date string under a DateTime-typed key shouldn't blow up the
        // metadata accessor — fall through to default(T) so the caller's `?? Fallback` recovers.
        var result = MetadataConvert.To<DateTime?>("not-a-date");

        result.ShouldBeNull();
    }
}
