namespace Warp.Core.Slo;

// Grammar: k=v[,k=v...]; a value may end in '*' (prefix match). An empty filter matches everything. An absent tag
// reads as "{none}" and so only matches k={none} (never a prefix pattern, not even a bare '*'). Values compare
// ordinally. This is the ONE matcher: the SLO evaluator and the dashboard series read (MetricQueryService) both use
// it, so a filter means the same thing on the metrics page and in an objective.
internal sealed class MetricFilter
{
    public const string NoneValue = Metrics.MetricSeries.None;

    private readonly KeyValuePair<string, MetricValuePattern>[] _clauses;

    private MetricFilter(KeyValuePair<string, MetricValuePattern>[] clauses)
    {
        _clauses = clauses;
    }

    public static MetricFilter Empty { get; } = new([]);

    public bool IsEmpty => _clauses.Length == 0;

    public static bool TryParse(string? text, out MetricFilter filter, out string? error)
    {
        filter = Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = null;

            return true;
        }

        var clauses = new List<KeyValuePair<string, MetricValuePattern>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in text.Split(','))
        {
            var pair = raw.Trim();
            if (pair.Length == 0)
            {
                error = "Filter contains an empty entry.";

                return false;
            }

            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0)
            {
                error = $"Filter entry '{pair}' is missing '='.";

                return false;
            }

            var key = pair[..separator].Trim();
            var value = pair[(separator + 1)..].Trim();

            if (key.Length == 0)
            {
                error = $"Filter entry '{pair}' has an empty key.";

                return false;
            }

            if (key.Contains('*', StringComparison.Ordinal))
            {
                error = $"Filter key '{key}' must not contain '*'.";

                return false;
            }

            if (!MetricValuePattern.TryParse(value, out var pattern))
            {
                error = $"Filter value '{value}' may only use '*' as its last character.";

                return false;
            }

            if (!seen.Add(key))
            {
                error = $"Filter key '{key}' appears more than once.";

                return false;
            }

            clauses.Add(new KeyValuePair<string, MetricValuePattern>(key, pattern));
        }

        filter = new MetricFilter([.. clauses]);
        error = null;

        return true;
    }

    // The series read's filter: one (key, value) per key, already split by the endpoint. Same checks as TryParse,
    // minus the duplicate-key one a dictionary cannot carry.
    public static bool TryCreate(IReadOnlyDictionary<string, string> pairs, out MetricFilter filter, out string? error)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        filter = Empty;

        var clauses = new List<KeyValuePair<string, MetricValuePattern>>();
        foreach (var (key, value) in pairs)
        {
            if (string.IsNullOrEmpty(key) || key.Contains('*', StringComparison.Ordinal))
            {
                error = $"Filter key '{key}' must be non-empty and must not contain '*'.";

                return false;
            }

            if (!MetricValuePattern.TryParse(value ?? string.Empty, out var pattern))
            {
                error = $"Filter value '{value}' may only use '*' as its last character.";

                return false;
            }

            clauses.Add(new KeyValuePair<string, MetricValuePattern>(key, pattern));
        }

        filter = new MetricFilter([.. clauses]);
        error = null;

        return true;
    }

    public bool Matches(IReadOnlyDictionary<string, string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        foreach (var (key, pattern) in _clauses)
        {
            var actual = tags.TryGetValue(key, out var value) ? value : null;
            if (!pattern.Matches(actual))
            {
                return false;
            }
        }

        return true;
    }

    // True when some tag set could match both filters: every key both constrain has overlapping patterns, and a key only
    // one side constrains narrows nothing on the other. An empty filter overlaps everything. "{none}" (the absent tag) is
    // never matched by a prefix pattern, so it overlaps only itself. The annotation-to-tile rule (#333).
    public bool Overlaps(MetricFilter other)
    {
        ArgumentNullException.ThrowIfNull(other);

        foreach (var (key, pattern) in _clauses)
        {
            var shared = Array.FindIndex(other._clauses, x => string.Equals(x.Key, key, StringComparison.Ordinal));
            if (shared < 0)
            {
                continue;
            }

            var theirs = other._clauses[shared].Value;
            var absent = IsAbsent(pattern) || IsAbsent(theirs);
            if (absent ? pattern != theirs : !pattern.Overlaps(theirs))
            {
                return false;
            }
        }

        return true;
    }

    // "k1=v1,k2=v2" or "-" (the stored canonical form): the query service's parser, so the two never drift.
    public static Dictionary<string, string> ParseCanonicalTags(string? canonical) => Services.MetricSeriesMath.ParseTags(canonical);

    private static bool IsAbsent(MetricValuePattern pattern)
        => !pattern.IsPrefix && string.Equals(pattern.Text, NoneValue, StringComparison.Ordinal);
}

// One value, optionally ending in '*'. A null actual value (tag absent) is "{none}": only an exact "{none}" matches.
// A bare '*' is a prefix of "" and so matches every PRESENT value, never an absent tag.
internal readonly record struct MetricValuePattern(string Text, bool IsPrefix)
{
    public static bool TryParse(string value, out MetricValuePattern pattern)
    {
        var star = value.IndexOf('*', StringComparison.Ordinal);
        if (star >= 0 && star != value.Length - 1)
        {
            pattern = default;

            return false;
        }

        pattern = star >= 0 ? new MetricValuePattern(value[..^1], true) : new MetricValuePattern(value, false);

        return true;
    }

    public bool Matches(string? actual)
    {
        if (actual is null)
        {
            return !IsPrefix && string.Equals(Text, MetricFilter.NoneValue, StringComparison.Ordinal);
        }

        return IsPrefix
            ? actual.StartsWith(Text, StringComparison.Ordinal)
            : string.Equals(actual, Text, StringComparison.Ordinal);
    }

    // True when some value could match both patterns: equal literals, a literal under the other's prefix, one prefix
    // extending the other. A bare '*' (prefix "") therefore overlaps everything.
    public bool Overlaps(MetricValuePattern other)
    {
        if (!IsPrefix && !other.IsPrefix)
        {
            return string.Equals(Text, other.Text, StringComparison.Ordinal);
        }

        if (IsPrefix && other.IsPrefix)
        {
            return Text.StartsWith(other.Text, StringComparison.Ordinal) || other.Text.StartsWith(Text, StringComparison.Ordinal);
        }

        var (literal, prefix) = IsPrefix ? (other.Text, Text) : (Text, other.Text);

        return literal.StartsWith(prefix, StringComparison.Ordinal);
    }
}

// CSV of values (GoodValues/BadValues), each optionally ending in '*'.
internal sealed class MetricValueList
{
    private readonly MetricValuePattern[] _patterns;

    private MetricValueList(MetricValuePattern[] patterns)
    {
        _patterns = patterns;
    }

    public IReadOnlyList<MetricValuePattern> Patterns => _patterns;

    public static bool TryParse(string? csv, out MetricValueList list, out string? error)
    {
        list = new MetricValueList([]);

        if (string.IsNullOrWhiteSpace(csv))
        {
            error = null;

            return true;
        }

        var patterns = new List<MetricValuePattern>();
        foreach (var raw in csv.Split(','))
        {
            var value = raw.Trim();
            if (value.Length == 0)
            {
                error = "Value list contains an empty entry.";

                return false;
            }

            if (!MetricValuePattern.TryParse(value, out var pattern))
            {
                error = $"Value '{value}' may only use '*' as its last character.";

                return false;
            }

            patterns.Add(pattern);
        }

        list = new MetricValueList([.. patterns]);
        error = null;

        return true;
    }

    public bool Matches(string? actual)
    {
        return _patterns.Any(x => x.Matches(actual));
    }

    // Spec §E: GoodValues and BadValues must not overlap, so no event can count as both good and bad. Any pair of
    // patterns that could match one value is an overlap ("500" vs "5*", "5*" vs "50*", "*" vs anything).
    public bool OverlapsWith(MetricValueList other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return _patterns.Any(x => other._patterns.Any(x.Overlaps));
    }
}
