using System.Diagnostics.Metrics;
using System.Globalization;
using Shouldly;
using Warp.Core.Enums;
using Warp.Core.Metrics;

namespace Warp.Tests.Metrics;

/// <summary>
/// NoDb coverage for <see cref="MetricBuckets"/>: bounds precedence configured &gt; <c>Instrument&lt;T&gt;.Advice</c>
/// &gt; unit default (R6), validation, and bucket indexing with the implicit overflow bucket.
/// </summary>
[Trait("Category", "NoDb")]
public class MetricBucketsTests
{
    [TimedFact]
    public void Resolve_ConfiguredBuckets_WinOverAdvice()
    {
        using var meter = new Meter($"test.{Guid.NewGuid():N}");
        var histogram = meter.CreateHistogram("h", "s", null, null, new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.1, 1] });

        var bounds = MetricBuckets.Resolve([1, 2, 3], histogram).Bounds;

        bounds.ShouldBe([1d, 2d, 3d]);
    }

    [TimedFact]
    public void Resolve_NoConfiguredBuckets_UsesAdvice()
    {
        using var meter = new Meter($"test.{Guid.NewGuid():N}");
        var histogram = meter.CreateHistogram("h", "s", null, null, new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.1, 1] });

        var bounds = MetricBuckets.Resolve(null, histogram).Bounds;

        bounds.ShouldBe([0.1, 1d]);
    }

    [TimedFact]
    public void Resolve_IntegerAdvice_ConvertsToDouble()
    {
        using var meter = new Meter($"test.{Guid.NewGuid():N}");
        var histogram = meter.CreateHistogram("h", "By", null, null, new InstrumentAdvice<int> { HistogramBucketBoundaries = [10, 20] });

        var bounds = MetricBuckets.Resolve(null, histogram).Bounds;

        bounds.ShouldBe([10d, 20d]);
    }

    [TimedTheory]
    [InlineData("s", 0.005)]
    [InlineData("ms", 5)]
    [InlineData("{request}", 0)]
    [InlineData(null, 0)]
    public void Resolve_NoAdvice_UsesUnitDefault(string? unit, double firstBound)
    {
        using var meter = new Meter($"test.{Guid.NewGuid():N}");
        var histogram = meter.CreateHistogram<double>("h", unit);

        var bounds = MetricBuckets.Resolve(null, histogram).Bounds;

        bounds[0].ShouldBe(firstBound);
        MetricBuckets.IsValid(bounds).ShouldBeTrue();
    }

    [TimedFact]
    public void Resolve_InvalidAdvice_FallsBackToUnitDefault()
    {
        using var meter = new Meter($"test.{Guid.NewGuid():N}");
        var histogram = meter.CreateHistogram("h", "ms", null, null, new InstrumentAdvice<double> { HistogramBucketBoundaries = [.. Enumerable.Range(1, 25).Select(x => (double)x)] });

        var resolved = MetricBuckets.Resolve(null, histogram);

        resolved.Bounds.ShouldBe(MetricBuckets.Milliseconds);
        resolved.AdviceRejected.ShouldBeTrue();
    }

    [TimedFact]
    public void Resolve_ComputedLadder_StoresBoundsThatRoundTripAndMatchTheBucketCounts()
    {
        // A computed ladder carries up to 17 significant digits per rung; stored and bucketed, it must stay aligned.
        using var meter = new Meter($"test.{Guid.NewGuid():N}");
        double[] ladder = [.. Enumerable.Range(0, WarpMetricsOptions.MaxBucketBounds).Select(x => Math.Pow(1.5, x))];
        var histogram = meter.CreateHistogram("h", "{x}", null, null, new InstrumentAdvice<double> { HistogramBucketBoundaries = ladder });
        var resolved = MetricBuckets.Resolve(null, histogram);
        var info = new MetricInstrumentInfo("h", meter.Name, MetricKind.Histogram, "{x}", null, resolved.Bounds, false);
        var aggregator = new MetricAggregator(TimeProvider.System, 5);
        var series = new MetricSeries(info, null, MetricSeries.NoTags);
        aggregator.Record(series, ladder[3]);
        var snapshot = aggregator.Drain().ShouldHaveSingleItem();

        MetricPointMapper.TryToPoint(snapshot, out var point).ShouldBeTrue();

        resolved.AdviceRejected.ShouldBeFalse();
        point.Bounds.ShouldNotBeNull();
        point.Bounds.Length.ShouldBeLessThanOrEqualTo(MetricPointMapper.MaxBoundsLength);
        var stored = point.Bounds.Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        stored.ShouldBe(resolved.Bounds);
        stored.Length.ShouldBe(ladder.Length);
        var counts = point.Buckets!.Split(',');
        counts.Length.ShouldBe(stored.Length + 1);
        counts[3].ShouldBe("1");
    }

    [TimedFact]
    public void Buckets_ConfiguredBoundsTooLongToStore_Throws()
    {
        // 20 valid, strictly increasing bounds whose 7-significant-digit CSV is still longer than 256 characters.
        double[] bounds = [.. Enumerable.Range(0, WarpMetricsOptions.MaxBucketBounds).Select(x => -1.111111e-100 * (WarpMetricsOptions.MaxBucketBounds + 1 - x))];
        MetricBuckets.IsValid(bounds).ShouldBeTrue();

        Should.Throw<ArgumentException>(() => new InstrumentOptions().Buckets(bounds));
    }

    [TimedFact]
    public void Resolve_AdviceTooLongToStore_FallsBackToTheUnitDefaultAndFlagsIt()
    {
        using var meter = new Meter($"test.{Guid.NewGuid():N}");
        double[] advice = [.. Enumerable.Range(0, WarpMetricsOptions.MaxBucketBounds).Select(x => -1.111111e-100 * (WarpMetricsOptions.MaxBucketBounds + 1 - x))];
        var histogram = meter.CreateHistogram("h", "s", null, null, new InstrumentAdvice<double> { HistogramBucketBoundaries = advice });

        var resolved = MetricBuckets.Resolve(null, histogram);

        resolved.AdviceRejected.ShouldBeTrue();
        resolved.Bounds.ShouldBe(MetricBuckets.Seconds);
    }

    [TimedFact]
    public void IsValid_RejectsEmptyTooManyNonIncreasingAndNonFinite()
    {
        MetricBuckets.IsValid([]).ShouldBeFalse();
        MetricBuckets.IsValid([.. Enumerable.Range(1, 21).Select(x => (double)x)]).ShouldBeFalse();
        MetricBuckets.IsValid([1, 1]).ShouldBeFalse();
        MetricBuckets.IsValid([2, 1]).ShouldBeFalse();
        MetricBuckets.IsValid([1, double.NaN]).ShouldBeFalse();
        MetricBuckets.IsValid([.. Enumerable.Range(1, 20).Select(x => (double)x)]).ShouldBeTrue();
    }

    [TimedFact]
    public void Buckets_InvalidBounds_Throws()
    {
        Should.Throw<ArgumentException>(() => new InstrumentOptions().Buckets(5, 1));
    }

    [TimedTheory]
    [InlineData(0, 0)]
    [InlineData(10, 0)]
    [InlineData(10.5, 1)]
    [InlineData(100, 1)]
    [InlineData(100.01, 2)]
    public void IndexOf_ReturnsFirstBoundAtOrAboveValueElseOverflow(double value, int expected)
    {
        MetricBuckets.IndexOf([10, 100], value).ShouldBe(expected);
    }

    [TimedFact]
    public void FormatCounts_ThenTryParseCounts_RoundTrips()
    {
        var csv = MetricBuckets.FormatCounts([1, 0, 42]);

        csv.ShouldBe("1,0,42");
        MetricBuckets.TryParseCounts(csv, 3, out var counts).ShouldBeTrue();
        counts.ShouldBe([1L, 0L, 42L]);
    }

    [TimedTheory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("1,x,3", null)]
    [InlineData("1,-2,3", null)]
    [InlineData("1, 2,3", null)]
    [InlineData("1,2", 3)]
    [InlineData("1,2,3,4", 3)]
    public void TryParseCounts_MalformedOrMismatched_IsFalseNeverAnException(string? csv, int? expectedLength)
    {
        MetricBuckets.TryParseCounts(csv, expectedLength, out var counts).ShouldBeFalse();
        counts.ShouldBeNull();
    }

    [TimedFact]
    public void TrySumCounts_SumsElementWise_AndOneEmptySideContributesNothing()
    {
        MetricBuckets.TrySumCounts("1,2,3", "4,5,6", 3, out var sum).ShouldBeTrue();
        sum.ShouldBe("5,7,9");

        MetricBuckets.TrySumCounts(null, "4,5,6", 3, out var right).ShouldBeTrue();
        right.ShouldBe("4,5,6");

        MetricBuckets.TrySumCounts(null, null, 3, out var none).ShouldBeTrue();
        none.ShouldBeNull();
    }

    [TimedTheory]
    [InlineData("1,2,3", "1,x,3")]
    [InlineData("1,2,3", "1,2")]
    [InlineData("9223372036854775807,0,0", "1,0,0")]
    public void TrySumCounts_MalformedMismatchedOrOverflowing_IsFalse(string first, string second)
    {
        MetricBuckets.TrySumCounts(first, second, 3, out var sum).ShouldBeFalse();
        sum.ShouldBeNull();
    }

    [TimedFact]
    public void TrySumCounts_ResultLongerThanTheColumn_IsFalseNeverTruncated()
    {
        // A normalized ladder (at most 21 counts) always fits; a stored row with a longer ladder need not. 30 counts of
        // 19 digits each: every side parses, but the summed CSV cannot fit 512 characters.
        var big = string.Join(',', Enumerable.Repeat("4000000000000000000", 30));

        MetricBuckets.TrySumCounts(big, big, 30, out var sum).ShouldBeFalse();
        sum.ShouldBeNull();
    }
}
