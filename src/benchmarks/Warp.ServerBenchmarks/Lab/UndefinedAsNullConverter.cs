using System.Text.Json;
using System.Text.Json.Serialization;

namespace Warp.ServerBenchmarks.Lab;

// Reads BenchmarkDotNet's statistics. A number is read as itself; anything else in a number's place
// ("NaN", "Infinity", a symbol) is null: BenchmarkDotNet writes a string where a statistic is undefined,
// such as the confidence interval of a case that ran a single iteration, and the exact spelling has
// differed between the cases seen.
internal sealed class UndefinedAsNullConverter : JsonConverter<double?>
{
    public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var number))
        {
            return number;
        }

        reader.Skip();

        return null;
    }

    public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
    {
        if (value is { } number && double.IsFinite(number))
        {
            writer.WriteNumberValue(number);

            return;
        }

        writer.WriteNullValue();
    }
}
