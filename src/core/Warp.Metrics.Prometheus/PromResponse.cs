using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Warp.Metrics.Prometheus;

/// <summary>The Prometheus <c>/api/v1/query</c>(<c>_range</c>) envelope.</summary>
public sealed class PromResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public PromData? Data { get; set; }
}

public sealed class PromData
{
    [JsonPropertyName("resultType")]
    public string ResultType { get; set; } = string.Empty;

    [JsonPropertyName("result")]
    public List<PromResult> Result { get; set; } = [];
}

public sealed class PromResult
{
    [JsonPropertyName("metric")]
    public Dictionary<string, string> Metric { get; set; } = new(StringComparer.Ordinal);

    // Instant query: a single [ unixSeconds, "value" ] pair.
    [JsonPropertyName("value")]
    public JsonElement[]? Value { get; set; }

    // Range query: a series of [ unixSeconds, "value" ] pairs.
    [JsonPropertyName("values")]
    public List<JsonElement[]>? Values { get; set; }
}

internal static class PromValue
{
    // The scalar of an instant result's [ts, "value"] pair, or null when absent / not a finite number
    // (Prometheus renders "NaN"/"+Inf" as strings — e.g. histogram_quantile over an empty set).
    public static double? Scalar(PromResult result)
        => result.Value is { Length: 2 } v ? Parse(v[1]) : null;

    public static double? Parse(JsonElement stringValue)
        => stringValue.ValueKind == JsonValueKind.String
            && double.TryParse(stringValue.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            && !double.IsNaN(d) && !double.IsInfinity(d)
            ? d
            : null;

    public static DateTime BucketTime(JsonElement unixSeconds)
    {
        var seconds = unixSeconds.ValueKind == JsonValueKind.Number
            ? unixSeconds.GetDouble()
            : double.Parse(unixSeconds.GetString() ?? "0", CultureInfo.InvariantCulture);

        return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)).UtcDateTime;
    }
}
