using System.Text.Json;
using System.Text.Json.Serialization;

namespace Warp.Core.Handlers;

public static class MetadataSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters =
        {
            new NativeObjectConverter(),

            // Write enums as their declared name and accept the same on read. Makes the
            // metadata column human-readable and makes API responses ship `"Skip"` instead
            // of `1` so the dashboard and any external consumer render the enum directly.
            // Read-side parse delegates to MetadataConvert.To (the dict carries the raw
            // string from NativeObjectConverter; the generated property getter converts).
            new JsonStringEnumConverter(),
        },
    };

    public static Dictionary<string, object> Deserialize(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<Dictionary<string, object>>(json, Options) ?? [];
    }

    public static string? Serialize(Dictionary<string, object>? metadata)
    {
        if (metadata == null || metadata.Count == 0)
        {
            return null;
        }

        return JsonSerializer.Serialize(metadata, Options);
    }

    private sealed class NativeObjectConverter : JsonConverter<object>
    {
        public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.TokenType switch
            {
                JsonTokenType.True => true,
                JsonTokenType.False => false,
                JsonTokenType.Number when reader.TryGetInt64(out var l) => l,
                JsonTokenType.Number => reader.GetDouble(),
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.StartArray => JsonSerializer.Deserialize<List<object>>(ref reader, options),
                JsonTokenType.StartObject => JsonSerializer.Deserialize<Dictionary<string, object>>(ref reader, options),
                _ => JsonDocument.ParseValue(ref reader).RootElement.Clone(),
            };
        }

        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value, value.GetType(), options);
        }
    }
}
