using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Metrics;

namespace Warp.Benchmarks.Benchmarks;

/// <summary>
/// SC10: the cost of one measurement on the caller's thread for an already-seen series, through the real
/// collector path (<c>MeterListener</c> callback, tag projection, cardinality guard, in-memory aggregate).
/// Budget: at most 1 µs and at most one allocation per measurement.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class MetricCollectorBenchmarks : IDisposable
{
    private const string MeterName = "bench";

    private Meter _meter = null!;
    private WarpMeterCollector _collector = null!;
    private Counter<long> _counter = null!;
    private Histogram<double> _histogram = null!;

    [GlobalSetup]
    public void Setup()
    {
        var options = new WarpMetricsOptions();
        options.CollectMeter(MeterName);

        var aggregator = new MetricAggregator(TimeProvider.System, Options.Create(new WarpConfiguration()));
        var guard = new MetricCardinalityGuard(options, TimeProvider.System, NullLogger<MetricCardinalityGuard>.Instance);

        _collector = new WarpMeterCollector(options, aggregator, guard, application: null);
        _collector.Start();

        _meter = new Meter(MeterName);
        _counter = _meter.CreateCounter<long>("payment.results");
        _histogram = _meter.CreateHistogram<double>("payment.latency", unit: "ms");

        // Warm the series once so the benchmark measures the steady state, not series creation.
        EmitCounter();
        EmitHistogram();
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _meter?.Dispose();
        _collector?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Benchmark]
    public void CounterAdd_WarmSeries_ThreeTags() => EmitCounter();

    [Benchmark]
    public void HistogramRecord_WarmSeries_ThreeTags() => EmitHistogram();

    private void EmitCounter()
        => _counter.Add(
            1,
            new KeyValuePair<string, object?>("step", "capture"),
            new KeyValuePair<string, object?>("gateway", "primary"),
            new KeyValuePair<string, object?>("result", "authorised"));

    private void EmitHistogram()
        => _histogram.Record(
            42.5,
            new KeyValuePair<string, object?>("step", "capture"),
            new KeyValuePair<string, object?>("gateway", "primary"),
            new KeyValuePair<string, object?>("result", "authorised"));
}
