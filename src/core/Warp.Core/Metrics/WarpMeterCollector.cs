using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Diagnostics.Metrics;
using System.Numerics;
using System.Runtime.InteropServices;
using Warp.Core.Enums;
using Warp.Core.Logging;

namespace Warp.Core.Metrics;

/// <summary>
/// The process's <see cref="MeterListener"/> for <c>AddMetrics</c>. Enables only instruments of collected
/// meters (never Warp's own <c>Warp</c> meter, whose data already has its own pipelines), projects each
/// measurement's tags into a series, and adds it to <see cref="MetricAggregator"/>.
/// <para>
/// The measurement callback runs on the caller's thread — inside a host request or a library call — so it does
/// in-memory work only and <b>never throws</b>: a failure is counted on <c>warp.metrics.collapsed{reason=error}</c>,
/// logged once per instrument without values, and the measurement is dropped (§8.19 "instrumentation never
/// out-throws").
/// </para>
/// <para>
/// Observable instruments are read by <see cref="RecordObservable"/>, which storage calls before each drain.
/// <c>ObservableCounter</c> and <c>ObservableUpDownCounter</c> report cumulative values. Several source values
/// can project onto one series (a <c>KeepTags</c> allowlist, the tag-count cap, a value or series collapse), so
/// a read first <b>sums</b> each series' cumulative values for the whole pass, then records the delta of that
/// total against the previous pass's total in this process. The first total of a series is a baseline only; a
/// Counter total that went down means the source reset, and the new total is the delta.
/// </para>
/// </summary>
internal sealed class WarpMeterCollector : IDisposable
{
    private readonly WarpMetricsOptions _options;
    private readonly MetricAggregator _aggregator;
    private readonly MetricCardinalityGuard _guard;
    private readonly string? _application;
    private readonly string? _version;
    private readonly Lock _gate = new();
    private readonly Lock _observeGate = new();

    // Guarded by _observeGate: the previous pass's total per cumulative series, and the totals of the pass in
    // progress. RecordObservableInstruments invokes the callbacks synchronously on the reading thread, so the
    // measurement callback writes _pass on the thread that holds the gate.
    private readonly Dictionary<MetricSeries, double> _cumulative = [];
    private readonly Dictionary<MetricSeries, double> _passTotals = [];
    private Dictionary<MetricSeries, double>? _pass;
    private MeterListener? _listener;

    public WarpMeterCollector(
        WarpMetricsOptions options,
        MetricAggregator aggregator,
        MetricCardinalityGuard guard,
        string? application,
        string? version = null)
    {
        _options = options;
        _aggregator = aggregator;
        _guard = guard;
        _application = MetricPointQueries.NormalizeApplication(application);

        // A version without an application is ignored, like every other §8.23 setting.
        _version = _application is null ? null : MetricPointQueries.NormalizeVersion(version);
    }

    /// <summary>True while the listener is running.</summary>
    public bool IsStarted => _listener is not null;

    /// <summary>The application every series carries, sanitized like a tag value; null when none is set.</summary>
    public string? Application => _application;

    /// <summary>The version every series carries (only with <c>ByVersion()</c> and an application), sanitized like a tag value.</summary>
    public string? Version => _version;

    /// <summary>
    /// Starts the listener. Idempotent. Starts nothing when no meter is collected, so a host that named no meter
    /// pays nothing.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_listener is not null || _options.Meters.Count == 0)
            {
                return;
            }

            var listener = new MeterListener
            {
                InstrumentPublished = OnInstrumentPublished,
            };

            listener.SetMeasurementEventCallback<byte>(OnMeasurement);
            listener.SetMeasurementEventCallback<short>(OnMeasurement);
            listener.SetMeasurementEventCallback<int>(OnMeasurement);
            listener.SetMeasurementEventCallback<long>(OnMeasurement);
            listener.SetMeasurementEventCallback<float>(OnMeasurement);
            listener.SetMeasurementEventCallback<double>(OnMeasurement);
            listener.SetMeasurementEventCallback<decimal>(OnMeasurement);

            _listener = listener;
            listener.Start();
        }
    }

    /// <summary>
    /// Reads every enabled observable instrument into the aggregator. Never throws: each failing host observable
    /// callback is counted on <c>warp.metrics.collapsed{reason=error}</c> and logged once per exception type
    /// (never its message); the other instruments' values from the same pass are still recorded, except the
    /// cumulative (observable counter) deltas: a pass with any failure commits none of them and keeps the previous
    /// baselines, since a partial pass total would be counted twice.
    /// </summary>
    public void RecordObservable()
    {
        var listener = _listener;
        if (listener is null)
        {
            return;
        }

        lock (_observeGate)
        {
            _passTotals.Clear();
            _pass = _passTotals;
            var failed = false;
            try
            {
                listener.RecordObservableInstruments();
            }
            catch (Exception ex)
            {
                failed = true;
                ReportObservableFailures(ex);
            }
            finally
            {
                _pass = null;
            }

            // A failure is not attributable to an instrument, and a callback may have yielded part of its measurements
            // before throwing, so a series' pass total can be partial. Committing it would read as a drop — a reset for
            // a Counter, recording the whole partial total — and the next full pass would count the difference again.
            // So a failed pass commits nothing and keeps the previous baselines; the next clean pass records the whole
            // delta since the last committed one.
            if (!failed)
            {
                CommitCumulative(_passTotals);
            }

            _passTotals.Clear();
        }
    }

    /// <summary>Stops the listener. Measurements after this point are not collected.</summary>
    public void Stop()
    {
        MeterListener? listener;
        lock (_gate)
        {
            listener = _listener;
            _listener = null;
        }

        listener?.Dispose();
    }

    public void Dispose() => Stop();

    /// <summary>Maps an instrument type onto its aggregate kind; null for an instrument type Warp does not aggregate.</summary>
    internal static (MetricKind Kind, bool IsCumulative)? Classify(Instrument instrument)
    {
        for (var type = instrument.GetType(); type is not null; type = type.BaseType)
        {
            if (!type.IsGenericType)
            {
                continue;
            }

            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(Counter<>))
            {
                return (MetricKind.Counter, false);
            }

            if (definition == typeof(ObservableCounter<>))
            {
                return (MetricKind.Counter, true);
            }

            if (definition == typeof(UpDownCounter<>))
            {
                return (MetricKind.UpDownCounter, false);
            }

            if (definition == typeof(ObservableUpDownCounter<>))
            {
                return (MetricKind.UpDownCounter, true);
            }

            if (definition == typeof(Histogram<>))
            {
                return (MetricKind.Histogram, false);
            }

            if (definition == typeof(Gauge<>) || definition == typeof(ObservableGauge<>))
            {
                return (MetricKind.Gauge, false);
            }
        }

        return null;
    }

    // Never lets a reporting failure (a throwing logger provider) reach the host's thread.
    private static void ReportSafely(MetricCardinalityGuard guard, string instrument, Exception exception)
    {
        try
        {
            guard.ReportError(instrument, exception);
        }
#pragma warning disable RCS1075 // reporting must not out-throw the failure it reports (a logger provider can throw): the host's thread comes first.
        catch (Exception)
        {
            // Deliberately ignored: the failure being reported is already dropped, and nothing may reach the host's thread.
        }
#pragma warning restore RCS1075
    }

    private static ReadOnlyCollection<Exception> Unwrap(Exception exception)
        => exception is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : new ReadOnlyCollection<Exception>([exception]);

    private void OnInstrumentPublished(Instrument instrument, MeterListener listener)
    {
        try
        {
            var meterName = instrument.Meter.Name;
            if (string.Equals(meterName, WarpTelemetry.ServiceName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!_options.Meters.TryGetValue(meterName, out var selector) || !selector.Matches(instrument.Name))
            {
                return;
            }

            if (Classify(instrument) is not { } classification)
            {
                // Selected by name but of a type Warp does not aggregate (a custom Instrument subclass): say so once
                // rather than leave the host wondering why a selected instrument never shows.
                _guard.Report(instrument.Name, null, MetricCardinalityGuard.ReasonUnsupported);

                return;
            }

            // Refused rather than truncated: two long names cut to one prefix would merge into one series in storage.
            if (instrument.Name.Length > MetricPointMapper.MaxNameLength)
            {
                _guard.Report(instrument.Name, null, MetricCardinalityGuard.ReasonNameTooLong);

                return;
            }

            var (tags, buckets) = _options.Resolve(meterName, instrument.Name);
            var resolved = classification.Kind == MetricKind.Histogram ? MetricBuckets.Resolve(buckets, instrument) : (ResolvedBounds?)null;
            var info = new MetricInstrumentInfo(
                instrument.Name,
                meterName,
                classification.Kind,
                instrument.Unit,
                instrument.Description,
                resolved?.Bounds,
                classification.IsCumulative);

            if (!_guard.TryAdmitInstrument(info))
            {
                return;
            }

            if (resolved is { AdviceRejected: true })
            {
                _guard.Report(instrument.Name, null, MetricCardinalityGuard.ReasonBounds);
            }

            var state = new CollectedInstrument(
                info,
                _guard.CreateInstrument(info, _application, _version),
                tags?.ToFrozenSet(StringComparer.Ordinal));

            listener.EnableMeasurementEvents(instrument, state);
        }
        catch (Exception ex)
        {
            // Publication runs inside the host's meter.CreateX call; never let it fail there.
            ReportSafely(_guard, instrument.Name, ex);
        }
    }

    private void OnMeasurement<T>(Instrument instrument, T measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        where T : struct, INumberBase<T>
    {
        if (state is not CollectedInstrument collected)
        {
            return;
        }

        try
        {
            var value = double.CreateTruncating(measurement);
            if (!double.IsFinite(value))
            {
                // Counted only (no state, no log): this runs on the host's thread for every such measurement.
                MetricCardinalityGuard.Count(collected.Info.Name, MetricCardinalityGuard.ReasonNonFinite);

                return;
            }

            var series = MetricSeries.Resolve(tags, collected.Cardinality, collected.Allowlist, _options.MaxTagsPerInstrument);

            if (collected.Info.IsCumulative)
            {
                // Observable callbacks only run inside RecordObservable, on this thread: sum per series for the pass.
                var pass = _pass;
                if (pass is not null)
                {
                    CollectionsMarshal.GetValueRefOrAddDefault(pass, series, out _) += value;
                }

                return;
            }

            if (!_aggregator.Record(series, value))
            {
                collected.Cardinality.Report(null, MetricCardinalityGuard.ReasonError);
            }
        }
        catch (Exception ex)
        {
            ReportSafely(_guard, collected.Info.Name, ex);
        }
    }

    // Each series' pass total becomes a delta against the previous pass's total. The first total is a baseline
    // only (no event). A Counter's total only grows within a process, so a drop means the source reset: the new
    // total is the delta.
    private void CommitCumulative(Dictionary<MetricSeries, double> totals)
    {
        foreach (var (series, total) in totals)
        {
            try
            {
                if (!double.IsFinite(total))
                {
                    // Summing a pass can overflow; never let infinity become a baseline or a delta.
                    MetricCardinalityGuard.Count(series.Instrument.Name, MetricCardinalityGuard.ReasonNonFinite);

                    continue;
                }

                if (!_cumulative.TryGetValue(series, out var previous))
                {
                    _cumulative[series] = total;

                    continue;
                }

                _cumulative[series] = total;
                var delta = total - previous;

                if (series.Instrument.Kind == MetricKind.Counter && delta < 0)
                {
                    delta = total;
                }

                if (!_aggregator.Record(series, delta))
                {
                    _guard.Report(series.Instrument.Name, null, MetricCardinalityGuard.ReasonError);
                }
            }
            catch (Exception ex)
            {
                ReportSafely(_guard, series.Instrument.Name, ex);
            }
        }
    }

    private void ReportObservableFailures(Exception exception)
    {
        foreach (var failure in Unwrap(exception))
        {
            try
            {
                _guard.ReportObservableFailure(failure);
            }
#pragma warning disable RCS1075 // the reader is storage's flush loop: a throwing logger provider must not stop the read.
            catch (Exception)
            {
                // Deliberately ignored: the failure is already counted where it could be, and the read must complete.
            }
#pragma warning restore RCS1075
        }
    }

    private sealed class CollectedInstrument
    {
        public CollectedInstrument(MetricInstrumentInfo info, MetricInstrumentCardinality cardinality, IReadOnlySet<string>? allowlist)
        {
            Info = info;
            Cardinality = cardinality;
            Allowlist = allowlist;
        }

        public MetricInstrumentInfo Info { get; }

        public MetricInstrumentCardinality Cardinality { get; }

        public IReadOnlySet<string>? Allowlist { get; }
    }
}
