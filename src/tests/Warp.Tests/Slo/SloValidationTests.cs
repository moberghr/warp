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
}
