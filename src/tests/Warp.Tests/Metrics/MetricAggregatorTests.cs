using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Warp.Core.Enums;
using Warp.Core.Metrics;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Metrics;

/// <summary>
/// NoDb coverage for <see cref="MetricAggregator"/>: per-(series, fine bucket) aggregation, bucket alignment,
/// the lossless drain (a sealed aggregate refuses late writes, which land in the next drain) and the additive
/// merge-back storage uses when a write fails.
/// </summary>
[Trait("Category", "NoDb")]
public class MetricAggregatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 10, 7, 30, TimeSpan.Zero);

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public void Record_SameSeriesSameBucket_AggregatesCountSumMinMaxAndBuckets()
    {
        var aggregator = new MetricAggregator(new FakeTimeProvider(Now), 5);
        var series = Series(MetricKind.Histogram, [10, 100]);

        aggregator.Record(series, 5);
        aggregator.Record(series, 50);
        aggregator.Record(series, 500);

        var point = aggregator.Drain().ShouldHaveSingleItem();
        point.Count.ShouldBe(3);
        point.Sum.ShouldBe(555);
        point.Min.ShouldBe(5);
        point.Max.ShouldBe(500);
        point.Buckets.ShouldBe([1L, 1L, 1L]);
        point.Bounds.ShouldBe([10d, 100d]);
        point.BucketStart.ShouldBe(new DateTime(2026, 11, 13, 10, 5, 0, DateTimeKind.Utc));
    }

    [TimedFact]
    public void Record_AcrossFineBucketBoundary_KeepsSeparateAggregates()
    {
        var time = new FakeTimeProvider(Now);
        var aggregator = new MetricAggregator(time, 5);
        var series = Series(MetricKind.Counter, null);

        aggregator.Record(series, 1);
        time.Advance(TimeSpan.FromMinutes(5));
        aggregator.Record(series, 2);

        var points = aggregator.Drain();
        points.Count.ShouldBe(2);
        points.Select(x => x.BucketStart).Order().ShouldBe(
        [
            new DateTime(2026, 11, 13, 10, 5, 0, DateTimeKind.Utc),
            new DateTime(2026, 11, 13, 10, 10, 0, DateTimeKind.Utc),
        ]);
        points.ShouldAllBe(x => x.Buckets == null);
    }

    [TimedFact]
    public void Drain_Twice_SecondDrainIsEmpty()
    {
        var aggregator = new MetricAggregator(new FakeTimeProvider(Now), 5);
        aggregator.Record(Series(MetricKind.Counter, null), 1);

        aggregator.Drain().Count.ShouldBe(1);

        aggregator.Drain().ShouldBeEmpty();
        aggregator.PendingCount.ShouldBe(0);
    }

    [TimedFact]
    public void MergeBack_DrainedSnapshot_IsAddedIntoTheNextDrain()
    {
        var aggregator = new MetricAggregator(new FakeTimeProvider(Now), 5);
        var series = Series(MetricKind.Histogram, [10, 100]);
        aggregator.Record(series, 5);
        aggregator.Record(series, 500);
        var failedWrite = aggregator.Drain();
        aggregator.Record(series, 50);

        aggregator.MergeBack(failedWrite);

        var point = aggregator.Drain().ShouldHaveSingleItem();
        point.Count.ShouldBe(3);
        point.Sum.ShouldBe(555);
        point.Min.ShouldBe(5);
        point.Max.ShouldBe(500);
        point.Buckets.ShouldBe([1L, 1L, 1L]);
    }

    [TimedFact]
    public void MergeBack_OverMaxPendingBuckets_DropsTheOldestBucketsAndReturnsThem()
    {
        var time = new FakeTimeProvider(Now);
        var aggregator = new MetricAggregator(time, 5, maxPendingBuckets: 2);
        var series = Series(MetricKind.Counter, null);
        aggregator.Record(series, 1);
        time.Advance(TimeSpan.FromMinutes(5));
        aggregator.Record(series, 2);
        time.Advance(TimeSpan.FromMinutes(5));
        aggregator.Record(series, 3);
        var failedWrite = aggregator.Drain();
        failedWrite.Count.ShouldBe(3);

        var dropped = aggregator.MergeBack(failedWrite);

        dropped.ShouldHaveSingleItem().BucketStart.ShouldBe(new DateTime(2026, 11, 13, 10, 5, 0, DateTimeKind.Utc));
        aggregator.PendingCount.ShouldBe(2);
        aggregator.Drain().Select(x => x.Sum).Order().ShouldBe([2d, 3d]);
    }

    [TimedFact]
    public void MergeBack_AtTheCap_StillMergesIntoAnAlreadyPendingBucket()
    {
        var aggregator = new MetricAggregator(new FakeTimeProvider(Now), 5, maxPendingBuckets: 1);
        var series = Series(MetricKind.Counter, null);
        aggregator.Record(series, 1);
        var failedWrite = aggregator.Drain();
        aggregator.Record(series, 4);

        var dropped = aggregator.MergeBack(failedWrite);

        dropped.ShouldBeEmpty();
        aggregator.Drain().ShouldHaveSingleItem().Sum.ShouldBe(5);
    }

    [TimedFact]
    public void TryRecord_AfterSeal_IsRefused()
    {
        var aggregate = new MetricAggregate(Series(MetricKind.Counter, null), Now.UtcDateTime);
        aggregate.TryRecord(1, -1, -1).ShouldBe(MetricRecordResult.Recorded);

        var snapshot = aggregate.Seal();

        aggregate.TryRecord(2, -1, -1).ShouldBe(MetricRecordResult.Sealed);
        snapshot.Count.ShouldBe(1);
        snapshot.Sum.ShouldBe(1);
    }

    [TimedFact]
    public void Record_SameNameWithDifferentBounds_IsRefusedWithoutIndexingOutOfRange()
    {
        var aggregator = new MetricAggregator(new FakeTimeProvider(Now), 5);
        var name = $"agg.{Guid.NewGuid():N}";
        var narrow = new MetricSeries(new MetricInstrumentInfo(name, "meter.one", MetricKind.Histogram, null, null, [10], false), null, "a=1");
        var wide = new MetricSeries(new MetricInstrumentInfo(name, "meter.two", MetricKind.Histogram, null, null, [10, 100, 1000], false), null, "a=1");
        aggregator.Record(narrow, 5).ShouldBeTrue();

        var recorded = aggregator.Record(wide, 5000);

        recorded.ShouldBeFalse();
        var point = aggregator.Drain().ShouldHaveSingleItem();
        point.Count.ShouldBe(1);
        point.Buckets.ShouldBe([1L, 0L]);
    }

    [TimedFact]
    public async Task Record_DrainSealsTheFetchedAggregate_LateWriteLandsInTheNextDrain()
    {
        var aggregator = new MetricAggregator(new FakeTimeProvider(Now), 5);
        var series = Series(MetricKind.Counter, null);
        aggregator.Record(series, 1);

        var barrier = new BarrierSignal();
        var blocked = 0;
        aggregator.AfterFetch = _ =>
        {
            // Pin only the first fetch: the recorder holds the aggregate the drain is about to seal.
            if (Interlocked.Exchange(ref blocked, 1) == 0)
            {
                barrier.Running.Release();
                barrier.CanFinish.Wait(TimeSpan.FromSeconds(5), Ct).ShouldBeTrue();
            }
        };
        var recorder = Task.Run(() => aggregator.Record(series, 1), Ct);
        (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue();

        var first = aggregator.Drain();
        barrier.CanFinish.Release();
        (await recorder).ShouldBeTrue();
        var second = aggregator.Drain();

        first.ShouldHaveSingleItem().Count.ShouldBe(1);
        second.ShouldHaveSingleItem().Count.ShouldBe(1);
        first.Concat(second).Sum(x => x.Count).ShouldBe(2);
    }

    private static MetricSeries Series(MetricKind kind, double[]? bounds)
        => new(new MetricInstrumentInfo($"agg.{Guid.NewGuid():N}", "test.meter", kind, null, null, bounds, false), null, "a=1");
}
