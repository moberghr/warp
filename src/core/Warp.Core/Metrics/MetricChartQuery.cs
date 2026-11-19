using System.Globalization;
using Warp.Core.Models;
using Warp.Core.Services;
using Warp.Core.Slo;

namespace Warp.Core.Metrics;

/// <summary>
/// The stored query of a chart tile (§8.34): the metric detail page's URL controls without the range, one grammar for
/// the page, a shared link and a tile. <see cref="MetricQueryKeys"/> mirrors <c>metricQueryKeys</c> in
/// <c>src/ui/src/pages/metrics/metricModel.ts</c>; <c>MetricChartQueryKeysSyncTests</c> reads that file and fails when
/// the two drift. Parsing is strict (an unknown key, a range key or a value the page would not read is an error) and the
/// result is canonical: keys in <see cref="MetricQueryKeys"/> order, filters by key, defaults left out, values
/// percent-encoded. So the seeder can match a seeded chart against a stored one by plain string equality.
/// </summary>
internal static class MetricChartQuery
{
    /// <summary>The longest stored chart query.</summary>
    public const int MaxLength = 2000;

    // An application name is bounded like everywhere else (board, tile, verdict: 200); a tag key keeps the 128 of a key.
    private const int MaxApplicationLength = 200;
    private const int MaxNameLength = 128;
    private const int MaxValueLength = 512;

    /// <summary>Every query key the metric detail page reads, in the page's order: <c>metricQueryKeys</c>.</summary>
    public static IReadOnlyList<string> MetricQueryKeys { get; } =
        ["range", "from", "to", "application", "breakdown", "filter", "compare", "percentile", "view", "numerator", "minmax", "rate"];

    /// <summary>The keys that pick the window. A tile never stores them: the board's range drives every tile.</summary>
    public static IReadOnlyList<string> RangeKeys { get; } = ["range", "from", "to"];

    /// <summary>
    /// The percentiles the metric page can draw: <c>PERCENTILES</c> in <c>metricModel.ts</c>, which reads any other value
    /// as no percentile. A tile holding another one would render without its line and lose it on the next edit.
    /// </summary>
    public static IReadOnlyList<int> Percentiles { get; } = [50, 90, 95, 99];

    /// <summary>
    /// Builds the canonical query of a seeded chart from typed parameters (<c>MetricBoardOptions.Chart</c>). The filter is
    /// the <c>MetricFilter</c> grammar (<c>k=v[,k=v]</c>); every other value is the page's own URL value. Invalid input
    /// fails <see cref="TryNormalize"/>, which the caller runs next.
    /// </summary>
    public static string Build(
        string? application,
        string? breakdown,
        string? filter,
        string? compare,
        int? percentile,
        string? numerator,
        bool minMax,
        string? rate)
    {
        var parts = new List<string>();
        Add(parts, "application", application);
        Add(parts, "breakdown", breakdown);
        if (!string.IsNullOrWhiteSpace(filter))
        {
            foreach (var clause in filter.Split(','))
            {
                var separator = clause.IndexOf('=', StringComparison.Ordinal);

                // A clause without '=' is passed through as is, so the validation reports it rather than this silently dropping it.
                Add(parts, "filter", separator < 0 ? clause : $"{clause[..separator].Trim()}:{clause[(separator + 1)..].Trim()}");
            }
        }

        Add(parts, "compare", compare);
        Add(parts, "percentile", percentile?.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(numerator))
        {
            Add(parts, "view", "ratio");
            Add(parts, "numerator", numerator);
        }

        Add(parts, "minmax", minMax ? "1" : null);
        Add(parts, "rate", rate);

        return string.Join('&', parts);
    }

    /// <summary>
    /// Parses <paramref name="query"/> (a URL query string, an optional leading <c>?</c>) and returns its canonical form, or
    /// null when it holds no control. False with a caller-facing message on an unknown or repeated key, a range key, a
    /// value the page would not read, a malformed filter, or a query longer than <see cref="MaxLength"/>.
    /// </summary>
    public static bool TryNormalize(string? query, out string? normalized, out string? error)
    {
        normalized = null;
        var text = (query ?? string.Empty).Trim();
        if (text.StartsWith('?'))
        {
            text = text[1..];
        }

        if (text.Length > MaxLength)
        {
            error = $"ChartQuery must be at most {MaxLength} characters.";

            return false;
        }

        if (!TryParsePairs(text, out var pairs, out error))
        {
            return false;
        }

        var controls = new ChartControls();
        foreach (var (key, value) in pairs)
        {
            if (!TryApply(controls, key, value, out error))
            {
                return false;
            }
        }

        if (!MetricFilter.TryCreate(controls.Filters, out _, out var filterError))
        {
            error = $"ChartQuery filter is invalid: {filterError}";

            return false;
        }

        normalized = controls.Serialize();
        if (normalized.Length > MaxLength)
        {
            error = $"ChartQuery must be at most {MaxLength} characters.";
            normalized = null;

            return false;
        }

        normalized = normalized.Length == 0 ? null : normalized;
        error = null;

        return true;
    }

    private static void Add(List<string> parts, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add($"{key}={Uri.EscapeDataString(value.Trim())}");
        }
    }

    private static bool TryParsePairs(string text, out List<(string Key, string Value)> pairs, out string? error)
    {
        pairs = [];
        foreach (var part in text.Split('&'))
        {
            if (part.Length == 0)
            {
                continue;
            }

            // Uri.UnescapeDataString never throws on .NET Core: a malformed escape is kept as typed, as URLSearchParams does.
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            var key = Decode(separator < 0 ? part : part[..separator]);
            var value = Decode(separator < 0 ? string.Empty : part[(separator + 1)..]);

            if (RangeKeys.Contains(key, StringComparer.Ordinal))
            {
                error = $"ChartQuery must not hold the range ('{key}'): the board's range drives every tile.";

                return false;
            }

            if (!MetricQueryKeys.Contains(key, StringComparer.Ordinal))
            {
                error = $"ChartQuery parameter '{Bounded(key)}' is not read by the metric page. Known parameters: {string.Join(", ", MetricQueryKeys.Except(RangeKeys, StringComparer.Ordinal))}.";

                return false;
            }

            if (value.Trim().Length == 0)
            {
                error = $"ChartQuery parameter '{key}' has no value.";

                return false;
            }

            pairs.Add((key, value.Trim()));
        }

        error = null;

        return true;
    }

    private static string Decode(string text) => Uri.UnescapeDataString(text.Replace('+', ' '));

    private static bool TryApply(ChartControls controls, string key, string value, out string? error)
    {
        error = null;
        if (!string.Equals(key, "filter", StringComparison.Ordinal) && !controls.Seen.Add(key))
        {
            error = $"ChartQuery parameter '{key}' appears more than once.";

            return false;
        }

        switch (key)
        {
            case "application":
            case "breakdown":
                var maxLength = string.Equals(key, "application", StringComparison.Ordinal) ? MaxApplicationLength : MaxNameLength;
                if (value.Length > maxLength)
                {
                    error = $"ChartQuery {key} must be at most {maxLength} characters.";

                    return false;
                }

                controls.Names[key] = value;

                return true;

            case "filter":
                return TryAddFilter(controls, value, out error);

            case "compare":
                foreach (var label in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!MetricCompareOffsets.TryParse(label, out var offset))
                    {
                        error = "ChartQuery compare must be 1d, 7d or both (comma-separated).";

                        return false;
                    }

                    controls.Compare.Add(offset.Value);
                }

                return true;

            case "percentile":
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var percentile) || !Percentiles.Contains(percentile))
                {
                    error = $"ChartQuery percentile must be one of {string.Join(", ", Percentiles)}.";

                    return false;
                }

                controls.Percentile = percentile;

                return true;

            case "view":
                if (value is not ("values" or "ratio"))
                {
                    error = "ChartQuery view must be values or ratio.";

                    return false;
                }

                controls.Ratio = string.Equals(value, "ratio", StringComparison.Ordinal);

                return true;

            case "numerator":
                if (value.Length > MaxValueLength || !MetricValuePattern.TryParse(value, out _))
                {
                    error = $"ChartQuery numerator must be a value or a prefix* pattern of at most {MaxValueLength} characters.";

                    return false;
                }

                controls.Numerator = value;

                return true;

            case "minmax":
                if (value is not ("1" or "true"))
                {
                    error = "ChartQuery minmax must be 1.";

                    return false;
                }

                controls.MinMax = true;

                return true;

            default:
                // "rate": the only key left, MetricQueryKeys minus the range keys is exhaustive above.
                if (value is not ("bucket" or "minute" or "second"))
                {
                    error = "ChartQuery rate must be bucket, minute or second.";

                    return false;
                }

                controls.Rate = string.Equals(value, "bucket", StringComparison.Ordinal) ? null : value;

                return true;
        }
    }

    // The series API's filter syntax (`key:value`), with its limits: one value per key, a trailing `*` only.
    private static bool TryAddFilter(ChartControls controls, string value, out string? error)
    {
        var separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == value.Length - 1)
        {
            error = "ChartQuery filter must be key:value.";

            return false;
        }

        var key = value[..separator].Trim();
        var pattern = value[(separator + 1)..].Trim();
        if (key.Length == 0 || pattern.Length == 0 || key.Length > MaxNameLength || pattern.Length > MaxValueLength)
        {
            error = $"ChartQuery filter needs a key of at most {MaxNameLength} characters and a value of at most {MaxValueLength}.";

            return false;
        }

        if (!controls.Filters.TryAdd(key, pattern))
        {
            error = $"ChartQuery filter key '{Bounded(key)}' appears more than once.";

            return false;
        }

        if (controls.Filters.Count > IMetricQueryService.MaxFilters)
        {
            error = $"ChartQuery holds at most {IMetricQueryService.MaxFilters} filters.";

            return false;
        }

        error = null;

        return true;
    }

    // An echoed key is caller input: bounded so the message stays short.
    private static string Bounded(string key) => key.Length > 64 ? key[..64] + "..." : key;

    private sealed class ChartControls
    {
        public HashSet<string> Seen { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Names { get; } = new(StringComparer.Ordinal);

        public SortedDictionary<string, string> Filters { get; } = new(StringComparer.Ordinal);

        public SortedSet<MetricCompareOffset> Compare { get; } = [];

        public int? Percentile { get; set; }

        public bool Ratio { get; set; }

        public string? Numerator { get; set; }

        public bool MinMax { get; set; }

        public string? Rate { get; set; }

        // Keys in MetricQueryKeys order, defaults left out: the page writes the same set, and two equal tiles store one string.
        public string Serialize()
        {
            var parts = new List<string>();
            Add(parts, "application", Names.GetValueOrDefault("application"));
            Add(parts, "breakdown", Names.GetValueOrDefault("breakdown"));
            foreach (var (key, value) in Filters)
            {
                Add(parts, "filter", $"{key}:{value}");
            }

            Add(parts, "compare", Compare.Count == 0 ? null : string.Join(',', Compare.Select(MetricCompareOffsets.Label)));
            Add(parts, "percentile", Percentile?.ToString(CultureInfo.InvariantCulture));
            Add(parts, "view", Ratio ? "ratio" : null);
            Add(parts, "numerator", Numerator);
            Add(parts, "minmax", MinMax ? "1" : null);
            Add(parts, "rate", Rate);

            return string.Join('&', parts);
        }
    }
}
