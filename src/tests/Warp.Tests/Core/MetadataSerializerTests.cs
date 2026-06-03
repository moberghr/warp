using Shouldly;
using Warp.Core;
using Warp.Core.Concurrency;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Helper;

namespace Warp.Tests.Core;

[Trait("Category", "NoDb")]
public class MetadataSerializerTests
{
    [TimedTheory]
    [InlineData("""{"Name":"Alice"}""", "Name", "Alice")]
    [InlineData("""{"Key":""}""", "Key", "")]
    [InlineData("""{"Key":"hello world"}""", "Key", "hello world")]
    public void Deserialize_StringValues_ReturnsString(string json, string key, string expected)
    {
        var dict = MetadataSerializer.Deserialize(json);

        dict[key].ShouldBeOfType<string>();
        dict[key].ShouldBe(expected);
    }

    [TimedTheory]
    [InlineData("""{"Count":0}""", "Count", 0L)]
    [InlineData("""{"Count":42}""", "Count", 42L)]
    [InlineData("""{"Count":-1}""", "Count", -1L)]
    [InlineData("""{"Big":2147483648}""", "Big", 2147483648L)]
    public void Deserialize_IntegerValues_ReturnsLong(string json, string key, long expected)
    {
        var dict = MetadataSerializer.Deserialize(json);

        dict[key].ShouldBeOfType<long>();
        dict[key].ShouldBe(expected);
    }

    [TimedTheory]
    [InlineData("""{"Value":3.14}""", "Value", 3.14)]
    [InlineData("""{"Value":0.5}""", "Value", 0.5)]
    public void Deserialize_FloatValues_ReturnsDouble(string json, string key, double expected)
    {
        var dict = MetadataSerializer.Deserialize(json);

        dict[key].ShouldBeOfType<double>();
        dict[key].ShouldBe(expected);
    }

    [TimedTheory]
    [InlineData("""{"Active":true}""", "Active", true)]
    [InlineData("""{"Active":false}""", "Active", false)]
    public void Deserialize_BooleanValues_ReturnsBool(string json, string key, bool expected)
    {
        var dict = MetadataSerializer.Deserialize(json);

        dict[key].ShouldBeOfType<bool>();
        dict[key].ShouldBe(expected);
    }

    [TimedFact]
    public void Deserialize_NullValue_ReturnsNull()
    {
        var dict = MetadataSerializer.Deserialize("""{"Key":null}""");

        dict.ShouldContainKey("Key");
        dict["Key"].ShouldBeNull();
    }

    [TimedFact]
    public void Deserialize_IntArray_ReturnsList()
    {
        var dict = MetadataSerializer.Deserialize("""{"Delays":[15,60,300]}""");

        var list = dict["Delays"].ShouldBeOfType<List<object>>();
        list.Count.ShouldBe(3);
        list[0].ShouldBe(15L);
        list[1].ShouldBe(60L);
        list[2].ShouldBe(300L);
    }

    [TimedFact]
    public void Deserialize_StringArray_ReturnsList()
    {
        var dict = MetadataSerializer.Deserialize("""{"Tags":["a","b","c"]}""");

        var list = dict["Tags"].ShouldBeOfType<List<object>>();
        list.Count.ShouldBe(3);
        list[0].ShouldBe("a");
        list[1].ShouldBe("b");
        list[2].ShouldBe("c");
    }

    [TimedFact]
    public void Deserialize_EmptyArray_ReturnsList()
    {
        var dict = MetadataSerializer.Deserialize("""{"Items":[]}""");

        var list = dict["Items"].ShouldBeOfType<List<object>>();
        list.Count.ShouldBe(0);
    }

    [TimedFact]
    public void Deserialize_MixedArray_ReturnsList()
    {
        var dict = MetadataSerializer.Deserialize("""{"Mixed":[1,"two",true]}""");

        var list = dict["Mixed"].ShouldBeOfType<List<object>>();
        list.Count.ShouldBe(3);
        list[0].ShouldBe(1L);
        list[1].ShouldBe("two");
        list[2].ShouldBe(true);
    }

    [TimedFact]
    public void Deserialize_NestedObject_ReturnsDictionary()
    {
        var dict = MetadataSerializer.Deserialize("""{"Outer":{"Inner":"value","Count":5}}""");

        var nested = dict["Outer"].ShouldBeOfType<Dictionary<string, object>>();
        nested["Inner"].ShouldBe("value");
        nested["Count"].ShouldBe(5L);
    }

    [TimedFact]
    public void Deserialize_MultipleKeys_AllConvertedCorrectly()
    {
        var dict = MetadataSerializer.Deserialize("""{"Name":"John","Age":30,"Active":true,"Score":9.5}""");

        dict["Name"].ShouldBe("John");
        dict["Age"].ShouldBe(30L);
        dict["Active"].ShouldBe(true);
        dict["Score"].ShouldBe(9.5);
    }

    [TimedFact]
    public void Deserialize_NullJson_ReturnsEmptyDictionary()
    {
        var dict = MetadataSerializer.Deserialize(null);

        dict.ShouldNotBeNull();
        dict.ShouldBeEmpty();
    }

    [TimedFact]
    public void Deserialize_EmptyString_ReturnsEmptyDictionary()
    {
        var dict = MetadataSerializer.Deserialize(string.Empty);

        dict.ShouldNotBeNull();
        dict.ShouldBeEmpty();
    }

    [TimedFact]
    public void Deserialize_EmptyObject_ReturnsEmptyDictionary()
    {
        var dict = MetadataSerializer.Deserialize("{}");

        dict.ShouldNotBeNull();
        dict.ShouldBeEmpty();
    }

    [TimedFact]
    public void Serialize_DictionaryWithNativeTypes_ProducesCorrectJson()
    {
        var dict = new Dictionary<string, object>
        {
            ["Name"] = "John",
            ["Age"] = 30,
            ["Active"] = true,
        };

        var json = MetadataSerializer.Serialize(dict);

        json.ShouldNotBeNull();
        json.ShouldContain("\"Name\":\"John\"");
        json.ShouldContain("\"Age\":30");
        json.ShouldContain("\"Active\":true");
    }

    [TimedFact]
    public void Serialize_EmptyDictionary_ReturnsNull()
    {
        MetadataSerializer.Serialize([]).ShouldBeNull();
    }

    [TimedFact]
    public void Serialize_NullDictionary_ReturnsNull()
    {
        MetadataSerializer.Serialize(null).ShouldBeNull();
    }

    [TimedFact]
    public void RoundTrip_NativeTypes_PreservedAfterSerializeDeserialize()
    {
        var original = new Dictionary<string, object>
        {
            ["Name"] = "Alice",
            ["Count"] = 42,
            ["Active"] = true,
            ["Score"] = 9.5,
        };

        var json = MetadataSerializer.Serialize(original)!;
        var restored = MetadataSerializer.Deserialize(json);

        restored["Name"].ShouldBe("Alice");
        restored["Count"].ShouldBe(42L);
        restored["Active"].ShouldBe(true);
        restored["Score"].ShouldBe(9.5);
    }

    [TimedFact]
    public void RoundTrip_ArrayValues_PreservedAfterSerializeDeserialize()
    {
        var original = new Dictionary<string, object>
        {
            ["Delays"] = new int[] { 15, 60, 300 },
        };

        var json = MetadataSerializer.Serialize(original)!;
        var restored = MetadataSerializer.Deserialize(json);

        var list = restored["Delays"].ShouldBeOfType<List<object>>();
        list.Count.ShouldBe(3);
        list[0].ShouldBe(15L);
        list[1].ShouldBe(60L);
        list[2].ShouldBe(300L);
    }

    [TimedFact]
    public void Deserialize_DeeplyNested_AllLevelsConverted()
    {
        const string json = """{"L1":{"L2":{"L3":"deep","Num":99}}}""";
        var dict = MetadataSerializer.Deserialize(json);

        var l1 = dict["L1"].ShouldBeOfType<Dictionary<string, object>>();
        var l2 = l1["L2"].ShouldBeOfType<Dictionary<string, object>>();
        l2["L3"].ShouldBe("deep");
        l2["Num"].ShouldBe(99L);
    }

    [TimedFact]
    public void Deserialize_ArrayOfObjects_ReturnsListOfDictionaries()
    {
        const string json = """{"Items":[{"Name":"A"},{"Name":"B"}]}""";
        var dict = MetadataSerializer.Deserialize(json);

        var list = dict["Items"].ShouldBeOfType<List<object>>();
        list.Count.ShouldBe(2);

        var first = list[0].ShouldBeOfType<Dictionary<string, object>>();
        first["Name"].ShouldBe("A");

        var second = list[1].ShouldBeOfType<Dictionary<string, object>>();
        second["Name"].ShouldBe("B");
    }

    [TimedFact]
    public void Serialize_EnumValue_WritesEnumNameAsString()
    {
        // Enums are persisted as their declared name so the dashboard and any external
        // metadata consumer renders "Skip" / "Wait" instead of the integer value.
        var dict = new Dictionary<string, object>
        {
            [nameof(IConcurrencyMetadata.ConcurrencyMode)] = ConcurrencyMode.Skip,
        };

        var json = MetadataSerializer.Serialize(dict);

        json.ShouldNotBeNull();
        json.ShouldContain("\"ConcurrencyMode\":\"Skip\"");
        json.ShouldNotContain("\"ConcurrencyMode\":1");
    }

    [TimedFact]
    public void RoundTrip_EnumThroughGeneratedAccessor_ReturnsTypedValue()
    {
        // End-to-end: write via the strongly-typed metadata interface, serialize, parse
        // back, read via the same interface. The string-name round-trip must produce the
        // same typed value the caller set.
        var parameters = new JobParameters().WithMutex("payment-42", ConcurrencyMode.Wait);

        var json = MetadataSerializer.Serialize(parameters.Metadata);
        var roundTripped = MetadataSerializer.Deserialize(json);
        var meta = MetadataFactory.Create<IConcurrencyMetadata>(roundTripped);

        meta.ConcurrencyKey.ShouldBe("payment-42");
        meta.ConcurrencyMode.ShouldBe(ConcurrencyMode.Wait);
        meta.ConcurrencyLimit.ShouldBe(1);
    }
}
