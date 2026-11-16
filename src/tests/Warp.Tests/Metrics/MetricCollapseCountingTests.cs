using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Logging;
using Warp.Core.Metrics;
using Warp.Core.Models;
using Warp.Core.Services;

namespace Warp.Tests.Metrics;

/// <summary>
/// The "counted" halves of two <c>warp.metrics.collapsed</c> reports, pinned at the NoDb level: the
/// <c>MaxPendingBuckets</c> cap's <c>reason=dropped</c> on a failed flush, and the query service's
/// <c>reason=error</c> for a corrupt bucket CSV. The DB classes assert the state side of both (the oldest bucket
/// gone, the corrupt row's percentile share skipped); these assert the meter side, deterministically:
/// <list type="bullet">
/// <item>the state behind each count is asserted first through the aggregator and the accumulator directly;</item>
/// <item>the meter listener is created BEFORE the code under test runs, and filters on an instrument name unique to
/// the test (a GUID) as well as the reason — the meter is process-global and every collector, rollup and query in
/// the run reports on it, under shared names like <c>h</c> or <c>c</c>;</item>
/// <item>both code paths count synchronously on the calling thread, so the assertion reads after the call
/// returns with no wait.</item>
/// </list>
/// </summary>
[Trait("Category", "NoDb")]
public class MetricCollapseCountingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 10, 7, 30, TimeSpan.Zero);

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task FlushOnce_FailedWriteOverThePendingCap_CountsTheDroppedBucketOnTheCollapsedMeter()
    {
        var name = $"collapse.{Guid.NewGuid():N}.dropped";
        var counted = 0L;
        using var listener = ListenForCollapses(name, MetricCardinalityGuard.ReasonDropped, x => Interlocked.Add(ref counted, x));
        var time = new FakeTimeProvider(Now);
        var options = new WarpMetricsOptions();
        var guard = new MetricCardinalityGuard(options, time, NullLogger<MetricCardinalityGuard>.Instance);
        var aggregator = new MetricAggregator(time, 5, maxPendingBuckets: 2);
        using var collector = new WarpMeterCollector(options, aggregator, guard, null);
        var services = new ServiceCollection();
        services.AddScoped<IMetricPointSink, FailingSink>();
        await using var provider = services.BuildServiceProvider();
        using var flusher = new MetricFlusher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            collector,
            aggregator,
            guard,
            time,
            Options.Create(new WarpConfiguration()),
            NullLogger<MetricFlusher>.Instance);
        var series = new MetricSeries(new MetricInstrumentInfo(name, "test.meter", MetricKind.Counter, null, null, null, false), null, "-");
        aggregator.Record(series, 1);
        time.Advance(TimeSpan.FromMinutes(5));
        aggregator.Record(series, 2);
        time.Advance(TimeSpan.FromMinutes(5));
        aggregator.Record(series, 3);

        var written = await flusher.FlushOnceAsync(Ct);

        // State: nothing written, two buckets kept, the oldest one (value 1) gone.
        written.ShouldBe(0);
        aggregator.PendingCount.ShouldBe(2);
        aggregator.Drain().Select(x => x.Sum).Order().ShouldBe([2d, 3d]);

        // Meter: exactly the one dropped bucket, counted under this instrument.
        Interlocked.Read(ref counted).ShouldBe(1);
    }

    [TimedFact]
    public void MetricAccumulator_CorruptOrMismatchedBucketCsv_KeepsCountsAndReportsTheRowUnusable()
    {
        var boundsCache = new Dictionary<string, double[]?>(StringComparer.Ordinal);
        var accumulator = new MetricAccumulator();

        accumulator.Add(Row(10, 100, "5,3,2,0"), boundsCache).ShouldBeTrue();
        accumulator.Add(Row(5, 20, "5,x,0,0"), boundsCache).ShouldBeFalse();
        accumulator.Add(Row(2, 4, "2,0"), boundsCache).ShouldBeFalse();

        accumulator.Count.ShouldBe(17);
        accumulator.Sum.ShouldBe(124);
    }

    [TimedFact]
    public async Task GetSeries_CorruptBucketCsvRows_CountedOnceOnTheCollapsedMeterAsError()
    {
        var name = $"collapse.{Guid.NewGuid():N}.corrupt";
        var counted = 0L;
        using var listener = ListenForCollapses(name, MetricCardinalityGuard.ReasonError, x => Interlocked.Add(ref counted, x));
        var options = new DbContextOptionsBuilder<TestContext>()
            .UseInMemoryDatabase($"collapse-{Guid.NewGuid():N}")
            .Options;
        var bucket = Now.UtcDateTime.AddMinutes(-20);
        await using (var seed = new TestContext(options))
        {
            seed.Set<MetricInstrument>().Add(new MetricInstrument { Name = name, Meter = "test", Kind = MetricKind.Histogram, Bounds = "10,100,1000", FirstSeenAt = bucket, LastSeenAt = bucket });
            seed.Set<MetricPoint>().AddRange(
                Point(name, bucket, 10, 100, "5,3,2,0"),
                Point(name, bucket, 5, 20, "5,x,0,0"),
                Point(name, bucket, 2, 4, "2,0"));
            await seed.SaveChangesAsync(Ct);
        }

        await using var context = new TestContext(options);
        var service = new MetricQueryService<TestContext>(context, Options.Create(new WarpConfiguration()));

        var result = await service.GetSeries(
            name,
            new MetricSeriesQuery
            {
                From = Now.UtcDateTime.AddHours(-1),
                To = Now.UtcDateTime,
                Filters = new Dictionary<string, string>(StringComparer.Ordinal),
                Compare = [],
                Percentile = 95,
            },
            Ct);

        // State: every row's count and sum kept; only the first row's ladder feeds the percentile.
        var point = result.ShouldNotBeNull().Series.ShouldHaveSingleItem().Points.ShouldHaveSingleItem();
        point.Count.ShouldBe(17);
        point.Sum.ShouldBe(124);
        point.Percentile.ShouldBe(1000);

        // Meter: one report per read of the instrument, however many rows were corrupt.
        Interlocked.Read(ref counted).ShouldBe(1);
    }

    /// <summary>
    /// True for <c>warp.metrics.collapsed</c>, matched by meter and instrument NAME. Never by
    /// <c>ReferenceEquals(x, WarpTelemetry.MetricsCollapsed)</c>: when the listener starts before <c>WarpTelemetry</c>'s
    /// static initializer has run, the counter is published from INSIDE that initializer, before the static field is
    /// assigned, so the reference compare sees null and the instrument is never enabled — a listener that silently
    /// counts zero, depending only on whether an earlier test in the process happened to touch <c>WarpTelemetry</c>.
    /// <c>WarpTelemetry.ServiceName</c> is a const, so reading it triggers no initialization.
    /// </summary>
    internal static bool IsCollapsedMeter(Instrument instrument)
        => string.Equals(instrument.Meter.Name, WarpTelemetry.ServiceName, StringComparison.Ordinal)
            && string.Equals(instrument.Name, "warp.metrics.collapsed", StringComparison.Ordinal);

    private static MetricPointRow Row(long count, double sum, string buckets)
        => new() { Tier = MetricTier.Fine, BucketStart = Now.UtcDateTime, Tags = "-", Count = count, Sum = sum, Buckets = buckets, Bounds = "10,100,1000" };

    private static MetricPoint Point(string instrument, DateTime bucket, long count, double sum, string buckets)
        => new() { Instrument = instrument, Tags = "-", Tier = MetricTier.Fine, BucketStart = bucket, Count = count, Sum = sum, Min = 1, Max = 900, Buckets = buckets, Bounds = "10,100,1000" };

    // warp.metrics.collapsed for one instrument and reason, enabled before the code under test runs.
    private static MeterListener ListenForCollapses(string instrument, string reason, Action<long> onCollapse)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (x, y) =>
            {
                if (IsCollapsedMeter(x))
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

    private sealed class FailingSink : IMetricPointSink
    {
        public Task WritePointsAsync(IReadOnlyList<MetricPoint> points, CancellationToken ct)
            => throw new InvalidOperationException("injected points failure");

        public Task UpsertInstrumentsAsync(IReadOnlyList<MetricInstrument> rows, CancellationToken ct)
            => throw new InvalidOperationException("injected registry failure");
    }
}
