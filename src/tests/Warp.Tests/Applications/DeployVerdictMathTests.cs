using Shouldly;
using Warp.Core.Enums;
using Warp.Core.Models;
using Warp.Core.Services;

namespace Warp.Tests.Applications;

/// <summary>
/// NoDb coverage for the post-deploy verdict's pure half: deploy resolution (latest vs explicit version, environment,
/// previous / next version), the window and its settle rule, application scoping, tile and SLO checks (acknowledged, stale
/// SLO evaluation, no rule) and the aggregation.
/// </summary>
[Trait("Category", "NoDb")]
public class DeployVerdictMathTests
{
    private static readonly DateTime Now = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    [TimedTheory]
    [InlineData(new SloState[0], true, DeployVerdicts.NoData)]
    [InlineData(new[] { SloState.NoData, SloState.NoData }, true, DeployVerdicts.NoData)]
    [InlineData(new[] { SloState.Healthy, SloState.NoData }, true, DeployVerdicts.Pass)]
    [InlineData(new[] { SloState.Acknowledged, SloState.NoData }, true, DeployVerdicts.Pass)]
    [InlineData(new[] { SloState.Healthy, SloState.Warning, SloState.Acknowledged }, true, DeployVerdicts.Warn)]
    [InlineData(new[] { SloState.Warning, SloState.Breaching, SloState.Healthy }, true, DeployVerdicts.Fail)]
    [InlineData(new[] { SloState.Breaching }, false, DeployVerdicts.Pending)]
    [InlineData(new SloState[0], false, DeployVerdicts.Pending)]
    public void Aggregate_States_GiveTheVerdict(SloState[] states, bool settled, string expected)
    {
        DeployVerdictMath.Aggregate(states, settled).ShouldBe(expected);
    }

    [TimedFact]
    public void Resolve_NoVersion_TakesTheLatestDeployAndItsPredecessor()
    {
        var resolved = DeployVerdictMath.Resolve([Deploy("1.0.0", -3), Deploy("2.0.0", -1), Deploy("1.5.0", -2)], null, null);

        resolved.ShouldNotBeNull();
        resolved.Deploy.Version.ShouldBe("2.0.0");
        resolved.Previous?.Version.ShouldBe("1.5.0");
        resolved.Next.ShouldBeNull();
    }

    [TimedFact]
    public void Resolve_ExplicitOlderVersion_HasBothNeighbours()
    {
        var resolved = DeployVerdictMath.Resolve([Deploy("1.0.0", -3), Deploy("2.0.0", -1), Deploy("1.5.0", -2)], "1.5.0", null);

        resolved.ShouldNotBeNull();
        resolved.Previous?.Version.ShouldBe("1.0.0");
        resolved.Next?.Version.ShouldBe("2.0.0");
    }

    [TimedFact]
    public void Resolve_Environment_IgnoresOtherEnvironmentsForTheDeployAndItsNeighbours()
    {
        var deploys = new[]
        {
            Deploy("1.0.0", -5, "prod"),
            Deploy("2.0.0", -3, "staging"),
            Deploy("2.0.0", -2, "prod"),
            Deploy("3.0.0", -1, "staging"),
        };

        var resolved = DeployVerdictMath.Resolve(deploys, null, "prod");

        resolved.ShouldNotBeNull();
        resolved.Deploy.FirstSeenAt.ShouldBe(Now.AddHours(-2));
        resolved.Previous?.Version.ShouldBe("1.0.0");
        resolved.Next.ShouldBeNull();
    }

    [TimedFact]
    public void Resolve_VersionInTwoEnvironmentsAndNoEnvironmentGiven_TakesTheNewest()
    {
        var resolved = DeployVerdictMath.Resolve([Deploy("2.0.0", -3, "staging"), Deploy("2.0.0", -2, "prod")], "2.0.0", null);

        resolved?.Deploy.Environment.ShouldBe("prod");
    }

    [TimedTheory]
    [InlineData("9.9.9", null)]
    [InlineData(null, "qa")]
    [InlineData("2.0.0", "staging")]
    public void Resolve_NothingMatches_ReturnsNull(string? version, string? environment)
    {
        DeployVerdictMath.Resolve([Deploy("2.0.0", -1, "prod")], version, environment).ShouldBeNull();
    }

    [TimedFact]
    public void Resolve_EmptyHistory_ReturnsNull()
    {
        DeployVerdictMath.Resolve([], null, null).ShouldBeNull();
    }

    [TimedTheory]
    [InlineData(10, 5, true)]
    [InlineData(5, 5, true)]
    [InlineData(4, 5, false)]
    [InlineData(0, 0, true)]
    public void Window_DeployAge_SettlesAtTheMinimum(int ageMinutes, int minMinutes, bool settled)
    {
        var deployedAt = Now.AddMinutes(-ageMinutes);

        var window = DeployVerdictMath.Window(deployedAt, null, Now, TimeSpan.FromMinutes(minMinutes));

        window.From.ShouldBe(deployedAt);
        window.To.ShouldBe(Now);
        window.Settled.ShouldBe(settled);
        window.SettlesAt.ShouldBe(deployedAt.AddMinutes(minMinutes));
        window.MinWindowSeconds.ShouldBe(minMinutes * 60);
    }

    [TimedFact]
    public void Window_Superseded_EndsAtTheNextDeploy()
    {
        var window = DeployVerdictMath.Window(Now.AddHours(-3), Now.AddHours(-1), Now, TimeSpan.FromMinutes(5));

        window.To.ShouldBe(Now.AddHours(-1));
        window.Settled.ShouldBeTrue();
    }

    [TimedFact]
    public void Window_OlderThanTheReadLimit_StartsAtTheLimit()
    {
        var window = DeployVerdictMath.Window(Now.AddDays(-200), null, Now, TimeSpan.FromMinutes(5));

        window.From.ShouldBe(Now.AddDays(-IMetricQueryService.MaxRangeDays));
    }

    [TimedFact]
    public void Window_DeployInTheFuture_IsEmptyAndPending()
    {
        var window = DeployVerdictMath.Window(Now.AddMinutes(1), null, Now, TimeSpan.FromMinutes(5));

        window.To.ShouldBe(window.From);
        window.Settled.ShouldBeFalse();
    }

    [TimedTheory]
    [InlineData(null, "api", true)]
    [InlineData("", "api", true)]
    [InlineData("api", "api", true)]
    [InlineData(" api ", "api", true)]
    [InlineData("admin", "api", false)]
    [InlineData("API", "api", false)]
    public void AppliesTo_Scope_MatchesUnscopedAndTheSameApplication(string? scope, string application, bool expected)
    {
        DeployVerdictMath.AppliesTo(scope, application).ShouldBe(expected);
    }

    [TimedFact]
    public void TileCheck_NoRule_IsNotACheck()
    {
        DeployVerdictMath.TileCheck(TileResult("Volume", null, current: 10)).ShouldBeNull();
    }

    [TimedFact]
    public void TileCheck_BreachingThreshold_CarriesValueTargetAndReason()
    {
        var check = DeployVerdictMath.TileCheck(TileResult("5xx", SloState.Breaching, current: 12, threshold: 5));

        check.ShouldNotBeNull();
        check.Source.ShouldBe(DeployVerdictSources.Tile);
        check.State.ShouldBe("breaching");
        check.Acknowledged.ShouldBeFalse();
        check.Value.ShouldBe(12);
        check.Target.ShouldBe(5);
        check.Baseline.ShouldBeNull();
        check.Reason.ShouldBe("Breaching: 12 vs at most 5.");
    }

    [TimedFact]
    public void TileCheck_Acknowledged_KeepsTheRuleStateAndIsMarked()
    {
        var check = DeployVerdictMath.TileCheck(TileResult("5xx", SloState.Acknowledged, unacknowledged: SloState.Breaching, current: 12, threshold: 5));

        check.ShouldNotBeNull();
        check.State.ShouldBe("acknowledged");
        check.UnacknowledgedState.ShouldBe("breaching");
        check.Acknowledged.ShouldBeTrue();
        check.Reason.ShouldStartWith("Acknowledged noise (would be breaching)");
    }

    [TimedFact]
    public void TileCheck_Deviation_UsesTheConfiguredBaseline()
    {
        var result = new MetricBoardTileResult
        {
            Tile = new MetricBoardTileModel
            {
                Id = 3,
                Name = "Success",
                Kind = MetricTileKind.Ratio,
                StatusRule = MetricTileStatusRule.Deviation,
                BaselineDays = 1,
                MaxDeviation = 0.05,
                DeviationDirection = MetricDeviationDirection.Drop,
            },
            InstrumentKind = MetricKind.Counter,
            Current = 0.9,
            DayAgo = 0.99,
            WeekAgo = 0.5,
            Status = SloState.Breaching,
            UnacknowledgedStatus = SloState.Breaching,
        };

        var check = DeployVerdictMath.TileCheck(result);

        check.ShouldNotBeNull();
        check.Baseline.ShouldBe(0.99);
        check.Target.ShouldBe(0.05);
        check.Reason.ShouldBe("Breaching: 0.9 vs 1-day baseline 0.99 (max drop 0.05).");
    }

    [TimedFact]
    public void TileCheck_UnknownInstrument_IsNoDataWithTheReason()
    {
        var check = DeployVerdictMath.TileCheck(TileResult("Ghost", SloState.NoData, known: false, threshold: 1));

        check.ShouldNotBeNull();
        check.State.ShouldBe("noData");
        check.Reason.ShouldContain("not in the metric registry");
    }

    [TimedFact]
    public void TileCheck_NonFiniteValue_IsNull()
    {
        var check = DeployVerdictMath.TileCheck(TileResult("Odd", SloState.Healthy, current: double.NaN, threshold: 1));

        check?.Value.ShouldBeNull();
    }

    [TimedFact]
    public void SloCheck_EvaluatedAfterTheDeploy_UsesItsState()
    {
        var (check, state) = DeployVerdictMath.SloCheck(Slo("Payments", SloState.Warning, evaluatedAt: Now), Now.AddHours(-1));

        state.ShouldBe(SloState.Warning);
        check.Source.ShouldBe(DeployVerdictSources.Slo);
        check.State.ShouldBe("warning");
        check.Value.ShouldBe(0.97);
        check.Target.ShouldBe(0.99);
        check.BudgetRemaining.ShouldBe(0.1);
    }

    [TimedFact]
    public void SloCheck_LastEvaluatedBeforeTheDeploy_IsNoData()
    {
        var (check, state) = DeployVerdictMath.SloCheck(Slo("Payments", SloState.Breaching, evaluatedAt: Now.AddHours(-2)), Now.AddHours(-1));

        state.ShouldBe(SloState.NoData);
        check.Value.ShouldBeNull();
        check.Reason.ShouldContain("before the deploy");
    }

    [TimedFact]
    public void SloCheck_NeverEvaluated_IsNoData()
    {
        var (check, state) = DeployVerdictMath.SloCheck(Slo("Payments", SloState.Healthy, evaluatedAt: null), Now.AddHours(-1));

        state.ShouldBe(SloState.NoData);
        check.Reason.ShouldBe("Not evaluated yet.");
    }

    [TimedFact]
    public void SloCheck_Acknowledged_IsMarked()
    {
        var (check, state) = DeployVerdictMath.SloCheck(Slo("Payments", SloState.Acknowledged, evaluatedAt: Now), Now.AddHours(-1));

        state.ShouldBe(SloState.Acknowledged);
        check.Acknowledged.ShouldBeTrue();
    }

    [TimedFact]
    public void Build_ScopesTilesAndSlosToTheApplication_AndSkipsDisabledSlos()
    {
        var resolved = DeployVerdictMath.Resolve([Deploy("2.0.0", -1)], null, null)!;
        var window = DeployVerdictMath.Window(Now.AddHours(-1), null, Now, TimeSpan.FromMinutes(5));

        var verdict = DeployVerdictMath.Build(
            resolved,
            window,
            [
                TileResult("Mine", SloState.Healthy, application: "api", current: 1, threshold: 5),
                TileResult("Global", SloState.Warning, current: 4.5, threshold: 5),
                TileResult("Theirs", SloState.Breaching, application: "admin", current: 9, threshold: 5),
            ],
            [
                Slo("Z mine", SloState.Healthy, evaluatedAt: Now, application: "api"),
                Slo("A global", SloState.Healthy, evaluatedAt: Now),
                Slo("Theirs", SloState.Breaching, evaluatedAt: Now, application: "admin"),
                Slo("Off", SloState.Breaching, evaluatedAt: Now, enabled: false),
            ]);

        verdict.Verdict.ShouldBe(DeployVerdicts.Warn);
        verdict.Checks.Select(x => x.Name).ShouldBe(["Mine", "Global", "A global", "Z mine"]);
        verdict.Summary.ShouldBe("warn: 1 warning, 3 healthy of 4 checks");
        verdict.Deploy.PreviousVersion.ShouldBeNull();
    }

    [TimedFact]
    public void Build_Pending_SummarisesTheSettleTime()
    {
        var resolved = DeployVerdictMath.Resolve([Deploy("2.0.0", 0)], null, null)!;
        var window = DeployVerdictMath.Window(Now.AddMinutes(-2), null, Now, TimeSpan.FromMinutes(5));

        var verdict = DeployVerdictMath.Build(resolved, window, [], []);

        verdict.Verdict.ShouldBe(DeployVerdicts.Pending);
        verdict.Summary.ShouldBe("pending: settles at 2026-11-16T12:03:00Z");
    }

    [TimedFact]
    public void Build_NoChecks_IsNoDataWithAnExplanation()
    {
        var resolved = DeployVerdictMath.Resolve([Deploy("2.0.0", -1)], null, null)!;
        var window = DeployVerdictMath.Window(Now.AddHours(-1), null, Now, TimeSpan.FromMinutes(5));

        var verdict = DeployVerdictMath.Build(resolved, window, [], []);

        verdict.Verdict.ShouldBe(DeployVerdicts.NoData);
        verdict.Summary.ShouldContain("no board tile with a rule and no SLO");
    }

    private static ApplicationDeployModel Deploy(string version, int hoursAgo, string? environment = null) => new()
    {
        Id = Guid.NewGuid(),
        Application = "api",
        Version = version,
        Environment = environment,
        FirstSeenAt = Now.AddHours(hoursAgo),
        InstanceId = Guid.NewGuid(),
    };

    private static MetricBoardTileResult TileResult(
        string name,
        SloState? status,
        SloState? unacknowledged = null,
        string? application = null,
        bool known = true,
        double? current = null,
        double? threshold = null)
        => new()
        {
            Tile = new MetricBoardTileModel
            {
                Id = name.Length,
                Name = name,
                Instrument = "c",
                Application = application,
                Kind = MetricTileKind.Value,
                StatusRule = status is null ? MetricTileStatusRule.None : MetricTileStatusRule.Threshold,
                Comparison = SloComparison.AtMost,
                Threshold = threshold,
            },
            InstrumentKind = known ? MetricKind.Counter : null,
            Current = current,
            Status = status,
            UnacknowledgedStatus = unacknowledged ?? status,
        };

    private static SloObjectiveModel Slo(string name, SloState state, DateTime? evaluatedAt, string? application = null, bool enabled = true) => new()
    {
        Id = name.Length,
        Name = name,
        Kind = SloKind.SuccessRate,
        Application = application,
        TargetValue = 0.99,
        WindowSeconds = 3600,
        Enabled = enabled,
        Evaluated = evaluatedAt is not null,
        Attainment = 0.97,
        BudgetRemaining = 0.1,
        State = state,
        LastEvaluatedAt = evaluatedAt,
    };
}
