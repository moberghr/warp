using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Warp.Core;
using Warp.Core.Data;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Metrics;
using Warp.Core.Services;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.Observability;
using Warp.Worker.Services;

namespace Warp.Tests.Metrics;

/// <summary>
/// Database coverage for <see cref="MetricFlusher"/>: the drain-and-write of the in-memory aggregates as partial
/// fine-tier <c>metric_point</c> rows (R10), the sink seam (R11), the additive merge-back on a failed write (R12)
/// and the instrument registry upsert, which is a separate save that can fail or race without touching the points.
/// The aggregator is driven directly with a <see cref="FakeTimeProvider"/>, so no <c>MeterListener</c> is involved.
/// </summary>
[GenerateDatabaseTests]
public abstract class MetricFlusherTestsBase : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 10, 7, 30, TimeSpan.Zero);

    private readonly IDatabaseFixture _fixture;

    protected MetricFlusherTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task FlushOnce_DrainedSeries_WritesOneRowPerSeriesAndBucketWithExactValues()
    {
        var harness = CreateHarness();
        var histogram = Series($"flush.{Guid.NewGuid():N}.duration", MetricKind.Histogram, [10, 100], "status=200");
        var counter = Series($"flush.{Guid.NewGuid():N}.payments", MetricKind.Counter, null, "-");
        var gauge = Series($"flush.{Guid.NewGuid():N}.queue", MetricKind.Gauge, null, "-");
        harness.Aggregator.Record(histogram, 5);
        harness.Aggregator.Record(histogram, 50);
        harness.Aggregator.Record(histogram, 500);
        harness.Aggregator.Record(counter, 2);
        harness.Aggregator.Record(counter, 3);
        harness.Aggregator.Record(gauge, 4);
        harness.Aggregator.Record(gauge, 8);
        harness.Time.Advance(TimeSpan.FromMinutes(5));
        harness.Aggregator.Record(histogram, 20);

        var written = await harness.Flusher.FlushOnceAsync(Ct);

        written.ShouldBe(4);
        var rows = await ReadPoints();
        rows.Count.ShouldBe(4);
        rows.ShouldAllBe(x => x.Tier == MetricTier.Fine && !x.Compacted && x.Application == null);

        var firstBucket = new DateTime(2026, 11, 13, 10, 5, 0, DateTimeKind.Utc);
        var histogramRow = rows.Single(x => string.Equals(x.Instrument, histogram.Instrument.Name, StringComparison.Ordinal) && x.BucketStart == firstBucket);
        histogramRow.Tags.ShouldBe("status=200");
        histogramRow.Count.ShouldBe(3);
        histogramRow.Sum.ShouldBe(555);
        histogramRow.Min.ShouldBe(5);
        histogramRow.Max.ShouldBe(500);
        histogramRow.Buckets.ShouldBe("1,1,1");
        histogramRow.Bounds.ShouldBe("10,100");

        var laterRow = rows.Single(x => string.Equals(x.Instrument, histogram.Instrument.Name, StringComparison.Ordinal) && x.BucketStart != firstBucket);
        laterRow.BucketStart.ShouldBe(new DateTime(2026, 11, 13, 10, 10, 0, DateTimeKind.Utc));
        laterRow.Count.ShouldBe(1);
        laterRow.Buckets.ShouldBe("0,1,0");

        var counterRow = rows.Single(x => string.Equals(x.Instrument, counter.Instrument.Name, StringComparison.Ordinal));
        counterRow.Count.ShouldBe(2);
        counterRow.Sum.ShouldBe(5);
        counterRow.Min.ShouldBeNull();
        counterRow.Max.ShouldBeNull();
        counterRow.Buckets.ShouldBeNull();
        counterRow.Bounds.ShouldBeNull();
        counterRow.Tags.ShouldBe("-");

        var gaugeRow = rows.Single(x => string.Equals(x.Instrument, gauge.Instrument.Name, StringComparison.Ordinal));
        gaugeRow.Count.ShouldBe(2);
        gaugeRow.Sum.ShouldBe(12);
        gaugeRow.Min.ShouldBe(4);
        gaugeRow.Max.ShouldBe(8);
    }

    [TimedFact]
    public async Task FlushOnce_Idle_WritesNothingAndDoesNoDatabaseWork()
    {
        var harness = CreateHarness();

        var written = await harness.Flusher.FlushOnceAsync(Ct);

        written.ShouldBe(0);
        harness.Switch.SinkResolutions.ShouldBe(0);
        (await ReadPoints()).ShouldBeEmpty();
        (await ReadInstruments()).ShouldBeEmpty();
    }

    [TimedFact]
    public async Task StopAsync_FlushesWhatIsStillAggregated()
    {
        var harness = CreateHarness();
        var series = Series($"flush.{Guid.NewGuid():N}.stop", MetricKind.Counter, null, "-");

        await harness.Flusher.StartAsync(Ct);
        harness.Aggregator.Record(series, 7);

        await harness.Flusher.StopAsync(Ct);

        var row = (await ReadPoints()).ShouldHaveSingleItem();
        row.Count.ShouldBe(1);
        row.Sum.ShouldBe(7);
        (await ReadInstruments()).ShouldHaveSingleItem().Name.ShouldBe(series.Instrument.Name);
    }

    [TimedFact]
    public async Task FlushOnce_ObservableGaugeOfACollectedMeter_IsReadAndWritten()
    {
        // Observables are only read by the flusher's own pass: nothing records them in between. The meter name is
        // unique because MeterListener is process-global.
        var meterName = $"flush.{Guid.NewGuid():N}.observable";
        var harness = CreateHarness(configureOptions: x => x.CollectMeter(meterName));
        using var collector = harness.Collector;
        collector.Start();
        using var meter = new System.Diagnostics.Metrics.Meter(meterName);
        meter.CreateObservableGauge("queue.depth", () => 42L);

        var written = await harness.Flusher.FlushOnceAsync(Ct);

        written.ShouldBe(1);
        var row = (await ReadPoints()).ShouldHaveSingleItem();
        row.Instrument.ShouldBe("queue.depth");
        row.Tier.ShouldBe(MetricTier.Fine);
        row.Count.ShouldBe(1);
        row.Sum.ShouldBe(42);
        row.Min.ShouldBe(42);
        row.Max.ShouldBe(42);
        var instrument = (await ReadInstruments()).ShouldHaveSingleItem();
        instrument.Name.ShouldBe("queue.depth");
        instrument.Kind.ShouldBe(MetricKind.Gauge);
        instrument.Meter.ShouldBe(meterName);
    }

    [TimedFact]
    public async Task FlushOnce_WhenTheWriteFails_MergesBackAndTheNextFlushWritesEverythingExactlyOnce()
    {
        const string secret = "customer-top-secret";
        var harness = CreateHarness();
        var series = Series($"flush.{Guid.NewGuid():N}.failing", MetricKind.Counter, null, $"customer={secret}");
        harness.Aggregator.Record(series, 2);
        harness.Aggregator.Record(series, 3);
        harness.Switch.FailPoints = true;

        var failed = await harness.Flusher.FlushOnceAsync(Ct);

        failed.ShouldBe(0);
        (await ReadPoints()).ShouldBeEmpty();
        (await ReadInstruments()).ShouldBeEmpty();
        harness.Aggregator.PendingCount.ShouldBe(1);
        var warning = harness.Logger.Logs.Single(x => x.Level == LogLevel.Warning);
        warning.Message.ShouldContain(nameof(InvalidOperationException));
        harness.Logger.Logs.ShouldAllBe(x => !x.Message.Contains(secret, StringComparison.Ordinal));

        harness.Aggregator.Record(series, 4);
        harness.Switch.FailPoints = false;

        (await harness.Flusher.FlushOnceAsync(Ct)).ShouldBe(1);
        (await harness.Flusher.FlushOnceAsync(Ct)).ShouldBe(0);

        var row = (await ReadPoints()).ShouldHaveSingleItem();
        row.Count.ShouldBe(3);
        row.Sum.ShouldBe(9);
    }

    [TimedFact]
    public async Task FlushOnce_NewInstrument_InsertsARegistryRowWithItsMetadata()
    {
        var harness = CreateHarness();
        var name = $"flush.{Guid.NewGuid():N}.registry";
        var series = Series(name, MetricKind.Histogram, [0.5, 1.5], "b=2,a=1", meter: "flush.meter");
        harness.Aggregator.Record(series, 1);

        await harness.Flusher.FlushOnceAsync(Ct);

        var row = (await ReadInstruments()).ShouldHaveSingleItem();
        row.Name.ShouldBe(name);
        row.Meter.ShouldBe("flush.meter");
        row.Kind.ShouldBe(MetricKind.Histogram);
        row.Unit.ShouldBe("ms");
        row.Description.ShouldBe("flush test instrument");
        row.Bounds.ShouldBe("0.5,1.5");
        row.TagKeys.ShouldBe("a,b");
        row.FirstSeenAt.ShouldBe(Now.UtcDateTime);
        row.LastSeenAt.ShouldBe(Now.UtcDateTime);
        row.CollapsedAt.ShouldBeNull();
        row.CollapsedTags.ShouldBeNull();
    }

    [TimedFact]
    public async Task FlushOnce_KnownInstrument_IsRefreshedOnlyAfterAnHour()
    {
        var harness = CreateHarness();
        var series = Series($"flush.{Guid.NewGuid():N}.refresh", MetricKind.Counter, null, "-");
        harness.Aggregator.Record(series, 1);
        await harness.Flusher.FlushOnceAsync(Ct);

        harness.Time.Advance(TimeSpan.FromMinutes(30));
        harness.Aggregator.Record(series, 1);
        await harness.Flusher.FlushOnceAsync(Ct);

        (await ReadInstruments()).ShouldHaveSingleItem().LastSeenAt.ShouldBe(Now.UtcDateTime);

        harness.Time.Advance(TimeSpan.FromMinutes(31));
        harness.Aggregator.Record(series, 1);
        await harness.Flusher.FlushOnceAsync(Ct);

        var refreshed = (await ReadInstruments()).ShouldHaveSingleItem();
        refreshed.LastSeenAt.ShouldBe(Now.UtcDateTime.AddMinutes(61));
        refreshed.FirstSeenAt.ShouldBe(Now.UtcDateTime);
        (await ReadPoints()).Count.ShouldBe(3);
    }

    [TimedFact]
    public async Task FlushOnce_CollapsedInstrument_WritesCollapseInfoAndSkipsSyntheticEntries()
    {
        var harness = CreateHarness();
        var name = $"flush.{Guid.NewGuid():N}.collapsed";
        var series = Series(name, MetricKind.Counter, null, "-");
        harness.Aggregator.Record(series, 1);
        harness.Guard.Report(name, "outcome", MetricCardinalityGuard.ReasonTagValues);
        harness.Guard.Report(MetricSeries.Other, null, MetricCardinalityGuard.ReasonInstrumentCap);
        harness.Guard.ReportObservableFailure(new InvalidOperationException());

        await harness.Flusher.FlushOnceAsync(Ct);

        var row = (await ReadInstruments()).ShouldHaveSingleItem();
        row.Name.ShouldBe(name);
        row.CollapsedAt.ShouldBe(Now.UtcDateTime);
        row.CollapsedTags.ShouldBe("outcome");

        // A newly collapsed key is written at once, well inside the hour a plain refresh waits for.
        harness.Time.Advance(TimeSpan.FromMinutes(1));
        harness.Aggregator.Record(series, 1);
        harness.Guard.Report(name, "region", MetricCardinalityGuard.ReasonTagValues);
        await harness.Flusher.FlushOnceAsync(Ct);

        var widened = (await ReadInstruments()).ShouldHaveSingleItem();
        widened.CollapsedTags.ShouldBe("outcome,region");
        widened.CollapsedAt.ShouldBe(Now.UtcDateTime.AddMinutes(1));
    }

    [TimedFact]
    public async Task FlushOnce_RegistryPrimaryKeyRace_RetriesRowByRowAndMergesIntoTheRacedRow()
    {
        var harness = CreateHarness();
        var name = $"flush.{Guid.NewGuid():N}.race";
        var series = Series(name, MetricKind.Counter, null, "a=1", meter: "flush.meter");
        var outOfBand = Now.UtcDateTime.AddDays(-3);
        harness.Aggregator.Record(series, 1);

        // The other process inserts the same name after this flush read the registry and before it saved, once.
        harness.Switch.BeforeRegistrySave = async ct =>
        {
            harness.Switch.BeforeRegistrySave = null;
            var other = _fixture.CreateContext();
            other.Set<MetricInstrument>().Add(new MetricInstrument
            {
                Name = name,
                Meter = "out.of.band",
                Kind = MetricKind.Counter,
                FirstSeenAt = outOfBand,
                LastSeenAt = outOfBand,
            });
            await other.SaveChangesAsync(ct);
        };

        var written = await harness.Flusher.FlushOnceAsync(Ct);

        // The batch conflicted, so the flusher retried row by row: the retry read the raced row and merged into it,
        // the first publisher's meter and FirstSeenAt intact.
        written.ShouldBe(1);
        (await ReadPoints()).ShouldHaveSingleItem().Sum.ShouldBe(1);
        var merged = (await ReadInstruments()).ShouldHaveSingleItem();
        merged.Meter.ShouldBe("out.of.band");
        merged.FirstSeenAt.ShouldBe(outOfBand);
        merged.LastSeenAt.ShouldBe(Now.UtcDateTime);
        merged.TagKeys.ShouldBe("a");
        harness.Logger.Logs.ShouldNotContain(x => x.Level >= LogLevel.Warning);
    }

    [TimedFact]
    public async Task FlushOnce_SameRegistryConflictOnTwoConsecutiveFlushes_WarnsWithTheInstrumentName()
    {
        var harness = CreateHarness();
        var name = $"flush.{Guid.NewGuid():N}.stuck";
        var series = Series(name, MetricKind.Counter, null, "-");
        harness.Switch.ConflictRegistry = true;

        harness.Aggregator.Record(series, 1);
        await harness.Flusher.FlushOnceAsync(Ct);

        harness.Logger.Logs.ShouldNotContain(x => x.Level >= LogLevel.Warning);

        harness.Time.Advance(TimeSpan.FromMinutes(5));
        harness.Aggregator.Record(series, 1);
        await harness.Flusher.FlushOnceAsync(Ct);

        var warning = harness.Logger.Logs.Where(x => x.Level >= LogLevel.Warning).ShouldHaveSingleItem();
        warning.Message.ShouldContain(name);
        (await ReadPoints()).Count.ShouldBe(2);
        (await ReadInstruments()).ShouldBeEmpty();
    }

    [TimedFact]
    public async Task FlushOnce_NonUniqueRegistryRejection_LogsWarningAndStillWritesThePoints()
    {
        var harness = CreateHarness(classifier: new FixedClassifier(isUnique: false));
        var series = Series($"flush.{Guid.NewGuid():N}.schema", MetricKind.Counter, null, "-");
        harness.Switch.ConflictRegistry = true;
        harness.Aggregator.Record(series, 1);

        var written = await harness.Flusher.FlushOnceAsync(Ct);

        written.ShouldBe(1);
        (await ReadPoints()).ShouldHaveSingleItem().Sum.ShouldBe(1);
        harness.Logger.Logs.ShouldContain(x => x.Level == LogLevel.Warning && x.Message.Contains("other than a key race", StringComparison.Ordinal));
        harness.Logger.Logs.ShouldNotContain(x => x.Level == LogLevel.Debug && x.Message.Contains("was rejected", StringComparison.Ordinal));
    }

    [TimedFact]
    public async Task FlushOnce_UniqueRegistryRejection_StaysAtDebugAsARace()
    {
        var harness = CreateHarness(classifier: new FixedClassifier(isUnique: true));
        var series = Series($"flush.{Guid.NewGuid():N}.unique", MetricKind.Counter, null, "-");
        harness.Switch.ConflictRegistry = true;
        harness.Aggregator.Record(series, 1);

        (await harness.Flusher.FlushOnceAsync(Ct)).ShouldBe(1);

        harness.Logger.Logs.ShouldNotContain(x => x.Level >= LogLevel.Warning);
        harness.Logger.Logs.ShouldContain(x => x.Level == LogLevel.Debug && x.Message.Contains("was rejected", StringComparison.Ordinal));
    }

    [TimedFact]
    public async Task FlushOnce_RegistryFailure_NeverFailsOrReAddsThePoints()
    {
        var harness = CreateHarness();
        var series = Series($"flush.{Guid.NewGuid():N}.registryfail", MetricKind.Counter, null, "-");
        harness.Aggregator.Record(series, 1);
        harness.Switch.FailRegistry = true;

        var written = await harness.Flusher.FlushOnceAsync(Ct);

        written.ShouldBe(1);
        (await ReadPoints()).ShouldHaveSingleItem().Sum.ShouldBe(1);
        (await ReadInstruments()).ShouldBeEmpty();
        harness.Aggregator.PendingCount.ShouldBe(0);
        harness.Logger.Logs.ShouldContain(x => x.Level == LogLevel.Warning);

        harness.Switch.FailRegistry = false;
        harness.Aggregator.Record(series, 10);
        (await harness.Flusher.FlushOnceAsync(Ct)).ShouldBe(1);

        var rows = await ReadPoints();
        rows.Select(x => x.Sum).Order().ToList().ShouldBe([1d, 10d]);
        (await ReadInstruments()).ShouldHaveSingleItem();
    }

    [TimedFact]
    public async Task FlushOnce_ContextSink_WritesPointsAndRegistryOnTContext()
    {
        var harness = CreateHarness(services =>
        {
            // Only the host's TContext exists in this scope: an AddWarp-only process has no server context.
            services.AddScoped(_ => _fixture.CreateContext());
            services.AddScoped<IMetricPointSink, ContextMetricPointSink<TestContext>>();
        });
        var series = Series($"flush.{Guid.NewGuid():N}.context", MetricKind.Counter, null, "-");
        harness.Aggregator.Record(series, 4);

        (await harness.Flusher.FlushOnceAsync(Ct)).ShouldBe(1);

        (await ReadPoints()).ShouldHaveSingleItem().Sum.ShouldBe(4);
        (await ReadInstruments()).ShouldHaveSingleItem().Name.ShouldBe(series.Instrument.Name);
    }

    [TimedFact]
    public async Task FlushOnce_ServerSink_WritesPointsAndRegistryOnTheServerContext()
    {
        var harness = CreateHarness(services =>
        {
            // Only the server context exists in this scope: the sink must not reach for TContext.
            services.AddScoped<IWarpServerContext>(_ => new TestServerContext(_fixture.CreateContext()));
            services.AddScoped<IMetricPointSink, ServerMetricPointSink>();
        });
        var series = Series($"flush.{Guid.NewGuid():N}.server", MetricKind.Counter, null, "-");
        harness.Aggregator.Record(series, 6);

        (await harness.Flusher.FlushOnceAsync(Ct)).ShouldBe(1);

        (await ReadPoints()).ShouldHaveSingleItem().Sum.ShouldBe(6);
        (await ReadInstruments()).ShouldHaveSingleItem().Name.ShouldBe(series.Instrument.Name);
    }

    [TimedFact]
    public async Task FlushOnce_RowThatWouldNeedTruncation_IsSkippedAndReportedNeverCut()
    {
        // MetricSeries.Resolve never builds a tag set past the column (MetricSeriesTests covers the trim), so this
        // series is constructed directly: it is the "cannot happen" case, and the flusher must not cut it into a
        // prefix another series could share. The healthy series beside it is still written.
        var harness = CreateHarness();
        var name = $"flush.{Guid.NewGuid():N}.long";
        var tags = string.Join(',', Enumerable.Range(0, 6).Select(x => $"k{x}={new string((char)('a' + x), 100)}"));
        tags.Length.ShouldBeGreaterThan(MetricPointMapper.MaxTagsLength);
        var overlong = Series(name, MetricKind.Counter, null, tags);
        var healthy = Series($"flush.{Guid.NewGuid():N}.healthy", MetricKind.Counter, null, "-");
        harness.Aggregator.Record(overlong, 1);
        harness.Aggregator.Record(healthy, 2);

        var written = await harness.Flusher.FlushOnceAsync(Ct);

        written.ShouldBe(1);
        var row = (await ReadPoints()).ShouldHaveSingleItem();
        row.Instrument.ShouldBe(healthy.Instrument.Name);
        harness.Aggregator.PendingCount.ShouldBe(0);
        var collapse = harness.Guard.Collapsed().ShouldHaveSingleItem();
        collapse.Instrument.ShouldBe(name);
        collapse.Reasons.ShouldBe([MetricCardinalityGuard.ReasonError]);
    }

    [TimedFact]
    public async Task FlushOnce_NameDifferingOnlyByCaseFromARegistryRow_DoesNotBlockTheOtherInstruments()
    {
        var harness = CreateHarness();
        var seededAt = Now.UtcDateTime.AddDays(-3);
        var ctx = _fixture.CreateContext();
        ctx.Set<MetricInstrument>().Add(new MetricInstrument
        {
            Name = "payments.count",
            Meter = "first.meter",
            Kind = MetricKind.Counter,
            FirstSeenAt = seededAt,
            LastSeenAt = seededAt,
        });
        await ctx.SaveChangesAsync(Ct);
        var other = Series($"flush.{Guid.NewGuid():N}.other", MetricKind.Counter, null, "-");
        harness.Aggregator.Record(Series("Payments.Count", MetricKind.Counter, null, "-", meter: "second.meter"), 1);
        harness.Aggregator.Record(other, 1);

        var written = await harness.Flusher.FlushOnceAsync(Ct);

        written.ShouldBe(2);
        var rows = await ReadInstruments();
        rows.ShouldContain(x => string.Equals(x.Name, other.Instrument.Name, StringComparison.Ordinal));
        rows.Single(x => string.Equals(x.Name, "payments.count", StringComparison.Ordinal)).Meter.ShouldBe("first.meter");
        harness.Logger.Logs.ShouldNotContain(x => x.Level >= LogLevel.Warning);
    }

    [TimedFact]
    public async Task ExecuteAsync_IntervalElapses_WritesTheAggregatedPoints()
    {
        var harness = CreateHarness();
        var series = Series($"flush.{Guid.NewGuid():N}.loop", MetricKind.Counter, null, "-");
        harness.Aggregator.Record(series, 3);

        await harness.Flusher.StartAsync(Ct);
        try
        {
            // The loop arms a delay timer before every wait. Seeing the first one means it waits on the interval, and
            // seeing the second one means the flush that followed the elapsed interval has completed.
            (await harness.Time.TimerCreated.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue();
            harness.Time.Advance(new WarpConfiguration().MetricFlushInterval);
            (await harness.Time.TimerCreated.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue();

            var row = (await ReadPoints()).ShouldHaveSingleItem();
            row.Sum.ShouldBe(3);
            harness.Aggregator.PendingCount.ShouldBe(0);
        }
        finally
        {
            await harness.Flusher.StopAsync(Ct);
        }
    }

    [TimedFact]
    public async Task StopAsync_CancelledShutdownToken_AbandonsTheFinalFlushAndKeepsTheAggregates()
    {
        var harness = CreateHarness();
        var series = Series($"flush.{Guid.NewGuid():N}.cancelled", MetricKind.Counter, null, "-");
        harness.Aggregator.Record(series, 5);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await harness.Flusher.StopAsync(cts.Token);

        (await ReadPoints()).ShouldBeEmpty();
        harness.Aggregator.PendingCount.ShouldBe(1);
        harness.Logger.Logs.ShouldContain(x => x.Level == LogLevel.Warning);
    }

    [TimedFact]
    public async Task FlushOnce_SumOverflowedToInfinity_IsSkippedAndCountedAndTheOtherRowsAreWritten()
    {
        // Each measurement is finite; their sum is not. No provider stores infinity, so the row must never reach the
        // batch, where it would fail every instrument's flush.
        var harness = CreateHarness();
        var name = $"flush.{Guid.NewGuid():N}.overflow";
        var overflowing = Series(name, MetricKind.Counter, null, "-");
        var healthy = Series($"flush.{Guid.NewGuid():N}.healthy", MetricKind.Counter, null, "-");
        harness.Aggregator.Record(overflowing, double.MaxValue);
        harness.Aggregator.Record(overflowing, double.MaxValue);
        harness.Aggregator.Record(healthy, 3);

        var written = await harness.Flusher.FlushOnceAsync(Ct);

        written.ShouldBe(1);
        var row = (await ReadPoints()).ShouldHaveSingleItem();
        row.Instrument.ShouldBe(healthy.Instrument.Name);
        row.Sum.ShouldBe(3);
        harness.Aggregator.PendingCount.ShouldBe(0);
        var collapse = harness.Guard.Collapsed().ShouldHaveSingleItem();
        collapse.Instrument.ShouldBe(name);
        collapse.Reasons.ShouldBe([MetricCardinalityGuard.ReasonError]);
    }

    [TimedFact]
    public async Task FlushOnce_PoisonRowAmongGoodRows_WritesTheGoodRowsOnceAndDropsAndCountsThePoisonRow()
    {
        var harness = CreateHarness();
        var poison = $"flush.{Guid.NewGuid():N}.poison";
        var good = Enumerable.Range(0, 3)
            .Select(x => Series($"flush.{Guid.NewGuid():N}.good{x}", MetricKind.Counter, null, "-"))
            .ToList();
        harness.Aggregator.Record(Series(poison, MetricKind.Counter, null, "-"), 1);
        foreach (var series in good)
        {
            harness.Aggregator.Record(series, 2);
        }

        harness.Switch.PoisonInstrument = poison;

        var written = await harness.Flusher.FlushOnceAsync(Ct);

        written.ShouldBe(3);
        var rows = await ReadPoints();
        rows.Count.ShouldBe(3);
        rows.Select(x => x.Instrument).Order(StringComparer.Ordinal).ShouldBe(good.Select(x => x.Instrument.Name).Order(StringComparer.Ordinal));
        rows.Select(x => x.Count).ShouldAllBe(x => x == 1);
        rows.Select(x => x.Sum).ShouldBe([2d, 2d, 2d]);
        harness.Aggregator.PendingCount.ShouldBe(0);
        var collapse = harness.Guard.Collapsed().ShouldHaveSingleItem();
        collapse.Instrument.ShouldBe(poison);
        collapse.Reasons.ShouldBe([MetricCardinalityGuard.ReasonError]);
        harness.Logger.Logs.ShouldContain(x => x.Level == LogLevel.Warning && x.Message.Contains("refused by the database on their own", StringComparison.Ordinal));

        // Nothing is left to retry: the poison row is gone and the good rows are not written a second time.
        (await harness.Flusher.FlushOnceAsync(Ct)).ShouldBe(0);
        (await ReadPoints()).Count.ShouldBe(3);
    }

    [TimedFact]
    public async Task FlushOnce_EveryRowRefused_MergesBackInsteadOfDropping()
    {
        // Nothing accepted points at the database rather than at a row, so nothing is dropped as poison.
        var harness = CreateHarness();
        var name = $"flush.{Guid.NewGuid():N}.refused";
        var series = Series(name, MetricKind.Counter, null, "-");
        harness.Aggregator.Record(series, 5);
        harness.Switch.PoisonInstrument = name;

        var written = await harness.Flusher.FlushOnceAsync(Ct);

        written.ShouldBe(0);
        (await ReadPoints()).ShouldBeEmpty();
        harness.Aggregator.PendingCount.ShouldBe(1);
        harness.Guard.Collapsed().ShouldBeEmpty();

        harness.Switch.PoisonInstrument = null;
        (await harness.Flusher.FlushOnceAsync(Ct)).ShouldBe(1);
        (await ReadPoints()).ShouldHaveSingleItem().Sum.ShouldBe(5);
    }

    [TimedFact]
    public async Task FlushOnce_FailedWriteOverThePendingCap_DropsTheOldestBucketAndCountsIt()
    {
        var harness = CreateHarness(maxPendingBuckets: 2);
        var name = $"flush.{Guid.NewGuid():N}.capped";
        var series = Series(name, MetricKind.Counter, null, "-");
        harness.Aggregator.Record(series, 1);
        harness.Time.Advance(TimeSpan.FromMinutes(5));
        harness.Aggregator.Record(series, 2);
        harness.Time.Advance(TimeSpan.FromMinutes(5));
        harness.Aggregator.Record(series, 3);
        harness.Switch.FailPoints = true;

        (await harness.Flusher.FlushOnceAsync(Ct)).ShouldBe(0);

        harness.Aggregator.PendingCount.ShouldBe(2);
        harness.Logger.Logs.ShouldContain(x => x.Level == LogLevel.Warning && x.Message.Contains("MaxPendingBuckets", StringComparison.Ordinal));

        harness.Switch.FailPoints = false;
        (await harness.Flusher.FlushOnceAsync(Ct)).ShouldBe(2);

        // The oldest bucket (10:05, value 1) was the one dropped.
        var rows = await ReadPoints();
        rows.Select(x => x.Sum).Order().ShouldBe([2d, 3d]);
        rows.Min(x => x.BucketStart).ShouldBe(new DateTime(2026, 11, 13, 10, 10, 0, DateTimeKind.Utc));
    }

    private static MetricSeries Series(string name, MetricKind kind, double[]? bounds, string tags, string meter = "test.meter")
        => new(new MetricInstrumentInfo(name, meter, kind, "ms", "flush test instrument", bounds, false), null, tags);

    private async Task<List<MetricPoint>> ReadPoints() =>
        await _fixture.CreateContext()
            .Set<MetricPoint>()
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .ToListAsync(Ct);

    private async Task<List<MetricInstrument>> ReadInstruments() =>
        await _fixture.CreateContext()
            .Set<MetricInstrument>()
            .AsNoTracking()
            .ToListAsync(Ct);

    private Harness CreateHarness(
        Action<IServiceCollection>? registerSink = null,
        int maxPendingBuckets = MetricAggregator.DefaultMaxPendingBuckets,
        Action<WarpMetricsOptions>? configureOptions = null,
        IDatabaseExceptionClassifier? classifier = null)
    {
        var time = new SignalingTimeProvider(Now);
        var options = new WarpMetricsOptions();
        configureOptions?.Invoke(options);
        var guard = new MetricCardinalityGuard(options, time, NullLogger<MetricCardinalityGuard>.Instance);
        var aggregator = new MetricAggregator(time, 5, maxPendingBuckets);
        var collector = new WarpMeterCollector(options, aggregator, guard, null);
        var sinkSwitch = new SinkSwitch();
        var logs = new CapturingLoggerProvider();

        var services = new ServiceCollection();
        if (classifier is not null)
        {
            services.AddSingleton(classifier);
        }

        if (registerSink is null)
        {
            services.AddScoped<IMetricPointSink>(_ => new SwitchableSink(_fixture.CreateContext(), sinkSwitch));
        }
        else
        {
            registerSink(services);
            services.AddSingleton(sinkSwitch);
        }

        var provider = services.BuildServiceProvider();
        var flusher = new MetricFlusher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            collector,
            aggregator,
            guard,
            time,
            Options.Create(new WarpConfiguration()),
            new CapturingLoggerFactory(logs).CreateLogger<MetricFlusher>());

        return new Harness(time, guard, aggregator, collector, flusher, sinkSwitch, logs);
    }

    private sealed record Harness(
        SignalingTimeProvider Time,
        MetricCardinalityGuard Guard,
        MetricAggregator Aggregator,
        WarpMeterCollector Collector,
        MetricFlusher Flusher,
        SinkSwitch Switch,
        CapturingLoggerProvider Logger);

    private sealed class SinkSwitch
    {
        public bool FailPoints { get; set; }

        public bool FailRegistry { get; set; }

        /// <summary>Every registry save fails like a primary-key conflict with another process.</summary>
        public bool ConflictRegistry { get; set; }

        public Func<CancellationToken, Task>? BeforeRegistrySave { get; set; }

        /// <summary>Every point write that includes this instrument is refused like a constraint violation.</summary>
        public string? PoisonInstrument { get; set; }

        public int PointWrites { get; set; }

        public int SinkResolutions { get; set; }
    }

    // Classifies every registry rejection one way, as a provider package's classifier would.
    private sealed class FixedClassifier : IDatabaseExceptionClassifier
    {
        private readonly bool _isUnique;

        public FixedClassifier(bool isUnique) => _isUnique = isUnique;

        public bool IsUniqueConstraintViolation(DbUpdateException ex) => _isUnique;

        public bool IsTransientDeadlock(Exception ex) => false;
    }

    private sealed class SwitchableSink : IMetricPointSink
    {
        private readonly TestContext _context;
        private readonly SinkSwitch _switch;

        public SwitchableSink(TestContext context, SinkSwitch sinkSwitch)
        {
            _context = context;
            _switch = sinkSwitch;
            _switch.SinkResolutions++;
        }

        public Task WritePointsAsync(IReadOnlyList<MetricPoint> points, CancellationToken ct)
        {
            if (_switch.FailPoints)
            {
                throw new InvalidOperationException("injected points failure for customer-top-secret");
            }

            _switch.PointWrites++;
            if (_switch.PoisonInstrument is { } poison && points.Any(x => string.Equals(x.Instrument, poison, StringComparison.Ordinal)))
            {
                throw new DbUpdateException("injected poison row");
            }

            return MetricSinkWriter.WritePointsAsync(_context, points, ct);
        }

        public Task UpsertInstrumentsAsync(IReadOnlyList<MetricInstrument> rows, CancellationToken ct)
        {
            if (_switch.FailRegistry)
            {
                throw new InvalidOperationException("injected registry failure");
            }

            if (_switch.ConflictRegistry)
            {
                throw new DbUpdateException("injected registry conflict");
            }

            return MetricSinkWriter.UpsertInstrumentsAsync(_context, rows, ct, _switch.BeforeRegistrySave);
        }
    }

    // Signals each timer the flusher's loop arms, so a test advances the clock only once the loop is waiting on it.
    private sealed class SignalingTimeProvider : FakeTimeProvider
    {
        public SignalingTimeProvider(DateTimeOffset start)
            : base(start)
        {
        }

        public SemaphoreSlim TimerCreated { get; } = new(0);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            TimerCreated.Release();

            return timer;
        }
    }
}
