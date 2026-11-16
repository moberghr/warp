namespace Warp.Core.Metrics;

/// <summary>
/// Configuration for the <c>System.Diagnostics.Metrics</c> collector, set via <c>opt.AddMetrics(m =&gt; …)</c>.
/// Nothing is collected unless a meter is named: every default is "collect nothing". Tag values of collected
/// instruments are persisted, so use <see cref="InstrumentOptions.KeepTags"/> on any instrument whose tags you
/// do not control, and never tag a measurement with PII (§1.2). Warp's own <c>Warp</c> meter is never
/// collected, even when named.
/// </summary>
public sealed class WarpMetricsOptions
{
    public const string AspNetCoreHostingMeter = "Microsoft.AspNetCore.Hosting";

    public const string AspNetCoreRequestDuration = "http.server.request.duration";

    public const string HttpClientMeter = "System.Net.Http";

    public const string HttpClientRequestDuration = "http.client.request.duration";

    /// <summary>The maximum number of explicit histogram bounds (the overflow bucket is implicit).</summary>
    public const int MaxBucketBounds = 20;

    private readonly Dictionary<string, MeterSelector> _meters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InstrumentOptions> _instruments = new(StringComparer.Ordinal);

    /// <summary>Tags kept per measurement after projection; extra keys are dropped in ordinal key order and reported.</summary>
    public int MaxTagsPerInstrument { get; set; } = 6;

    /// <summary>Distinct values per (instrument, tag key) in this process; later new values are recorded as <c>{other}</c>.</summary>
    public int MaxValuesPerTag { get; set; } = 100;

    /// <summary>Distinct series per instrument in this process; later new series are recorded under the <c>{other}</c> tag set.</summary>
    public int MaxSeriesPerInstrument { get; set; } = 500;

    /// <summary>Collected instruments per process; further instruments are not enabled and are reported.</summary>
    public int MaxInstruments { get; set; } = 300;

    /// <summary>
    /// The most (series, 5-minute bucket) aggregates this process keeps in memory while point writes fail. A failed
    /// write merges its aggregates back for the next flush; one that would grow the pending set past this cap drops the
    /// OLDEST buckets instead (counted on <c>warp.metrics.collapsed{reason=dropped}</c>, a Warning at most once a
    /// minute), so a database outage bounds memory instead of growing it without limit. The default, 60,000, is ten
    /// times the default <see cref="MaxSeriesPerInstrument"/> over an hour of 5-minute buckets.
    /// </summary>
    public int MaxPendingBuckets { get; set; } = 60_000;

    /// <summary>
    /// True when every series this process collects also carries its <c>ApplicationVersion</c> as a dimension (set by
    /// <see cref="ByVersion"/>). Default false: <c>metric_point.Version</c> stays null and storage is unchanged.
    /// </summary>
    public bool CollectByVersion { get; private set; }

    /// <summary>The collected meters, by meter name.</summary>
    public IReadOnlyDictionary<string, MeterSelector> Meters => _meters;

    /// <summary>Per-instrument options that apply whichever meter publishes the instrument.</summary>
    public IReadOnlyDictionary<string, InstrumentOptions> Instruments => _instruments;

    /// <summary>Collects every instrument of <paramref name="meterName"/>.</summary>
    public WarpMetricsOptions CollectMeter(string meterName)
    {
        GetOrAddMeter(meterName).CollectAll = true;

        return this;
    }

    /// <summary>
    /// Collects <paramref name="meterName"/>, restricted to the instruments <paramref name="configure"/> lists.
    /// A selector that lists no instrument collects every instrument of the meter.
    /// </summary>
    public WarpMetricsOptions CollectMeter(string meterName, Action<MeterSelector> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var selector = GetOrAddMeter(meterName);
        configure(selector);

        if (selector.InstrumentNames.Count == 0)
        {
            selector.CollectAll = true;
        }

        return this;
    }

    /// <summary>
    /// Stamps this process's <c>ApplicationVersion</c> on every series it collects, so the Metrics page can filter and
    /// break a metric down by version (old vs new during a rolling deploy). Series count grows by one set per version
    /// running at once; old versions age out with retention. Ignored when <c>ApplicationName</c> or
    /// <c>ApplicationVersion</c> is not set.
    /// </summary>
    public WarpMetricsOptions ByVersion()
    {
        CollectByVersion = true;

        return this;
    }

    /// <summary>Configures an instrument by name, whichever collected meter publishes it. Does not enable a meter.</summary>
    public WarpMetricsOptions Instrument(string name, Action<InstrumentOptions> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        if (!_instruments.TryGetValue(name, out var options))
        {
            options = new InstrumentOptions();
            _instruments[name] = options;
        }

        configure(options);

        return this;
    }

    /// <summary>
    /// Preset: ASP.NET Core's <c>http.server.request.duration</c> (meter <c>Microsoft.AspNetCore.Hosting</c>),
    /// keeping <c>http.route</c> (the template, not the raw path), <c>http.request.method</c> and
    /// <c>http.response.status_code</c>. Configuration only: Warp takes no ASP.NET Core dependency.
    /// </summary>
    public WarpMetricsOptions CollectAspNetCoreHttp()
        => CollectMeter(AspNetCoreHostingMeter, x => x
            .Instrument(AspNetCoreRequestDuration, y => y.KeepTags("http.route", "http.request.method", "http.response.status_code")));

    /// <summary>
    /// Preset: <c>HttpClient</c>'s <c>http.client.request.duration</c> (meter <c>System.Net.Http</c>), keeping
    /// <c>http.request.method</c>, <c>server.address</c>, <c>http.response.status_code</c> and <c>error.type</c>.
    /// </summary>
    public WarpMetricsOptions CollectHttpClient()
        => CollectMeter(HttpClientMeter, x => x
            .Instrument(HttpClientRequestDuration, y => y.KeepTags("http.request.method", "server.address", "http.response.status_code", "error.type")));

    internal void Validate()
    {
        if (MaxTagsPerInstrument < 1)
        {
            throw new InvalidOperationException($"MaxTagsPerInstrument must be at least 1 (was {MaxTagsPerInstrument}).");
        }

        if (MaxValuesPerTag < 1)
        {
            throw new InvalidOperationException($"MaxValuesPerTag must be at least 1 (was {MaxValuesPerTag}).");
        }

        if (MaxSeriesPerInstrument < 1)
        {
            throw new InvalidOperationException($"MaxSeriesPerInstrument must be at least 1 (was {MaxSeriesPerInstrument}).");
        }

        if (MaxInstruments < 1)
        {
            throw new InvalidOperationException($"MaxInstruments must be at least 1 (was {MaxInstruments}).");
        }

        if (MaxPendingBuckets < 1)
        {
            throw new InvalidOperationException($"MaxPendingBuckets must be at least 1 (was {MaxPendingBuckets}).");
        }
    }

    /// <summary>
    /// The effective options for <paramref name="instrumentName"/> on <paramref name="meterName"/>: meter-scoped
    /// settings win field by field over the global <see cref="Instrument"/> settings.
    /// </summary>
    internal (IReadOnlyList<string>? Tags, IReadOnlyList<double>? Buckets) Resolve(string meterName, string instrumentName)
    {
        InstrumentOptions? scoped = null;
        if (_meters.TryGetValue(meterName, out var selector))
        {
            selector.Instruments.TryGetValue(instrumentName, out scoped);
        }

        _instruments.TryGetValue(instrumentName, out var global);

        return (scoped?.Tags ?? global?.Tags, scoped?.BucketBounds ?? global?.BucketBounds);
    }

    private MeterSelector GetOrAddMeter(string meterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meterName);

        if (!_meters.TryGetValue(meterName, out var selector))
        {
            selector = new MeterSelector();
            _meters[meterName] = selector;
        }

        return selector;
    }
}

/// <summary>Restricts a collected meter to named instruments, optionally configuring each.</summary>
public sealed class MeterSelector
{
    private readonly Dictionary<string, InstrumentOptions> _instruments = new(StringComparer.Ordinal);

    /// <summary>True when every instrument of the meter is collected.</summary>
    public bool CollectAll { get; internal set; }

    /// <summary>The listed instrument names (empty when none is listed).</summary>
    public IReadOnlyCollection<string> InstrumentNames => _instruments.Keys;

    internal IReadOnlyDictionary<string, InstrumentOptions> Instruments => _instruments;

    /// <summary>Collects <paramref name="name"/> from this meter.</summary>
    public MeterSelector Instrument(string name) => Instrument(name, _ => { });

    /// <summary>Collects <paramref name="name"/> from this meter with the given options.</summary>
    public MeterSelector Instrument(string name, Action<InstrumentOptions> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        if (!_instruments.TryGetValue(name, out var options))
        {
            options = new InstrumentOptions();
            _instruments[name] = options;
        }

        configure(options);

        return this;
    }

    internal bool Matches(string instrumentName) => CollectAll || _instruments.ContainsKey(instrumentName);
}

/// <summary>Per-instrument tag allowlist and histogram bucket bounds.</summary>
public sealed class InstrumentOptions
{
    /// <summary>The tag allowlist; null keeps every tag (still capped by <see cref="WarpMetricsOptions.MaxTagsPerInstrument"/>).</summary>
    public IReadOnlyList<string>? Tags { get; private set; }

    /// <summary>Explicit histogram bounds; null falls back to the instrument's advice, then to the unit default.</summary>
    public IReadOnlyList<double>? BucketBounds { get; private set; }

    /// <summary>Keeps only these tag keys; every other tag is dropped before the series is formed.</summary>
    public InstrumentOptions KeepTags(params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        if (tags.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Tag keys must be non-empty.", nameof(tags));
        }

        Tags = [.. tags.Distinct(StringComparer.Ordinal)];

        return this;
    }

    /// <summary>
    /// Explicit histogram bounds: at most 20, finite and strictly increasing. The overflow bucket is implicit. Each
    /// bound is stored rounded to 7 significant digits; the rounded ladder must still be strictly increasing and fit
    /// 256 characters as CSV, or this throws (it is never truncated, which would misalign the bucket counts).
    /// </summary>
    public InstrumentOptions Buckets(params double[] bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);

        if (!MetricBuckets.IsValid(bounds))
        {
            throw new ArgumentException(
                $"Histogram bounds must be 1 to {WarpMetricsOptions.MaxBucketBounds} finite values in strictly increasing order.",
                nameof(bounds));
        }

        if (!MetricBuckets.TryNormalize(bounds, out var normalized))
        {
            throw new ArgumentException(
                $"Histogram bounds rounded to 7 significant digits must stay strictly increasing and fit {MetricPointMapper.MaxBoundsLength} characters as CSV.",
                nameof(bounds));
        }

        BucketBounds = normalized;

        return this;
    }
}
