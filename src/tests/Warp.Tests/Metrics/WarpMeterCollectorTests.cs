using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Warp.Core;
using Warp.Core.Enums;
using Warp.Core.Logging;
using Warp.Core.Metrics;
using Warp.Tests.Observability;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Metrics;

/// <summary>
/// NoDb coverage for <see cref="WarpMeterCollector"/> and <c>AddMetrics</c>: each instrument kind's aggregate
/// (spec §B table), meter selection (R1, the <c>Warp</c> meter never collected), observable-counter deltas with
/// a first-sight baseline (R5), the instrument cap (R9), a throwing measurement path never reaching host code
/// (R3), and registration only under <c>AddMetrics</c>. Every test uses its own meter name: the listener is
/// process-global and tests run in parallel.
/// </summary>
[Trait("Category", "NoDb")]
public class WarpMeterCollectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 10, 7, 30, TimeSpan.Zero);

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public void Counter_Measurements_AggregateCountAndSum()
    {
        var name = MeterName();
        using var meter = new Meter(name);
        using var harness = Harness.Start(x => x.CollectMeter(name));
        var counter = meter.CreateCounter<long>("payments");

        counter.Add(2, new KeyValuePair<string, object?>("outcome", "ok"));
        counter.Add(3, new KeyValuePair<string, object?>("outcome", "ok"));

        var point = harness.Aggregator.Drain().ShouldHaveSingleItem();
        point.Series.Instrument.Kind.ShouldBe(MetricKind.Counter);
        point.Series.Instrument.MeterName.ShouldBe(name);
        point.Series.Tags.ShouldBe("outcome=ok");
        point.Count.ShouldBe(2);
        point.Sum.ShouldBe(5);
        point.Buckets.ShouldBeNull();
    }

    [TimedFact]
    public void UpDownCounter_Measurements_AggregateSumOfDeltas()
    {
        var name = MeterName();
        using var meter = new Meter(name);
        using var harness = Harness.Start(x => x.CollectMeter(name));
        var counter = meter.CreateUpDownCounter<int>("inflight");

        counter.Add(5);
        counter.Add(-2);

        var point = harness.Aggregator.Drain().ShouldHaveSingleItem();
        point.Series.Instrument.Kind.ShouldBe(MetricKind.UpDownCounter);
        point.Series.Tags.ShouldBe(MetricSeries.NoTags);
        point.Sum.ShouldBe(3);
    }

    [TimedFact]
    public void Histogram_Measurements_AggregateCountSumMinMaxAndConfiguredBuckets()
    {
        var name = MeterName();
        using var meter = new Meter(name);
        using var harness = Harness.Start(x => x
            .CollectMeter(name)
            .Instrument("latency", y => y.Buckets(10, 100)));
        var histogram = meter.CreateHistogram<double>("latency", "ms");

        histogram.Record(5);
        histogram.Record(50);
        histogram.Record(500);

        var point = harness.Aggregator.Drain().ShouldHaveSingleItem();
        point.Series.Instrument.Kind.ShouldBe(MetricKind.Histogram);
        point.Bounds.ShouldBe([10d, 100d]);
        point.Count.ShouldBe(3);
        point.Sum.ShouldBe(555);
        point.Min.ShouldBe(5);
        point.Max.ShouldBe(500);
        point.Buckets.ShouldBe([1L, 1L, 1L]);
    }

    [TimedFact]
    public void Histogram_WithAdvice_UsesAdviceBounds()
    {
        var name = MeterName();
        using var meter = new Meter(name);
        using var harness = Harness.Start(x => x.CollectMeter(name));
        var histogram = meter.CreateHistogram("duration", "s", null, null, new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.1, 1] });

        histogram.Record(0.5);

        harness.Aggregator.Drain().ShouldHaveSingleItem().Bounds.ShouldBe([0.1, 1d]);
    }

    [TimedFact]
    public void Gauge_Measurements_AggregateCountSumMinMax()
    {
        var name = MeterName();
        using var meter = new Meter(name);
        using var harness = Harness.Start(x => x.CollectMeter(name));
        var gauge = meter.CreateGauge<double>("temperature");

        gauge.Record(3);
        gauge.Record(7);

        var point = harness.Aggregator.Drain().ShouldHaveSingleItem();
        point.Series.Instrument.Kind.ShouldBe(MetricKind.Gauge);
        point.Count.ShouldBe(2);
        point.Sum.ShouldBe(10);
        point.Min.ShouldBe(3);
        point.Max.ShouldBe(7);
    }

    [TimedFact]
    public void ObservableGauge_RecordObservable_RecordsTheObservedValue()
    {
        var name = MeterName();
        using var meter = new Meter(name);
        using var harness = Harness.Start(x => x.CollectMeter(name));
        meter.CreateObservableGauge("depth", () => 42L);

        harness.Collector.RecordObservable();

        var point = harness.Aggregator.Drain().ShouldHaveSingleItem();
        point.Series.Instrument.Kind.ShouldBe(MetricKind.Gauge);
        point.Sum.ShouldBe(42);
    }

    [TimedFact]
    public void ObservableCounter_FirstObservation_IsABaselineOnly()
    {
        var name = MeterName();
        using var meter = new Meter(name);
        using var harness = Harness.Start(x => x.CollectMeter(name));
        meter.CreateObservableCounter("processed", () => 10L);

        harness.Collector.RecordObservable();

        harness.Aggregator.Drain().ShouldBeEmpty();
    }

    [TimedFact]
    public void ObservableCounter_LaterObservations_RecordDeltas()
    {
        var name = MeterName();
        using var meter = new Meter(name);
        using var harness = Harness.Start(x => x.CollectMeter(name));
        var cumulative = 10L;
        meter.CreateObservableCounter("processed", () => cumulative);

        harness.Collector.RecordObservable();
        cumulative = 15;
        harness.Collector.RecordObservable();
        cumulative = 22;
        harness.Collector.RecordObservable();

        var point = harness.Aggregator.Drain().ShouldHaveSingleItem();
        point.Series.Instrument.Kind.ShouldBe(MetricKind.Counter);
        point.Series.Instrument.IsCumulative.ShouldBeTrue();
        point.Count.ShouldBe(2);
        point.Sum.ShouldBe(12);
    }

    [TimedFact]
    public void ObservableUpDownCounter_LaterObservations_RecordSignedDeltas()
    {
        var name = MeterName();
        using var meter = new Meter(name);
        using var harness = Harness.Start(x => x.CollectMeter(name));
        var cumulative = 10;
        meter.CreateObservableUpDownCounter("connections", () => cumulative);

        harness.Collector.RecordObservable();
        cumulative = 4;
        harness.Collector.RecordObservable();

        var point = harness.Aggregator.Drain().ShouldHaveSingleItem();
        point.Series.Instrument.Kind.ShouldBe(MetricKind.UpDownCounter);
        point.Sum.ShouldBe(-6);
    }

    [TimedFact]
    public void ObservableCounter_SourcesProjectedOntoOneSeries_RecordTheDeltaOfTheirSum()
    {
        var name = MeterName();
        using var meter = new Meter(name);
        using var harness = Harness.Start(x => x
            .CollectMeter(name)
            .Instrument("processed", y => y.KeepTags("b")));
        var first = 100L;
        var second = 20L;
        meter.CreateObservableCounter<long>("processed", () => Cumulative(("1", first), ("2", second)));
        harness.Collector.RecordObservable();
        first++;
        second++;

        harness.Collector.RecordObservable();

        var point = harness.Aggregator.Drain().ShouldHaveSingleItem();
        point.Series.Tags.ShouldBe("b=x");
        point.Count.ShouldBe(1);
        point.Sum.ShouldBe(2);
    }

    [TimedFact]
    public void ObservableCounter_SourcesProjectedOntoOneSeries_FirstPassIsABaselineOnly()
    {
        var name = MeterName();
        using var meter = new Meter(name);
        using var harness = Harness.Start(x => x
            .CollectMeter(name)
            .Instrument("processed", y => y.KeepTags("b")));
        meter.CreateObservableCounter<long>("processed", () => Cumulative(("1", 100), ("2", 20), ("3", 5)));

        harness.Collector.RecordObservable();

        harness.Aggregator.Drain().ShouldBeEmpty();
    }

    [TimedFact]
    public void RecordObservable_HostCallbacksThrow_DoesNotThrowAndCountsEachFailure()
    {
        var name = MeterName();
        using var meter = new Meter(name);
        using var harness = Harness.Start(x => x.CollectMeter(name));
        var failures = 0L;
        using var listener = ListenForObservableFailures(x => Interlocked.Add(ref failures, x));
        meter.CreateObservableGauge("broken.one", ThrowingCallback);
        meter.CreateObservableGauge("broken.two", ThrowingCallback);
        meter.CreateObservableGauge("healthy", () => 7L);

        Should.NotThrow(() => harness.Collector.RecordObservable());
        Should.NotThrow(() => harness.Collector.RecordObservable());

        var points = harness.Aggregator.Drain();
        points.ShouldHaveSingleItem().Series.Instrument.Name.ShouldBe("healthy");
        points[0].Count.ShouldBe(2);
        Interlocked.Read(ref failures).ShouldBe(4);
        harness.Guard.Collapsed().ShouldHaveSingleItem().Instrument.ShouldBe(MetricCardinalityGuard.ObservableInstrument);
        var warning = harness.Warnings.ShouldHaveSingleItem();
        warning.ShouldContain(typeof(InvalidOperationException).FullName!);
        warning.ShouldNotContain(ThrowingTagValue.Secret);
    }

    [TimedFact]
    public void InstrumentPublished_SameNameOnTwoMetersWithDifferentBounds_CollectsOnlyTheFirst()
    {
        var firstName = MeterName();
        var secondName = MeterName();
        using var firstMeter = new Meter(firstName);
        using var secondMeter = new Meter(secondName);
        using var harness = Harness.Start(x => x
            .CollectMeter(firstName)
            .CollectMeter(secondName));
        var first = firstMeter.CreateHistogram("shared", null, null, null, new InstrumentAdvice<double> { HistogramBucketBoundaries = [1, 2] });
        var second = secondMeter.CreateHistogram("shared", null, null, null, new InstrumentAdvice<double> { HistogramBucketBoundaries = [1, 2, 3, 4] });

        Should.NotThrow(() =>
        {
            first.Record(1.5);
            second.Record(3.5);
        });

        var point = harness.Aggregator.Drain().ShouldHaveSingleItem();
        point.Series.Instrument.MeterName.ShouldBe(firstName);
        point.Count.ShouldBe(1);
        point.Buckets.ShouldBe([0L, 1L, 0L]);
        harness.Guard.Collapsed().ShouldHaveSingleItem().Reasons.ShouldBe([MetricCardinalityGuard.ReasonNameConflict]);
        var warning = harness.Warnings.ShouldHaveSingleItem();
        warning.ShouldContain(firstName);
        warning.ShouldContain(secondName);
    }

    [TimedFact]
    public void InstrumentPublished_MeterRecreatedWithTheSameInstrument_IsCollectedAgain()
    {
        var name = MeterName();
        using var harness = Harness.Start(x => x.CollectMeter(name));
        using (var first = new Meter(name))
        {
            first.CreateCounter<long>("recreated").Add(1);
        }

        using var second = new Meter(name);
        second.CreateCounter<long>("recreated").Add(2);

        var point = harness.Aggregator.Drain().ShouldHaveSingleItem();
        point.Count.ShouldBe(2);
        point.Sum.ShouldBe(3);
        harness.Guard.Collapsed().ShouldBeEmpty();
    }

    [TimedFact]
    public async Task Counter_TwoMeasurementsPinnedInsideTheAggregate_BothLand()
    {
        var name = MeterName();
        using var meter = new Meter(name);
        using var harness = Harness.Start(x => x.CollectMeter(name));
        var counter = meter.CreateCounter<long>("concurrent");
        var barrier = new BarrierSignal();
        var pinned = 0;
        harness.Aggregator.AfterFetch = _ =>
        {
            if (Interlocked.Increment(ref pinned) <= 2)
            {
                barrier.Running.Release();
                barrier.CanFinish.Wait(TimeSpan.FromSeconds(5), Ct).ShouldBeTrue();
            }
        };

        var one = Task.Run(() => counter.Add(1), Ct);
        var two = Task.Run(() => counter.Add(1), Ct);
        (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue();
        (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue();
        barrier.CanFinish.Release(2);
        await Task.WhenAll(one, two);

        var point = harness.Aggregator.Drain().ShouldHaveSingleItem();
        point.Count.ShouldBe(2);
        point.Sum.ShouldBe(2);
    }

    [TimedFact]
    public void Collector_ApplicationName_IsSanitizedLikeATagValue()
    {
        using var harness = Harness.Start(_ => { }, application: "  shop,eu=1\n ");

        harness.Collector.Application.ShouldBe("shop-eu-1");
    }

    [TimedFact]
    public void Measurement_WithVersion_CarriesTheSanitizedVersionOnTheSeries()
    {
        var name = MeterName();
        using var meter = new Meter(name);
        using var harness = Harness.Start(x => x.CollectMeter(name), application: "orders", version: " 2.4.1,rc=1 ");

        meter.CreateCounter<long>("c").Add(1);

        var series = harness.Aggregator.Drain().ShouldHaveSingleItem().Series;
        series.Application.ShouldBe("orders");
        series.Version.ShouldBe("2.4.1-rc-1");
    }

    [TimedFact]
    public void Collector_VersionWithoutApplication_IsIgnored()
    {
        using var harness = Harness.Start(_ => { }, application: null, version: "2.4.1");

        harness.Collector.Version.ShouldBeNull();
    }

    [TimedFact]
    public async Task AddMetrics_ByVersion_StampsTheApplicationVersion()
    {
        var services = NewServices();
        services.AddWarp<TestContext>(x =>
        {
            x.ApplicationName = "orders";
            x.ApplicationVersion = "2.4.1";
            x.AddMetrics(y => y.CollectMeter(MeterName()).ByVersion());
        });
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<WarpMeterCollector>().Version.ShouldBe("2.4.1");
    }

    [TimedFact]
    public async Task AddMetrics_WithoutByVersion_LeavesTheVersionOff()
    {
        var services = NewServices();
        services.AddWarp<TestContext>(x =>
        {
            x.ApplicationName = "orders";
            x.ApplicationVersion = "2.4.1";
            x.AddMetrics(y => y.CollectMeter(MeterName()));
        });
        await using var provider = services.BuildServiceProvider();

        var collector = provider.GetRequiredService<WarpMeterCollector>();
        collector.Version.ShouldBeNull();
        collector.Application.ShouldBe("orders");
    }

    [TimedFact]
    public void WarpMeter_NamedForCollection_IsNeverEnabled()
    {
        var name = MeterName();
        using var meter = new Meter(name);
        using var harness = Harness.Start(x => x
            .CollectMeter(WarpTelemetry.ServiceName)
            .CollectMeter(name));
        var counter = meter.CreateCounter<long>("control");

        WarpTelemetry.MetricsCollapsed.Add(1, new KeyValuePair<string, object?>("instrument", name), new KeyValuePair<string, object?>("reason", "test"));
        counter.Add(1);

        var point = harness.Aggregator.Drain().ShouldHaveSingleItem();
        point.Series.Instrument.MeterName.ShouldBe(name);
    }

    [TimedFact]
    public void Measurement_OnMeterNotCollected_IsIgnored()
    {
        var collected = MeterName();
        using var collectedMeter = new Meter(collected);
        using var otherMeter = new Meter(MeterName());
        using var harness = Harness.Start(x => x.CollectMeter(collected));

        otherMeter.CreateCounter<long>("ignored").Add(1);
        collectedMeter.CreateCounter<long>("kept").Add(1);

        harness.Aggregator.Drain().ShouldHaveSingleItem().Series.Instrument.Name.ShouldBe("kept");
    }

    [TimedFact]
    public void Measurement_OnInstrumentNotListed_IsIgnored()
    {
        var name = MeterName();
        using var meter = new Meter(name);
        using var harness = Harness.Start(x => x.CollectMeter(name, y => y.Instrument("listed", z => z.KeepTags("route"))));

        meter.CreateCounter<long>("unlisted").Add(1);
        meter.CreateCounter<long>("listed").Add(
            1,
            new KeyValuePair<string, object?>("route", "/orders/{id}"),
            new KeyValuePair<string, object?>("url.full", "https://shop/orders/42"));

        var point = harness.Aggregator.Drain().ShouldHaveSingleItem();
        point.Series.Instrument.Name.ShouldBe("listed");
        point.Series.Tags.ShouldBe("route=/orders/{id}");
    }

    [TimedFact]
    public void Measurement_WithApplicationName_CarriesApplicationOnTheSeries()
    {
        var name = MeterName();
        using var meter = new Meter(name);
        using var harness = Harness.Start(x => x.CollectMeter(name), application: "orders");

        meter.CreateCounter<long>("c").Add(1);

        harness.Aggregator.Drain().ShouldHaveSingleItem().Series.Application.ShouldBe("orders");
    }

    [TimedFact]
    public void InstrumentPublished_BeyondMaxInstruments_IsNotEnabledAndIsReported()
    {
        var name = MeterName();
        using var meter = new Meter(name);
        using var harness = Harness.Start(x =>
        {
            x.MaxInstruments = 1;
            x.CollectMeter(name);
        });

        meter.CreateCounter<long>("first").Add(1);
        meter.CreateCounter<long>("second").Add(1);

        harness.Aggregator.Drain().ShouldHaveSingleItem().Series.Instrument.Name.ShouldBe("first");
        var collapse = harness.Guard.Collapsed().ShouldHaveSingleItem();
        collapse.Instrument.ShouldBe(MetricSeries.Other);
        collapse.Reasons.ShouldBe([MetricCardinalityGuard.ReasonInstrumentCap]);
    }

    [TimedFact]
    public void InstrumentPublished_AdviceTooLongToStore_UsesTheUnitDefaultAndIsReportedOnce()
    {
        var name = MeterName();
        double[] advice = [.. Enumerable.Range(0, WarpMetricsOptions.MaxBucketBounds).Select(x => -1.111111e-100 * (WarpMetricsOptions.MaxBucketBounds + 1 - x))];
        using var harness = Harness.Start(x => x.CollectMeter(name));

        // Published twice (a disposed and re-created meter): still one warning for the instrument.
        for (var i = 0; i < 2; i++)
        {
            using var meter = new Meter(name);
            meter.CreateHistogram("latency", "ms", null, null, new InstrumentAdvice<double> { HistogramBucketBoundaries = advice }).Record(5);
        }

        var points = harness.Aggregator.Drain();
        points.ShouldNotBeEmpty();
        points.ShouldAllBe(x => x.Bounds!.SequenceEqual(MetricBuckets.Milliseconds));
        harness.Guard.Collapsed().ShouldHaveSingleItem().Reasons.ShouldBe([MetricCardinalityGuard.ReasonBounds]);
        harness.Warnings.ShouldHaveSingleItem().ShouldContain("latency");
    }

    [TimedFact]
    public void InstrumentPublished_NameLongerThanTheColumn_IsNeverEnabledAndIsReportedOnce()
    {
        var name = MeterName();
        using var meter = new Meter(name);
        using var harness = Harness.Start(x => x.CollectMeter(name));
        var longName = new string('n', MetricPointMapper.MaxNameLength + 1);

        meter.CreateCounter<long>(longName).Add(1);
        meter.CreateCounter<long>(longName).Add(1);

        harness.Aggregator.Drain().ShouldBeEmpty();
        harness.Guard.InstrumentCount.ShouldBe(0);
        var collapse = harness.Guard.Collapsed().ShouldHaveSingleItem();
        collapse.Instrument.ShouldBe(longName);
        collapse.Reasons.ShouldBe([MetricCardinalityGuard.ReasonNameTooLong]);
        harness.Warnings.Count.ShouldBe(1);
    }

    [TimedFact]
    public void Measurement_WhenRecordingThrows_DoesNotPropagateAndIsCountedOnce()
    {
        var name = MeterName();
        using var meter = new Meter(name);
        using var harness = Harness.Start(x => x.CollectMeter(name));
        var counter = meter.CreateCounter<long>("fragile");

        Should.NotThrow(() => counter.Add(1, new KeyValuePair<string, object?>("customer", new ThrowingTagValue())));
        Should.NotThrow(() => counter.Add(1, new KeyValuePair<string, object?>("customer", new ThrowingTagValue())));

        harness.Aggregator.Drain().ShouldBeEmpty();
        harness.Guard.Collapsed().ShouldHaveSingleItem().Reasons.ShouldBe([MetricCardinalityGuard.ReasonError]);
        var warning = harness.Warnings.ShouldHaveSingleItem();
        warning.ShouldContain("fragile");
        warning.ShouldNotContain(ThrowingTagValue.Secret);
    }

    [TimedFact]
    public void Start_NoMeterCollected_StartsNoListener()
    {
        using var harness = Harness.Start(_ => { });

        harness.Collector.IsStarted.ShouldBeFalse();
    }

    [TimedFact]
    public void AddWarp_WithoutAddMetrics_RegistersNoCollector()
    {
        var services = NewServices();

        services.AddWarp<TestContext>();

        services.ShouldNotContain(x => x.ServiceType == typeof(WarpMeterCollector));
        services.ShouldNotContain(x => x.ServiceType == typeof(MetricAggregator));
        services.ShouldNotContain(x => x.ServiceType == typeof(IMetricsMarker));
        services.ShouldNotContain(x => x.ImplementationType == typeof(MetricCollectorHost));
    }

    [TimedFact]
    public async Task AddMetrics_Registered_HostStartsAndStopsTheListener()
    {
        var name = MeterName();
        var services = NewServices();
        services.AddWarp<TestContext>(x => x.AddMetrics(y => y.CollectMeter(name)));

        services.ShouldContain(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(MetricCollectorHost));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        provider.GetRequiredService<IMetricsMarker>().ShouldNotBeNull();
        var collector = provider.GetRequiredService<WarpMeterCollector>();
        var host = new MetricCollectorHost(collector);

        await host.StartAsync(Xunit.TestContext.Current.CancellationToken);
        collector.IsStarted.ShouldBeTrue();
        await host.StopAsync(Xunit.TestContext.Current.CancellationToken);

        collector.IsStarted.ShouldBeFalse();
    }

    [TimedFact]
    public void AddMetrics_InvalidCap_ThrowsAtRegistration()
    {
        var services = NewServices();

        Should.Throw<InvalidOperationException>(() =>
            services.AddWarp<TestContext>(x => x.AddMetrics(y => y.MaxSeriesPerInstrument = 0)));
    }

    [TimedFact]
    public async Task AddMetrics_MergedOptionsInvalid_ThrowsWhenTheCollectorResolves()
    {
        // Each AddMetrics lambda is probed on its own; a later Configure can still make the merged object invalid.
        var services = NewServices();
        services.AddWarp<TestContext>(x => x.AddMetrics(y => y.CollectMeter(MeterName())));
        services.Configure<WarpMetricsOptions>(x => x.MaxPendingBuckets = 0);
        await using var provider = services.BuildServiceProvider();

        Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<WarpMeterCollector>())
            .Message.ShouldContain(nameof(WarpMetricsOptions.MaxPendingBuckets));
    }

    [TimedFact]
    public void ObservableCounter_PassWhereTheCallbackThrowsAfterAPartialYield_RecordsNoDeltaAndTheNextCleanPassRecordsTheTrueDelta()
    {
        var name = MeterName();
        using var meter = new Meter(name);
        using var harness = Harness.Start(x => x
            .CollectMeter(name)
            .Instrument("processed", y => y.KeepTags("b")));
        var pass = 0;
        meter.CreateObservableCounter("processed", Observe);

        // Pass 1: 100 + 20 = 120, the baseline.
        harness.Collector.RecordObservable();
        harness.Aggregator.Drain().ShouldBeEmpty();

        // Pass 2: the callback yields 105 and throws before the second source. The partial 105 must not read as a
        // reset (which would record 105) nor become the baseline.
        harness.Collector.RecordObservable();
        harness.Aggregator.Drain().ShouldBeEmpty();

        // Pass 3: 106 + 21 = 127, so the true increase since the committed baseline is 7.
        harness.Collector.RecordObservable();
        var point = harness.Aggregator.Drain().ShouldHaveSingleItem();
        point.Count.ShouldBe(1);
        point.Sum.ShouldBe(7);

        IEnumerable<Measurement<long>> Observe()
        {
            pass++;
            yield return new Measurement<long>(pass switch { 1 => 100, 2 => 105, _ => 106 }, new KeyValuePair<string, object?>("a", "1"), new KeyValuePair<string, object?>("b", "x"));
            if (pass == 2)
            {
                throw new InvalidOperationException("observable failed mid-pass");
            }

            yield return new Measurement<long>(pass == 1 ? 20 : 21, new KeyValuePair<string, object?>("a", "2"), new KeyValuePair<string, object?>("b", "x"));
        }
    }

    [TimedFact]
    public void Measurement_NonFinite_IsNotRecordedAndIsCounted()
    {
        var name = MeterName();
        using var meter = new Meter(name);
        using var harness = Harness.Start(x => x.CollectMeter(name));
        var instrument = $"values.{Guid.NewGuid():N}";
        var counted = 0L;
        using var listener = ListenForCollapses(instrument, MetricCardinalityGuard.ReasonNonFinite, x => Interlocked.Add(ref counted, x));
        var counter = meter.CreateCounter<double>(instrument);

        counter.Add(double.NaN);
        counter.Add(double.PositiveInfinity);
        counter.Add(2);

        var point = harness.Aggregator.Drain().ShouldHaveSingleItem();
        point.Count.ShouldBe(1);
        point.Sum.ShouldBe(2);
        Interlocked.Read(ref counted).ShouldBe(2);
        harness.Warnings.ShouldBeEmpty();
    }

    [TimedFact]
    public void InstrumentPublished_UnsupportedInstrumentType_IsNotEnabledAndWarnsOnce()
    {
        var name = MeterName();
        using var meter = new Meter(name);
        using var harness = Harness.Start(x => x.CollectMeter(name));

        _ = new UnsupportedInstrument(meter, "custom.one");
        _ = new UnsupportedInstrument(meter, "custom.one");

        var collapse = harness.Guard.Collapsed().ShouldHaveSingleItem();
        collapse.Instrument.ShouldBe("custom.one");
        collapse.Reasons.ShouldBe([MetricCardinalityGuard.ReasonUnsupported]);
        harness.Warnings.ShouldHaveSingleItem().ShouldContain("custom.one");
        harness.Aggregator.Drain().ShouldBeEmpty();
    }

    private static string MeterName() => $"test.{Guid.NewGuid():N}";

    // warp.metrics.collapsed for one instrument and reason. The meter is process-global: callers use a unique name.
    private static MeterListener ListenForCollapses(string instrument, string reason, Action<long> onCollapse)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (x, y) =>
            {
                if (MetricCollapseCountingTests.IsCollapsedMeter(x))
                {
                    y.EnableMeasurementEvents(x);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            var matchesInstrument = false;
            var matchesReason = false;
            foreach (var tag in tags)
            {
                matchesInstrument |= string.Equals(tag.Key, "instrument", StringComparison.Ordinal) && Equals(tag.Value, instrument);
                matchesReason |= string.Equals(tag.Key, "reason", StringComparison.Ordinal) && Equals(tag.Value, reason);
            }

            if (matchesInstrument && matchesReason)
            {
                onCollapse(measurement);
            }
        });
        listener.Start();

        return listener;
    }

    private static long ThrowingCallback() => throw new InvalidOperationException(ThrowingTagValue.Secret);

    private static Measurement<long>[] Cumulative(params (string A, long Value)[] sources)
        =>
        [
            .. sources.Select(x => new Measurement<long>(
                x.Value,
                new KeyValuePair<string, object?>("a", x.A),
                new KeyValuePair<string, object?>("b", "x"))),
        ];

    private static MeterListener ListenForObservableFailures(Action<long> onFailure)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (x, y) =>
            {
                if (MetricCollapseCountingTests.IsCollapsedMeter(x))
                {
                    y.EnableMeasurementEvents(x);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (string.Equals(tag.Key, "instrument", StringComparison.Ordinal)
                    && Equals(tag.Value, MetricCardinalityGuard.ObservableInstrument))
                {
                    onFailure(measurement);
                }
            }
        });
        listener.Start();

        return listener;
    }

    private static ServiceCollection NewServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TestContext>(x => x.UseInMemoryDatabase($"metrics-{Guid.NewGuid():N}"));

        return services;
    }

    private sealed class Harness : IDisposable
    {
        private Harness(WarpMetricsOptions options, string? application, string? version)
        {
            var time = new FakeTimeProvider(Now);
            Guard = new MetricCardinalityGuard(options, time, new CapturingLoggerFactory(Logs).CreateLogger<MetricCardinalityGuard>());
            Aggregator = new MetricAggregator(time, 5);
            Collector = new WarpMeterCollector(options, Aggregator, Guard, application, version);
        }

        public CapturingLoggerProvider Logs { get; } = new();

        public List<string> Warnings => [.. Logs.Logs.Where(x => x.Level == LogLevel.Warning).Select(x => x.Message)];

        public MetricCardinalityGuard Guard { get; }

        public MetricAggregator Aggregator { get; }

        public WarpMeterCollector Collector { get; }

        public static Harness Start(Action<WarpMetricsOptions> configure, string? application = null, string? version = null)
        {
            var options = new WarpMetricsOptions();
            configure(options);
            var harness = new Harness(options, application, version);
            harness.Collector.Start();

            return harness;
        }

        public void Dispose() => Collector.Dispose();
    }

    // An Instrument<T> subclass Warp does not aggregate: published by name like any instrument, but no known kind.
    private sealed class UnsupportedInstrument : Instrument<long>
    {
        public UnsupportedInstrument(Meter meter, string name)
            : base(meter, name)
        {
            Publish();
        }
    }

    private sealed class ThrowingTagValue
    {
        public const string Secret = "secret-from-tostring";

#pragma warning disable S3877 // the point of this fixture: a tag value whose formatting throws inside the measurement callback.
        public override string ToString() => throw new InvalidOperationException(Secret);
#pragma warning restore S3877
    }
}
