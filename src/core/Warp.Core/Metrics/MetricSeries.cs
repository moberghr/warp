using System.Buffers;
using System.Globalization;
using Warp.Core.Enums;

namespace Warp.Core.Metrics;

/// <summary>
/// One series of a collected instrument: (instrument name, application, canonical tag set), plus the application
/// version when the host opted in with <c>AddMetrics(m =&gt; m.ByVersion())</c>. The canonical tag
/// set is <c>k1=v1,k2=v2</c> — projected through the instrument's <c>KeepTags</c> allowlist, keys sanitized and
/// sorted in ordinal order, capped at <c>MaxTagsPerInstrument</c> keys and at <see cref="MaxTagsLength"/> characters
/// (the <c>Tags</c> column) — or <see cref="NoTags"/> when no tag survives. A series key is therefore never longer than
/// the column, so storage never has to cut it and two distinct series can never merge there.
/// Application and version come from <c>WarpConfiguration.ApplicationName</c> / <c>ApplicationVersion</c>, never from a
/// tag. Both are per-process constants, so they never multiply the series one process interns; they matter where rows
/// of several processes meet (the rollup's key, the read's breakdown).
/// <para>
/// Series instances are interned per instrument by <see cref="MetricInstrumentCardinality"/>, so an
/// already-seen series resolves without allocating: the tag set is written into a pooled buffer and looked up
/// by span.
/// </para>
/// </summary>
internal sealed class MetricSeries : IEquatable<MetricSeries>
{
    public const string NoTags = "-";

    public const string None = "{none}";

    public const string Other = "{other}";

    public const int MaxValueLength = 64;

    /// <summary>The longest canonical tag set: the <c>Tags</c> column of <c>metric_point</c>.</summary>
    public const int MaxTagsLength = 512;

    // key (≤ 64) + '=' + value (≤ 64) + ','
    private const int MaxTagLength = (MaxValueLength * 2) + 2;

    private readonly int _hashCode;

    public MetricSeries(MetricInstrumentInfo instrument, string? application, string tags, string? version = null)
    {
        Instrument = instrument;
        Application = application;
        Tags = tags;
        Version = version;
        _hashCode = HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(instrument.Name),
            application is null ? 0 : StringComparer.Ordinal.GetHashCode(application),
            StringComparer.Ordinal.GetHashCode(tags),
            version is null ? 0 : StringComparer.Ordinal.GetHashCode(version));
    }

    public MetricInstrumentInfo Instrument { get; }

    public string? Application { get; }

    public string Tags { get; }

    /// <summary>The application version, only when the host opted in with <c>ByVersion()</c>; null otherwise.</summary>
    public string? Version { get; }

    /// <summary>
    /// Resolves the series for one measurement's tags. Keys are sanitized first, and sorting, de-duplication and
    /// value mapping all use the sanitized key. Key, value and series caps are applied through
    /// <paramref name="cardinality"/>; keys beyond <paramref name="maxTags"/> (in ordinal order) are dropped
    /// and reported. Once the next <c>k=v</c> would push the tag set past <see cref="MaxTagsLength"/>, it and every
    /// later key in canonical order are dropped and reported as <c>tags_truncated</c> (keys only, never values).
    /// </summary>
    public static MetricSeries Resolve(
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        MetricInstrumentCardinality cardinality,
        IReadOnlySet<string>? allowlist,
        int maxTags)
    {
        if (tags.IsEmpty)
        {
            return cardinality.AdmitSeries(NoTags);
        }

        var kept = ArrayPool<int>.Shared.Rent(tags.Length);
        var keys = ArrayPool<MetricTagKey?>.Shared.Rent(tags.Length);
        var buffer = ArrayPool<char>.Shared.Rent(maxTags * MaxTagLength);
        try
        {
            var keptCount = Project(tags, allowlist, cardinality, kept, keys);
            SortByKey(kept, keys, keptCount);

            Span<char> value = stackalloc char[MaxValueLength];
            var written = 0;
            var emitted = 0;
            var truncated = false;
            MetricTagKey? previousKey = null;

            for (var i = 0; i < keptCount; i++)
            {
                // Keys are interned per instrument, so equal sanitized keys are the same instance.
                var key = keys[i]!;
                if (ReferenceEquals(previousKey, key))
                {
                    continue;
                }

                previousKey = key;

                if (emitted == maxTags)
                {
                    cardinality.ReportTagCount(key);

                    continue;
                }

                if (truncated)
                {
                    cardinality.ReportTagsTruncated(key);

                    continue;
                }

                var length = FormatValue(tags[kept[i]].Value, value);
                var mapped = cardinality.MapValue(key, value[..length]);

                var separator = emitted > 0 ? 1 : 0;
                if (written + separator + key.Name.Length + 1 + mapped.Length > MaxTagsLength)
                {
                    truncated = true;
                    cardinality.ReportTagsTruncated(key);

                    continue;
                }

                if (separator > 0)
                {
                    buffer[written++] = ',';
                }

                key.Name.AsSpan().CopyTo(buffer.AsSpan(written));
                written += key.Name.Length;
                buffer[written++] = '=';
                mapped.AsSpan().CopyTo(buffer.AsSpan(written));
                written += mapped.Length;
                emitted++;
            }

            return emitted == 0
                ? cardinality.AdmitSeries(NoTags)
                : cardinality.AdmitSeries(buffer.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
            ArrayPool<MetricTagKey?>.Shared.Return(keys, clearArray: true);
            ArrayPool<int>.Shared.Return(kept);
        }
    }

    /// <summary>
    /// Formats a tag value invariantly into <paramref name="destination"/> (at least <see cref="MaxValueLength"/>
    /// chars) and sanitizes it; null or empty becomes <see cref="None"/>. Returns the written length.
    /// </summary>
    public static int FormatValue(object? value, Span<char> destination)
    {
        var written = value switch
        {
            null => 0,
            string x => Sanitize(x, destination),
            bool x => Sanitize(x ? bool.TrueString : bool.FalseString, destination),
            ISpanFormattable x => FormatSpanFormattable(x, destination),
            IFormattable x => Sanitize(x.ToString(null, CultureInfo.InvariantCulture), destination),
            _ => Sanitize(value.ToString(), destination),
        };

        return written == 0 ? WriteNone(destination) : written;
    }

    /// <summary>
    /// Trims, strips control characters, replaces <c>,</c> and <c>=</c> with <c>-</c>, and truncates to
    /// <see cref="MaxValueLength"/>. Returns the written length (0 for null, empty or all-whitespace input).
    /// </summary>
    public static int Sanitize(ReadOnlySpan<char> source, Span<char> destination)
    {
        source = source.Trim();
        var written = 0;

        foreach (var c in source)
        {
            if (written == MaxValueLength)
            {
                break;
            }

            if (char.IsControl(c))
            {
                continue;
            }

            destination[written++] = c is ',' or '=' ? '-' : c;
        }

        // Never leave half a surrogate pair at the truncation point.
        if (written == MaxValueLength && char.IsHighSurrogate(destination[written - 1]))
        {
            written--;
        }

        while (written > 0 && char.IsWhiteSpace(destination[written - 1]))
        {
            written--;
        }

        return written;
    }

    public bool Equals(MetricSeries? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return other is not null
            && _hashCode == other._hashCode
            && string.Equals(Instrument.Name, other.Instrument.Name, StringComparison.Ordinal)
            && string.Equals(Application, other.Application, StringComparison.Ordinal)
            && string.Equals(Tags, other.Tags, StringComparison.Ordinal)
            && string.Equals(Version, other.Version, StringComparison.Ordinal);
    }

    public override bool Equals(object? obj) => obj is MetricSeries other && Equals(other);

    public override int GetHashCode() => _hashCode;

    public override string ToString() => $"{Instrument.Name}[{Tags}]";

    // Keeps the allowlisted, non-blank keys, each sanitized and admitted under the instrument's key cap. A key
    // over the cap is dropped here, so it never reaches the series or a log.
    private static int Project(
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        IReadOnlySet<string>? allowlist,
        MetricInstrumentCardinality cardinality,
        int[] kept,
        MetricTagKey?[] keys)
    {
        Span<char> sanitized = stackalloc char[MaxValueLength];
        var count = 0;

        for (var i = 0; i < tags.Length; i++)
        {
            var key = tags[i].Key;
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (allowlist is not null && !allowlist.Contains(key))
            {
                continue;
            }

            var admitted = cardinality.AdmitKey(sanitized[..WriteOrNone(key, sanitized)]);
            if (admitted is null)
            {
                continue;
            }

            kept[count] = i;
            keys[count] = admitted;
            count++;
        }

        return count;
    }

    // Insertion sort over the sanitized keys, carrying the tag indices along: tag counts are tiny, and it
    // allocates nothing.
    private static void SortByKey(int[] kept, MetricTagKey?[] keys, int count)
    {
        for (var i = 1; i < count; i++)
        {
            var currentIndex = kept[i];
            var currentKey = keys[i]!;
            var j = i - 1;
            while (j >= 0 && string.CompareOrdinal(keys[j]!.Name, currentKey.Name) > 0)
            {
                kept[j + 1] = kept[j];
                keys[j + 1] = keys[j];
                j--;
            }

            kept[j + 1] = currentIndex;
            keys[j + 1] = currentKey;
        }
    }

    private static int WriteOrNone(string key, Span<char> destination)
    {
        var written = Sanitize(key, destination);

        return written == 0 ? WriteNone(destination) : written;
    }

    private static int WriteNone(Span<char> destination)
    {
        None.AsSpan().CopyTo(destination);

        return None.Length;
    }

    private static int FormatSpanFormattable(ISpanFormattable value, Span<char> destination)
    {
        Span<char> scratch = stackalloc char[128];

        return value.TryFormat(scratch, out var length, default, CultureInfo.InvariantCulture)
            ? Sanitize(scratch[..length], destination)
            : Sanitize(value.ToString(null, CultureInfo.InvariantCulture), destination);
    }
}

/// <summary>
/// What the aggregator and storage need to know about a collected instrument: identity, kind, unit and (for a
/// histogram) its resolved bucket bounds.
/// </summary>
internal sealed class MetricInstrumentInfo
{
    public MetricInstrumentInfo(string name, string meterName, MetricKind kind, string? unit, string? description, double[]? bounds, bool isCumulative)
    {
        Name = name;
        MeterName = meterName;
        Kind = kind;
        Unit = unit;
        Description = description;
        Bounds = bounds;
        IsCumulative = isCumulative;
    }

    public string Name { get; }

    public string MeterName { get; }

    public MetricKind Kind { get; }

    public string? Unit { get; }

    public string? Description { get; }

    /// <summary>Histogram bounds; null for every other kind.</summary>
    public double[]? Bounds { get; }

    /// <summary>True for <c>ObservableCounter</c> / <c>ObservableUpDownCounter</c>, which report cumulative values.</summary>
    public bool IsCumulative { get; }
}
