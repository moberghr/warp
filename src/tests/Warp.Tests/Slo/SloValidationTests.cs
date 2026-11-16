using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Slo;

namespace Warp.Tests.Slo;

/// <summary>
/// NoDb coverage for <see cref="SloValidation"/> (§8.31) — the boundary guard shared by the upsert endpoint and the
/// config seeder. Without it, an out-of-range target/window silently breaks evaluation (a rate target &gt; 1 pins
/// Breaching forever; a window ≤ 0 pins Healthy). Also checks the normalization: latency kinds get an explicit
/// default percentile, non-latency kinds have theirs cleared.
/// </summary>
[Trait("Category", "NoDb")]
public class SloValidationTests
{
    private static SloDefinition Def(SloKind kind, double target, int windowSeconds = 3600, int? percentile = null, string name = "obj", string dimension = "dim")
        => new() { Name = name, Kind = kind, Dimension = dimension, TargetValue = target, WindowSeconds = windowSeconds, Percentile = percentile };

    [Fact]
    public void ValidRateObjective_Passes()
    {
        SloValidation.TryValidate(Def(SloKind.SuccessRate, 0.995), out var error).ShouldBeTrue();
        error.ShouldBeNull();
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    public void RateTargetOutsideOpenUnitInterval_Rejected(double target)
    {
        SloValidation.TryValidate(Def(SloKind.SuccessRate, target), out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void DeadlineAttainment_UsesRateRules()
    {
        SloValidation.TryValidate(Def(SloKind.DeadlineAttainment, 0.99), out _).ShouldBeTrue();
        SloValidation.TryValidate(Def(SloKind.DeadlineAttainment, 2.0), out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositiveWindow_Rejected(int windowSeconds)
    {
        SloValidation.TryValidate(Def(SloKind.SuccessRate, 0.99, windowSeconds: windowSeconds), out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void UndefinedKind_Rejected()
    {
        SloValidation.TryValidate(Def((SloKind)99, 0.5), out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void EmptyNameOrDimension_Rejected()
    {
        SloValidation.TryValidate(Def(SloKind.SuccessRate, 0.99, name: "  "), out _).ShouldBeFalse();
        SloValidation.TryValidate(Def(SloKind.SuccessRate, 0.99, dimension: string.Empty), out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(SloKind.QueueWaitLatency)]
    [InlineData(SloKind.ExecutionLatency)]
    [InlineData(SloKind.BacklogDepth)]
    public void ThresholdKind_NonPositiveTarget_Rejected(SloKind kind)
    {
        SloValidation.TryValidate(Def(kind, 0), out _).ShouldBeFalse();
        SloValidation.TryValidate(Def(kind, 30_000), out _).ShouldBeTrue();
    }

    [Fact]
    public void LatencyKind_NullPercentile_NormalizedToDefault()
    {
        var def = Def(SloKind.ExecutionLatency, 30_000, percentile: null);

        SloValidation.TryValidate(def, out _).ShouldBeTrue();
        def.Percentile.ShouldBe(SloValidation.DefaultLatencyPercentile);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void LatencyKind_PercentileOutOfRange_Rejected(int percentile)
    {
        SloValidation.TryValidate(Def(SloKind.QueueWaitLatency, 30_000, percentile: percentile), out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void NonLatencyKind_PercentileCleared()
    {
        var def = Def(SloKind.SuccessRate, 0.99, percentile: 95);

        SloValidation.TryValidate(def, out _).ShouldBeTrue();
        def.Percentile.ShouldBeNull();
    }

    private static SloDefinition Ratio(double target = 0.99, int window = 3600, int? baselineDays = null, string good = "ok", string bad = "error")
        => new()
        {
            Name = "r",
            Kind = SloKind.MetricRatio,
            Dimension = "orders.processed",
            TargetValue = target,
            WindowSeconds = window,
            RatioTag = "outcome",
            GoodValues = good,
            BadValues = bad,
            BaselineDays = baselineDays,
        };

    private static SloDefinition Metric(SloKind kind, double target, int window = 3600)
        => new() { Name = "m", Kind = kind, Dimension = "orders.processed", TargetValue = target, WindowSeconds = window };

    [Fact]
    public void TryValidate_MetricRatioValid_PassesAndClearsPercentile()
    {
        var def = Ratio();
        def.Percentile = 95;

        SloValidation.TryValidate(def, out var error).ShouldBeTrue();
        error.ShouldBeNull();
        def.Percentile.ShouldBeNull();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-0.5)]
    [InlineData(1.5)]
    public void TryValidate_MetricRatioTargetOutsideOpenUnitInterval_Rejected(double target)
    {
        SloValidation.TryValidate(Ratio(target), out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void TryValidate_MetricRatioMissingRatioTag_Rejected()
    {
        var def = Ratio();
        def.RatioTag = " ";

        SloValidation.TryValidate(def, out var error).ShouldBeFalse();
        error.ShouldNotBeNull().ShouldContain("RatioTag");
    }

    [Theory]
    [InlineData(null, "error")]
    [InlineData("ok", null)]
    [InlineData("", "error")]
    public void TryValidate_MetricRatioEmptyGoodOrBad_Rejected(string? good, string? bad)
    {
        var def = Ratio();
        def.GoodValues = good;
        def.BadValues = bad;

        SloValidation.TryValidate(def, out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void TryValidate_MetricRatioSameLiteralInBothSets_Rejected()
    {
        SloValidation.TryValidate(Ratio(good: "ok,retry", bad: "error,retry"), out var error).ShouldBeFalse();
        error.ShouldNotBeNull().ShouldContain("must not overlap");
    }

    [Theory]
    [InlineData("200,500", "5*")] // a literal under the other side's prefix
    [InlineData("2*", "200")]
    [InlineData("5*", "50*")] // one prefix extends the other
    [InlineData("50*", "5*")]
    [InlineData("*", "error")] // a bare * overlaps anything
    [InlineData("ok", "*")]
    public void TryValidate_MetricRatioOverlappingGoodAndBad_Rejected(string good, string bad)
    {
        SloValidation.TryValidate(Ratio(good: good, bad: bad), out var error).ShouldBeFalse();
        error.ShouldNotBeNull().ShouldContain("must not overlap");
    }

    [Fact]
    public void TryValidate_MetricRatioDisjointPrefixes_Passes()
    {
        SloValidation.TryValidate(Ratio(good: "2*,3*", bad: "5*"), out var error).ShouldBeTrue();
        error.ShouldBeNull();
    }

    [Fact]
    public void TryValidate_MetricRatioStarNotAtEnd_Rejected()
    {
        SloValidation.TryValidate(Ratio(good: "o*k"), out var error).ShouldBeFalse();
        error.ShouldNotBeNull().ShouldContain("GoodValues");
    }

    [Fact]
    public void TryValidate_MetricRatioBaselineDays_AcceptsOneAndSevenOnly()
    {
        SloValidation.TryValidate(Ratio(0.05, baselineDays: 1), out _).ShouldBeTrue();
        SloValidation.TryValidate(Ratio(0.05, baselineDays: 7), out _).ShouldBeTrue();
        SloValidation.TryValidate(Ratio(0.05, baselineDays: 3), out var error).ShouldBeFalse();
        error.ShouldNotBeNull().ShouldContain("BaselineDays");
    }

    [Fact]
    public void TryValidate_MetricRatioWithBaseline_RequiresMaxDropInOpenUnitInterval()
    {
        SloValidation.TryValidate(Ratio(0.05, baselineDays: 1), out _).ShouldBeTrue();
        SloValidation.TryValidate(Ratio(0.0, baselineDays: 1), out _).ShouldBeFalse();
        SloValidation.TryValidate(Ratio(1.0, baselineDays: 1), out _).ShouldBeFalse();
    }

    [Fact]
    public void TryValidate_MetricThresholdTarget_AllowsZeroRejectsNegative()
    {
        SloValidation.TryValidate(Metric(SloKind.MetricThreshold, 0), out var error).ShouldBeTrue();
        error.ShouldBeNull();
        SloValidation.TryValidate(Metric(SloKind.MetricThreshold, 25), out _).ShouldBeTrue();
        SloValidation.TryValidate(Metric(SloKind.MetricThreshold, -1), out _).ShouldBeFalse();
        SloValidation.TryValidate(Metric(SloKind.MetricThreshold, double.NaN), out _).ShouldBeFalse();
    }

    [Fact]
    public void TryValidate_ExistingThresholdKindsZeroTarget_Rejected()
    {
        SloValidation.TryValidate(Def(SloKind.BacklogDepth, 0), out _).ShouldBeFalse();
        SloValidation.TryValidate(Def(SloKind.ExecutionLatency, 0), out _).ShouldBeFalse();
        SloValidation.TryValidate(Def(SloKind.QueueWaitLatency, 0), out _).ShouldBeFalse();
    }

    [Fact]
    public void TryValidate_MetricGapWithoutSecondaryMetric_Rejected()
    {
        var def = Metric(SloKind.MetricGap, 0);

        SloValidation.TryValidate(def, out var error).ShouldBeFalse();
        error.ShouldNotBeNull().ShouldContain("SecondaryMetric");

        def.SecondaryMetric = "orders.finished";
        SloValidation.TryValidate(def, out _).ShouldBeTrue();
        SloValidation.TryValidate(Metric(SloKind.MetricGap, -1), out _).ShouldBeFalse();
    }

    [Fact]
    public void TryValidate_MetricVolumeFloor_RequiresBaselineOneOrSevenAndRatioTarget()
    {
        var def = Metric(SloKind.MetricVolumeFloor, 0.5);

        SloValidation.TryValidate(def, out var error).ShouldBeFalse();
        error.ShouldNotBeNull().ShouldContain("BaselineDays");

        def.BaselineDays = 3;
        SloValidation.TryValidate(def, out _).ShouldBeFalse();

        def.BaselineDays = 1;
        SloValidation.TryValidate(def, out _).ShouldBeTrue();

        def.BaselineDays = 7;
        SloValidation.TryValidate(def, out _).ShouldBeTrue();

        def.TargetValue = 0;
        SloValidation.TryValidate(def, out _).ShouldBeFalse();
        def.TargetValue = 1;
        SloValidation.TryValidate(def, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(599)]
    [InlineData(60)]
    [InlineData(1)]
    public void TryValidate_MetricKindsWindowBelow600_Rejected(int window)
    {
        SloValidation.TryValidate(Metric(SloKind.MetricThreshold, 0, window), out var error).ShouldBeFalse();
        error.ShouldNotBeNull().ShouldContain("600");
    }

    [Fact]
    public void TryValidate_MetricKindsWindow600_Passes()
    {
        SloValidation.TryValidate(Metric(SloKind.MetricThreshold, 0, 600), out _).ShouldBeTrue();
    }

    [Fact]
    public void TryValidate_ExistingKindsShortWindow_Passes()
    {
        SloValidation.TryValidate(Def(SloKind.SuccessRate, 0.99, windowSeconds: 60), out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryValidate_MetricKindsBlankInstrument_Rejected(string dimension)
    {
        var def = Metric(SloKind.MetricThreshold, 0);
        def.Dimension = dimension;

        SloValidation.TryValidate(def, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryValidate_MetricKindsInstrumentNameOver128_Rejected()
    {
        var def = Metric(SloKind.MetricThreshold, 0);
        def.Dimension = new string('a', 129);

        SloValidation.TryValidate(def, out var error).ShouldBeFalse();
        error.ShouldNotBeNull().ShouldContain("128");

        def.Dimension = new string('a', 128);
        SloValidation.TryValidate(def, out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("status")]
    [InlineData("status=5xx,,route=/a")]
    [InlineData("=5")]
    [InlineData("a=1,a=2")]
    [InlineData("a=x*y")]
    public void TryValidate_MetricKindsInvalidTagFilter_Rejected(string filter)
    {
        var def = Metric(SloKind.MetricThreshold, 0);
        def.TagFilter = filter;

        SloValidation.TryValidate(def, out var error).ShouldBeFalse();
        error.ShouldNotBeNull().ShouldContain("TagFilter");
    }

    [Fact]
    public void TryValidate_MetricGapInvalidSecondaryTagFilter_Rejected()
    {
        var def = Metric(SloKind.MetricGap, 0);
        def.SecondaryMetric = "orders.finished";
        def.SecondaryTagFilter = "a";

        SloValidation.TryValidate(def, out var error).ShouldBeFalse();
        error.ShouldNotBeNull().ShouldContain("SecondaryTagFilter");

        def.SecondaryTagFilter = "result=ok*";
        def.TagFilter = "kind=a,site=eu*";
        SloValidation.TryValidate(def, out _).ShouldBeTrue();
    }

    [Fact]
    public void TryValidate_MetricKindsFieldsAboveColumnLimits_Rejected()
    {
        var def = Metric(SloKind.MetricThreshold, 0);
        def.TagFilter = "k=" + new string('v', 511);

        SloValidation.TryValidate(def, out var error).ShouldBeFalse();
        error.ShouldNotBeNull().ShouldContain("512");
    }

    [Fact]
    public void TryValidate_SevenDayBaselineRetentionBelowThreshold_RejectedNamingSetting()
    {
        var def = Ratio(0.05, window: 3600, baselineDays: 7);
        var required = TimeSpan.FromDays(7) + TimeSpan.FromHours(1) + TimeSpan.FromHours(1);

        SloValidation.TryValidate(def, required - TimeSpan.FromSeconds(1), null, out var error).ShouldBeFalse();
        error.ShouldNotBeNull().ShouldContain("HourlyStatisticsRetention");
    }

    [Fact]
    public void TryValidate_SevenDayBaselineRetentionAtThreshold_Passes()
    {
        var def = Ratio(0.05, window: 3600, baselineDays: 7);
        var required = TimeSpan.FromDays(7) + TimeSpan.FromHours(1) + TimeSpan.FromHours(1);

        SloValidation.TryValidate(def, required, null, out var error).ShouldBeTrue();
        error.ShouldBeNull();
    }

    [Fact]
    public void TryValidate_VolumeFloorSevenDayBaselineShortRetention_Rejected()
    {
        var def = Metric(SloKind.MetricVolumeFloor, 0.5, 600);
        def.BaselineDays = 7;

        SloValidation.TryValidate(def, TimeSpan.FromDays(7), null, out var error).ShouldBeFalse();
        error.ShouldNotBeNull().ShouldContain("HourlyStatisticsRetention");
        SloValidation.TryValidate(def, TimeSpan.FromDays(8), null, out _).ShouldBeTrue();
    }

    [Fact]
    public void TryValidate_OneDayBaselineShortRetention_Passes()
    {
        var def = Ratio(0.05, baselineDays: 1);

        SloValidation.TryValidate(def, TimeSpan.FromHours(1), null, out _).ShouldBeTrue();
    }

    [Fact]
    public void TryValidate_SevenDayBaselineWithoutConfiguration_SkipsRetentionCheck()
    {
        SloValidation.TryValidate(Ratio(0.05, baselineDays: 7), out _).ShouldBeTrue();
    }

    [Fact]
    public void TryValidate_MetricKindsOnGaugeInstrument_Rejected()
    {
        foreach (var kind in new[] { SloKind.MetricThreshold, SloKind.MetricGap, SloKind.MetricRatio, SloKind.MetricVolumeFloor })
        {
            var def = kind switch
            {
                SloKind.MetricRatio => Ratio(),
                SloKind.MetricGap => new SloDefinition { Name = "g", Kind = kind, Dimension = "i", TargetValue = 0.5, WindowSeconds = 3600, SecondaryMetric = "j" },
                SloKind.MetricVolumeFloor => new SloDefinition { Name = "v", Kind = kind, Dimension = "i", TargetValue = 0.5, WindowSeconds = 3600, BaselineDays = 1 },
                _ => Metric(kind, 5),
            };

            SloValidation.TryValidate(def, null, MetricKind.Gauge, out var error).ShouldBeFalse(kind.ToString());
            error.ShouldNotBeNull().ShouldContain("gauge");
        }
    }

    [Fact]
    public void TryValidate_MetricGapOnGaugeSecondaryInstrument_Rejected()
    {
        var def = new SloDefinition { Name = "g", Kind = SloKind.MetricGap, Dimension = "i", TargetValue = 0, WindowSeconds = 3600, SecondaryMetric = "j" };

        SloValidation.TryValidate(def, null, MetricKind.Counter, MetricKind.Gauge, out var error).ShouldBeFalse();
        error.ShouldNotBeNull().ShouldContain("SecondaryMetric");
        SloValidation.TryValidate(def, null, MetricKind.Counter, MetricKind.Histogram, out _).ShouldBeTrue();
        SloValidation.TryValidate(def, null, MetricKind.Counter, null, out _).ShouldBeTrue();
    }

    [Fact]
    public void TryValidate_NonGapKindWithGaugeSecondaryKind_IgnoresIt()
    {
        SloValidation.TryValidate(Metric(SloKind.MetricThreshold, 0), null, MetricKind.Counter, MetricKind.Gauge, out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(MetricKind.Counter)]
    [InlineData(MetricKind.UpDownCounter)]
    [InlineData(MetricKind.Histogram)]
    public void TryValidate_NonGaugeOrUnknownInstrumentKind_Passes(MetricKind? kind)
    {
        SloValidation.TryValidate(Metric(SloKind.MetricThreshold, 0), null, kind, out _).ShouldBeTrue();
    }

    [Fact]
    public void TryValidate_ExistingKindsWithGaugeAndRetentionHooks_IgnoresThem()
    {
        SloValidation.TryValidate(Def(SloKind.SuccessRate, 0.99), TimeSpan.Zero, MetricKind.Gauge, out _).ShouldBeTrue();
    }

    [Fact]
    public void TryValidate_MetricKinds_ClearsFieldsOfOtherKinds()
    {
        var def = Metric(SloKind.MetricThreshold, 0);
        def.RatioTag = "x";
        def.GoodValues = "a";
        def.BadValues = "b";
        def.SecondaryMetric = "s";
        def.BaselineDays = 7;

        SloValidation.TryValidate(def, out _).ShouldBeTrue();
        def.RatioTag.ShouldBeNull();
        def.GoodValues.ShouldBeNull();
        def.BadValues.ShouldBeNull();
        def.SecondaryMetric.ShouldBeNull();
        def.BaselineDays.ShouldBeNull();
    }

    [Fact]
    public void TryValidate_MetricThresholdWithStraySevenDayBaselineAndShortRetention_PassesAndClearsIt()
    {
        // R29 runs after normalisation: a threshold never reads a baseline, so its stray BaselineDays = 7 is cleared
        // instead of failing a retention check it has nothing to do with.
        var def = Metric(SloKind.MetricThreshold, 0);
        def.BaselineDays = 7;

        SloValidation.TryValidate(def, TimeSpan.FromDays(1), null, out var error).ShouldBeTrue();
        error.ShouldBeNull();
        def.BaselineDays.ShouldBeNull();
    }

    [Theory]
    [InlineData(SloKind.MetricRatio, true)]
    [InlineData(SloKind.MetricThreshold, true)]
    [InlineData(SloKind.MetricVolumeFloor, true)]
    [InlineData(SloKind.MetricGap, true)]
    [InlineData(SloKind.MetricLatency, true)]
    [InlineData(SloKind.SuccessRate, false)]
    [InlineData(SloKind.QueueWaitLatency, false)]
    [InlineData(SloKind.ExecutionLatency, false)]
    [InlineData(SloKind.BacklogDepth, false)]
    [InlineData(SloKind.DeadlineAttainment, false)]
    public void IsMetricKind_EachKind_ClassifiesIt(SloKind kind, bool expected)
    {
        SloValidation.IsMetricKind(kind).ShouldBe(expected);
    }

    [Fact]
    public void SloKind_Values_AreAppendOnly()
    {
        ((int)SloKind.DeadlineAttainment).ShouldBe(5);
        ((int)SloKind.MetricRatio).ShouldBe(6);
        ((int)SloKind.MetricThreshold).ShouldBe(7);
        ((int)SloKind.MetricVolumeFloor).ShouldBe(8);
        ((int)SloKind.MetricGap).ShouldBe(9);
        ((int)SloKind.MetricLatency).ShouldBe(10);
    }

    [Theory]
    [InlineData(MetricKind.Histogram, true)]
    [InlineData(null, true)]
    [InlineData(MetricKind.Counter, false)]
    [InlineData(MetricKind.UpDownCounter, false)]
    [InlineData(MetricKind.Gauge, false)]
    public void TryValidate_MetricLatency_AcceptsOnlyAHistogramOrAnUnknownInstrument(MetricKind? instrument, bool expected)
    {
        var def = Metric(SloKind.MetricLatency, 0.5);

        SloValidation.TryValidate(def, null, instrument, out var error).ShouldBe(expected, error);
        if (!expected)
        {
            error.ShouldNotBeNull().ShouldContain("histogram");
        }
    }

    [Fact]
    public void TryValidate_MetricLatencyWithoutPercentile_DefaultsTo95AndKeepsIt()
    {
        var def = Metric(SloKind.MetricLatency, 0.25);

        SloValidation.TryValidate(def, out _).ShouldBeTrue();
        def.Percentile.ShouldBe(95);

        var p99 = Metric(SloKind.MetricLatency, 0.25);
        p99.Percentile = 99;
        SloValidation.TryValidate(p99, out _).ShouldBeTrue();
        p99.Percentile.ShouldBe(99);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(-5)]
    public void TryValidate_MetricLatencyPercentileOutOfRange_Rejected(int percentile)
    {
        var def = Metric(SloKind.MetricLatency, 0.25);
        def.Percentile = percentile;

        SloValidation.TryValidate(def, out var error).ShouldBeFalse();
        error.ShouldNotBeNull().ShouldContain("Percentile");
    }

    [Fact]
    public void TryValidate_MetricLatencyThreshold_AllowsZeroRejectsNegativeAndNonFinite()
    {
        SloValidation.TryValidate(Metric(SloKind.MetricLatency, 0), out _).ShouldBeTrue();
        SloValidation.TryValidate(Metric(SloKind.MetricLatency, -0.1), out _).ShouldBeFalse();
        SloValidation.TryValidate(Metric(SloKind.MetricLatency, double.NaN), out _).ShouldBeFalse();
    }

    [Fact]
    public void TryValidate_MetricLatency_ClearsFieldsOfOtherKindsButKeepsTheTagFilter()
    {
        var def = Metric(SloKind.MetricLatency, 0.5);
        def.TagFilter = "http.route=/api/x";
        def.RatioTag = "x";
        def.SecondaryMetric = "s";
        def.BaselineDays = 1;

        SloValidation.TryValidate(def, out _).ShouldBeTrue();
        def.TagFilter.ShouldBe("http.route=/api/x");
        def.RatioTag.ShouldBeNull();
        def.SecondaryMetric.ShouldBeNull();
        def.BaselineDays.ShouldBeNull();
    }

    [Theory]
    [InlineData(SloKind.MetricThreshold)]
    [InlineData(SloKind.MetricGap)]
    public void TryValidate_EventKindOnHistogram_StillPasses(SloKind kind)
    {
        var def = Metric(kind, 5);
        def.SecondaryMetric = kind == SloKind.MetricGap ? "j" : null;

        SloValidation.TryValidate(def, null, MetricKind.Histogram, out _).ShouldBeTrue();
    }

    [Fact]
    public void TryValidate_ExistingKinds_KeepTheirRules()
    {
        // SuccessRate / DeadlineAttainment: ratio in (0,1), percentile cleared.
        var rate = Def(SloKind.SuccessRate, 0.99, percentile: 90);
        SloValidation.TryValidate(rate, out _).ShouldBeTrue();
        rate.Percentile.ShouldBeNull();
        SloValidation.TryValidate(Def(SloKind.DeadlineAttainment, 1.0), out _).ShouldBeFalse();

        // Latency: default percentile 95, 1..99 enforced, target > 0.
        var latency = Def(SloKind.ExecutionLatency, 500);
        SloValidation.TryValidate(latency, out _).ShouldBeTrue();
        latency.Percentile.ShouldBe(95);
        SloValidation.TryValidate(Def(SloKind.QueueWaitLatency, 500, percentile: 100), out _).ShouldBeFalse();

        // Backlog: target > 0, window > 0, no 600 floor.
        SloValidation.TryValidate(Def(SloKind.BacklogDepth, 100, windowSeconds: 30), out _).ShouldBeTrue();
        SloValidation.TryValidate(Def(SloKind.BacklogDepth, 100, windowSeconds: 0), out _).ShouldBeFalse();
        SloValidation.TryValidate(Def(SloKind.BacklogDepth, 100, dimension: " "), out _).ShouldBeFalse();
        SloValidation.TryValidate(Def(SloKind.BacklogDepth, 100, name: " "), out _).ShouldBeFalse();
        SloValidation.TryValidate(Def((SloKind)99, 1), out _).ShouldBeFalse();
    }
}
