using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Warp.Core.Enums;
using Warp.Core.Logging;

namespace Warp.Core.Metrics;

/// <summary>
/// Bounds what collected host instruments can do to memory and storage (the §8.27 / §8.19 cardinality shape),
/// in memory and per process:
/// <list type="bullet">
/// <item>at most <c>MaxValuesPerTag</c> distinct values per (instrument, tag key) — later new values become
/// <c>{other}</c>;</item>
/// <item>at most <c>2 × MaxTagsPerInstrument</c> distinct tag keys tracked per instrument — later new keys are
/// dropped from the series, never named in a log (a dynamic key may embed a value);</item>
/// <item>at most <c>MaxSeriesPerInstrument</c> series per instrument — later new tag sets become
/// <c>{other}</c>;</item>
/// <item>a canonical tag set of at most 512 characters — trailing tags that would not fit are dropped
/// (<c>tags_truncated</c>);</item>
/// <item>instrument names of at most 128 characters (<c>name_too_long</c>) and histogram advice that fits the
/// <c>Bounds</c> column (<c>bounds</c>, falling back to the unit default) — both checked at publication;</item>
/// <item>at most <c>MaxInstruments</c> collected instruments — later instruments are never enabled and are
/// reported under the constant instrument <c>{other}</c>, so a refused name leaves no state behind;</item>
/// <item>one publisher per instrument name — the first (meter, kind, bounds) wins, and a later conflicting
/// publication is never enabled.</item>
/// </list>
/// Every collapse is reported three ways and <b>never includes a tag value</b> (§1.2): a Warning once per
/// (instrument, tag key, reason), the always-on <c>warp.metrics.collapsed{instrument, reason}</c> meter, and
/// <see cref="Collapsed"/>, which storage reads to badge the instrument registry. Admission takes a small lock
/// around count-then-add, so the caps hold exactly; only the first sighting of a value or series contends.
/// </summary>
internal sealed class MetricCardinalityGuard
{
    public const string ReasonTagValues = "tag_values";

    public const string ReasonSeries = "series";

    public const string ReasonTagCount = "tag_count";

    public const string ReasonInstrumentCap = "instrument_cap";

    public const string ReasonNameConflict = "name_conflict";

    public const string ReasonError = "error";

    /// <summary>A canonical tag set that would not fit the 512-character <c>Tags</c> column lost its trailing tags.</summary>
    public const string ReasonTagsTruncated = "tags_truncated";

    /// <summary>A histogram's advised bounds could not be stored as given; the unit default ladder was used.</summary>
    public const string ReasonBounds = "bounds";

    /// <summary>An instrument name longer than the 128-character <c>Instrument</c> column; it is never collected.</summary>
    public const string ReasonNameTooLong = "name_too_long";

    /// <summary>
    /// A drained aggregate was discarded because keeping it would have pushed the in-memory pending set past
    /// <see cref="WarpMetricsOptions.MaxPendingBuckets"/> (a write failing for long enough).
    /// </summary>
    public const string ReasonDropped = "dropped";

    /// <summary>A measurement (or an observable counter's delta) was NaN or infinite and was not recorded.</summary>
    public const string ReasonNonFinite = "non_finite";

    /// <summary>A selected instrument has a type Warp does not aggregate; it is never enabled.</summary>
    public const string ReasonUnsupported = "unsupported";

    /// <summary>The instrument a failing observable callback is reported under: the callback is not attributable to one instrument.</summary>
    public const string ObservableInstrument = "{observable}";

    private const int MaxObservableFailureTypes = 32;

    private readonly int _maxValuesPerTag;
    private readonly int _maxSeriesPerInstrument;
    private readonly int _maxInstruments;
    private readonly int _maxTagKeys;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MetricCardinalityGuard> _logger;
    private readonly ConcurrentDictionary<string, InstrumentIdentity> _instruments = new(StringComparer.Ordinal);
    private readonly Lock _instrumentGate = new();
    private readonly ConcurrentDictionary<(string Instrument, string TagKey, string Reason), byte> _warned = new();
    private readonly ConcurrentDictionary<string, CollapseState> _collapses = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _observableFailureTypes = new(StringComparer.Ordinal);
    private readonly Lock _observableFailureGate = new();
    private int _instrumentCapWarned;

    public MetricCardinalityGuard(WarpMetricsOptions options, TimeProvider timeProvider, ILogger<MetricCardinalityGuard> logger)
    {
        _maxValuesPerTag = options.MaxValuesPerTag;
        _maxSeriesPerInstrument = options.MaxSeriesPerInstrument;
        _maxInstruments = options.MaxInstruments;
        _maxTagKeys = options.MaxTagsPerInstrument * 2;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Instrument names admitted so far. Diagnostics and tests.</summary>
    public int InstrumentCount => _instruments.Count;

    /// <summary>Distinct (instrument, tag key, reason) warnings logged so far. Diagnostics and tests.</summary>
    public int WarningCount => _warned.Count;

    /// <summary>
    /// Admits an instrument by name. The first publisher of a name wins: a later publication with the same
    /// (meter, kind, bounds) — a disposed and re-created meter — is admitted again; one that differs in any of
    /// them is refused and reported as <see cref="ReasonNameConflict"/>. A new name over the <c>MaxInstruments</c>
    /// cap is refused and reported under <c>{other}</c>.
    /// </summary>
    public bool TryAdmitInstrument(MetricInstrumentInfo instrument)
    {
        if (!_instruments.TryGetValue(instrument.Name, out var first))
        {
            lock (_instrumentGate)
            {
                if (!_instruments.TryGetValue(instrument.Name, out first) && _instruments.Count < _maxInstruments)
                {
                    _instruments.TryAdd(instrument.Name, new InstrumentIdentity(instrument.MeterName, instrument.Kind, instrument.Bounds));

                    return true;
                }
            }
        }

        if (first is null)
        {
            ReportInstrumentCap();

            return false;
        }

        if (first.Matches(instrument))
        {
            return true;
        }

        ReportNameConflict(instrument.Name, first.MeterName, instrument.MeterName);

        return false;
    }

    /// <summary>The per-instrument key, value and series caps for one collected instrument.</summary>
    public MetricInstrumentCardinality CreateInstrument(MetricInstrumentInfo instrument, string? application, string? version = null)
        => new(this, instrument, application, _maxValuesPerTag, _maxSeriesPerInstrument, _maxTagKeys, version);

    /// <summary>Reports a collapse. <paramref name="tagKey"/> is an admitted key name, never a value.</summary>
    public void Report(string instrument, string? tagKey, string reason)
    {
        Count(instrument, reason);

        _collapses
            .GetOrAdd(instrument, static _ => new CollapseState())
            .Touch(_timeProvider.GetUtcNow().UtcDateTime, tagKey, reason);

        if (_warned.TryAdd((instrument, tagKey ?? string.Empty, reason), 0))
        {
            _logger.LogWarning(
                "Collected metric instrument {Instrument} collapsed a measurement ({Reason}) on tag key {TagKey}; further occurrences are counted on warp.metrics.collapsed without another warning.",
                instrument,
                reason,
                tagKey ?? "(none)");
        }
    }

    /// <summary>Counts a collapse on <c>warp.metrics.collapsed</c> only: no state, no clock read, no log.</summary>
    public static void Count(string instrument, string reason)
        => WarpTelemetry.MetricsCollapsed.Add(
            1,
            new KeyValuePair<string, object?>("instrument", instrument),
            new KeyValuePair<string, object?>("reason", reason));

    /// <summary>
    /// Reports a measurement the collector could not record. Logs the exception TYPE only — a message could
    /// carry a tag value.
    /// </summary>
    public void ReportError(string instrument, Exception exception)
    {
        Count(instrument, ReasonError);

        _collapses
            .GetOrAdd(instrument, static _ => new CollapseState())
            .Touch(_timeProvider.GetUtcNow().UtcDateTime, null, ReasonError);

        if (_warned.TryAdd((instrument, string.Empty, ReasonError), 0))
        {
            _logger.LogWarning(
                "Collected metric instrument {Instrument} failed to record a measurement ({ExceptionType}); the measurement was dropped and the host code was not affected. Further failures are counted on warp.metrics.collapsed without another warning.",
                instrument,
                exception.GetType().FullName);
        }
    }

    /// <summary>
    /// Reports one failure of a host observable callback. The callback cannot be attributed to an instrument,
    /// so it is counted under <see cref="ObservableInstrument"/> and logged once per exception TYPE, never its
    /// message.
    /// </summary>
    public void ReportObservableFailure(Exception exception)
    {
        Count(ObservableInstrument, ReasonError);

        _collapses
            .GetOrAdd(ObservableInstrument, static _ => new CollapseState())
            .Touch(_timeProvider.GetUtcNow().UtcDateTime, null, ReasonError);

        var type = exception.GetType().FullName ?? exception.GetType().Name;
        if (!TryTrackObservableFailureType(type))
        {
            return;
        }

        _logger.LogWarning(
            "An observable instrument callback of a collected meter threw ({ExceptionType}) while Warp read collected metrics; its measurements were skipped this time. Further failures of this type are counted on warp.metrics.collapsed without another warning.",
            type);
    }

    /// <summary>A snapshot of every instrument that has collapsed: its collapsed tag keys, reasons and last collapse time.</summary>
    public IReadOnlyList<MetricCollapse> Collapsed()
        => [.. _collapses.Select(x => x.Value.Snapshot(x.Key))];

    private void ReportInstrumentCap()
    {
        Count(MetricSeries.Other, ReasonInstrumentCap);

        _collapses
            .GetOrAdd(MetricSeries.Other, static _ => new CollapseState())
            .Touch(_timeProvider.GetUtcNow().UtcDateTime, null, ReasonInstrumentCap);

        if (Interlocked.Exchange(ref _instrumentCapWarned, 1) == 0)
        {
            _logger.LogWarning(
                "More than {MaxInstruments} collected metric instruments were published ({Reason}); further instruments are not collected and are counted on warp.metrics.collapsed under instrument {OtherInstrument} without another warning.",
                _maxInstruments,
                ReasonInstrumentCap,
                MetricSeries.Other);
        }
    }

    private void ReportNameConflict(string instrument, string firstMeter, string meter)
    {
        Count(instrument, ReasonNameConflict);

        _collapses
            .GetOrAdd(instrument, static _ => new CollapseState())
            .Touch(_timeProvider.GetUtcNow().UtcDateTime, null, ReasonNameConflict);

        if (_warned.TryAdd((instrument, string.Empty, ReasonNameConflict), 0))
        {
            _logger.LogWarning(
                "Collected metric instrument {Instrument} was published by meter {Meter} with a different meter, kind or bucket bounds than its first publisher, meter {FirstMeter} ({Reason}); only the first publisher's instrument is collected.",
                instrument,
                meter,
                firstMeter,
                ReasonNameConflict);
        }
    }

    private bool TryTrackObservableFailureType(string type)
    {
        if (_observableFailureTypes.ContainsKey(type))
        {
            return false;
        }

        lock (_observableFailureGate)
        {
            return _observableFailureTypes.Count < MaxObservableFailureTypes && _observableFailureTypes.TryAdd(type, 0);
        }
    }

    private sealed class InstrumentIdentity
    {
        private readonly double[]? _bounds;

        public InstrumentIdentity(string meterName, MetricKind kind, double[]? bounds)
        {
            MeterName = meterName;
            Kind = kind;
            _bounds = bounds;
        }

        public string MeterName { get; }

        public MetricKind Kind { get; }

        public bool Matches(MetricInstrumentInfo instrument)
            => string.Equals(MeterName, instrument.MeterName, StringComparison.Ordinal)
                && Kind == instrument.Kind
                && BoundsEqual(_bounds, instrument.Bounds);

        private static bool BoundsEqual(double[]? first, double[]? second)
        {
            if (first is null || second is null)
            {
                return first is null && second is null;
            }

            return first.AsSpan().SequenceEqual(second);
        }
    }

    private sealed class CollapseState
    {
        private readonly ConcurrentDictionary<string, byte> _tagKeys = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _reasons = new(StringComparer.Ordinal);
        private long _lastTicks;

        public void Touch(DateTime utc, string? tagKey, string reason)
        {
            Interlocked.Exchange(ref _lastTicks, utc.Ticks);
            _reasons.TryAdd(reason, 0);

            if (tagKey is not null)
            {
                _tagKeys.TryAdd(tagKey, 0);
            }
        }

        public MetricCollapse Snapshot(string instrument)
            => new(
                instrument,
                [.. _tagKeys.Keys.Order(StringComparer.Ordinal)],
                [.. _reasons.Keys.Order(StringComparer.Ordinal)],
                new DateTime(Interlocked.Read(ref _lastTicks), DateTimeKind.Utc));
    }
}

/// <summary>
/// The key, value and series caps of one collected instrument. Interns tag keys, tag values and series so an
/// already-seen key, value or series resolves by span lookup, without allocating.
/// </summary>
internal sealed class MetricInstrumentCardinality
{
    private readonly MetricCardinalityGuard _guard;
    private readonly MetricInstrumentInfo _instrument;
    private readonly string? _application;
    private readonly string? _version;
    private readonly int _maxValuesPerTag;
    private readonly int _maxSeries;
    private readonly int _maxTagKeys;
    private readonly ConcurrentDictionary<string, MetricTagKey> _keys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MetricTagKey>.AlternateLookup<ReadOnlySpan<char>> _keyLookup;
    private readonly Lock _keyGate = new();
    private readonly ConcurrentDictionary<string, MetricSeries> _series = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MetricSeries>.AlternateLookup<ReadOnlySpan<char>> _seriesLookup;
    private readonly Lock _seriesGate = new();
    private readonly MetricSeries _otherSeries;
    private int _keyOverflowReported;

    public MetricInstrumentCardinality(
        MetricCardinalityGuard guard,
        MetricInstrumentInfo instrument,
        string? application,
        int maxValuesPerTag,
        int maxSeries,
        int maxTagKeys,
        string? version = null)
    {
        _guard = guard;
        _instrument = instrument;
        _application = application;
        _version = version;
        _maxValuesPerTag = maxValuesPerTag;
        _maxSeries = maxSeries;
        _maxTagKeys = maxTagKeys;
        _keyLookup = _keys.GetAlternateLookup<ReadOnlySpan<char>>();
        _seriesLookup = _series.GetAlternateLookup<ReadOnlySpan<char>>();
        _otherSeries = new MetricSeries(instrument, application, MetricSeries.Other, version);
    }

    /// <summary>Distinct series admitted so far (excluding the <c>{other}</c> series).</summary>
    public int SeriesCount => _series.Count;

    /// <summary>Distinct tag keys tracked so far; never more than <c>2 × MaxTagsPerInstrument</c>.</summary>
    public int TagKeyCount => _keys.Count;

    /// <summary>
    /// Test seam: invoked on the slow path of <see cref="MapValue(MetricTagKey, ReadOnlySpan{char})"/>, after the
    /// lock-free lookup missed and before the admission lock is taken. Null in production.
    /// </summary>
    internal Action? BeforeValueAdmission { get; set; }

    /// <summary>
    /// Returns the interned key for a sanitized tag key, or null once the instrument tracks its cap of distinct
    /// keys: the key is then dropped from the series and counted under <c>tag_count</c>, with one Warning per
    /// instrument that never names the key.
    /// </summary>
    public MetricTagKey? AdmitKey(ReadOnlySpan<char> key)
    {
        if (_keyLookup.TryGetValue(key, out var admitted))
        {
            return admitted;
        }

        lock (_keyGate)
        {
            if (_keyLookup.TryGetValue(key, out admitted))
            {
                return admitted;
            }

            if (_keys.Count < _maxTagKeys)
            {
                admitted = new MetricTagKey(key.ToString());
                _keys.TryAdd(admitted.Name, admitted);

                return admitted;
            }
        }

        ReportKeyOverflow();

        return null;
    }

    /// <summary>Admits <paramref name="tagKey"/> and maps the value; <c>{other}</c> when the key itself is over the cap.</summary>
    public string MapValue(string tagKey, ReadOnlySpan<char> value)
    {
        var key = AdmitKey(tagKey);

        return key is null ? MetricSeries.Other : MapValue(key, value);
    }

    /// <summary>Returns the interned value, or <c>{other}</c> once the key's distinct values reached the cap.</summary>
    public string MapValue(MetricTagKey key, ReadOnlySpan<char> value)
    {
        if (key.Lookup.TryGetValue(value, out var interned))
        {
            return interned;
        }

        BeforeValueAdmission?.Invoke();

        lock (key.Gate)
        {
            if (key.Lookup.TryGetValue(value, out interned))
            {
                return interned;
            }

            if (key.Values.Count < _maxValuesPerTag)
            {
                var admitted = value.ToString();
                key.Values.TryAdd(admitted, admitted);

                return admitted;
            }
        }

        Report(key.Name, MetricCardinalityGuard.ReasonTagValues);

        return MetricSeries.Other;
    }

    /// <summary>Returns the interned series for a canonical tag set, or the <c>{other}</c> series once the cap is reached.</summary>
    public MetricSeries AdmitSeries(ReadOnlySpan<char> tags)
    {
        if (_seriesLookup.TryGetValue(tags, out var series))
        {
            return series;
        }

        lock (_seriesGate)
        {
            if (_seriesLookup.TryGetValue(tags, out series))
            {
                return series;
            }

            if (_series.Count < _maxSeries)
            {
                var admitted = tags.ToString();
                series = new MetricSeries(_instrument, _application, admitted, _version);
                _series.TryAdd(admitted, series);

                return series;
            }
        }

        Report(null, MetricCardinalityGuard.ReasonSeries);

        return _otherSeries;
    }

    /// <summary>
    /// Reports a key dropped because the measurement carried more than <c>MaxTagsPerInstrument</c> keys. Reported
    /// once per (instrument, key); afterwards it is a flag read, so a host that always sends too many keys pays
    /// no meter add, clock read or dictionary write per measurement.
    /// </summary>
    public void ReportTagCount(MetricTagKey key)
    {
        if (key.TryMarkTagCountReported())
        {
            Report(key.Name, MetricCardinalityGuard.ReasonTagCount);
        }
    }

    /// <summary>
    /// Reports a key dropped because adding it would push the canonical tag set past the <c>Tags</c> column. Reported
    /// once per (instrument, key), naming the key only, never its value (§1.2).
    /// </summary>
    public void ReportTagsTruncated(MetricTagKey key)
    {
        if (key.TryMarkTagsTruncatedReported())
        {
            Report(key.Name, MetricCardinalityGuard.ReasonTagsTruncated);
        }
    }

    public void Report(string? tagKey, string reason) => _guard.Report(_instrument.Name, tagKey, reason);

    // A key over the tracking cap may be dynamic and embed a value (§1.2): it is never named or stored. The
    // first overflow is reported in full; later ones are only counted on the meter.
    private void ReportKeyOverflow()
    {
        if (Volatile.Read(ref _keyOverflowReported) == 0 && Interlocked.Exchange(ref _keyOverflowReported, 1) == 0)
        {
            _guard.Report(_instrument.Name, null, MetricCardinalityGuard.ReasonTagCount);

            return;
        }

        MetricCardinalityGuard.Count(_instrument.Name, MetricCardinalityGuard.ReasonTagCount);
    }
}

/// <summary>
/// One tracked tag key of a collected instrument (its sanitized name), with that key's interned values. Interned
/// per instrument, so two references to the same key are the same instance.
/// </summary>
internal sealed class MetricTagKey
{
    private int _tagCountReported;
    private int _tagsTruncatedReported;

    public MetricTagKey(string name)
    {
        Name = name;
        Lookup = Values.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public string Name { get; }

    public ConcurrentDictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> Lookup { get; }

    public Lock Gate { get; } = new();

    public bool TryMarkTagCountReported()
        => Volatile.Read(ref _tagCountReported) == 0 && Interlocked.Exchange(ref _tagCountReported, 1) == 0;

    public bool TryMarkTagsTruncatedReported()
        => Volatile.Read(ref _tagsTruncatedReported) == 0 && Interlocked.Exchange(ref _tagsTruncatedReported, 1) == 0;
}

/// <summary>An instrument that collapsed at least once: the tag keys it collapsed on, why, and when it last did.</summary>
internal sealed record MetricCollapse(string Instrument, IReadOnlyList<string> TagKeys, IReadOnlyList<string> Reasons, DateTime LastCollapsedAt);
