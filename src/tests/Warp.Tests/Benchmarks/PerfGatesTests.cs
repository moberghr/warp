using Shouldly;
using Warp.ServerBenchmarks.Lab;

namespace Warp.Tests.Benchmarks;

/// <summary>
/// The perf-CI gate decision (rule 6.24): where each line sits after the no-op calibration, which rows are
/// report-only, and how a lopsided two-server case is judged on each provider.
/// </summary>
[Trait("Category", "NoDb")]
public class PerfGatesTests
{
    private static readonly GateCase PostgresDraining = new(SqlServer: false, PerSecond: false, Calibrated: true, ClockBound: false, StatementTolerancePct: 0);

    private static readonly GateSide Base = new(StatementsPerJob: 10, CommandsPerJob: 10, WalBytesPerJob: 1000, BuffersPerJob: 100, JobsPerSecond: 300);

    [TimedFact]
    public void Breaches_IdenticalSides_None()
    {
        var breaches = PerfGates.Breaches(PostgresDraining, Base, Base);

        breaches.ShouldBeEmpty();
    }

    [TimedTheory]
    [InlineData(10.49, false)]
    [InlineData(10.51, true)]
    public void Breaches_Statements_FailPastFivePercent(double headStatements, bool breached)
    {
        var breaches = PerfGates.Breaches(PostgresDraining, Base, Base with { StatementsPerJob = headStatements });

        breaches.Any(x => x.Metric == GateMetric.Statements).ShouldBe(breached);
    }

    [TimedTheory]
    [InlineData(10.6, false)]
    [InlineData(10.79, false)]
    [InlineData(10.81, true)]
    public void Breaches_ScenarioWithItsOwnStatementLine_UsesIt(double headStatements, bool breached)
    {
        var rateLimit = PostgresDraining with { ClockBound = true, StatementTolerancePct = 8.0 };
        PerfGates.StatementLimit(rateLimit).ShouldBe(8.0);

        var breaches = PerfGates.Breaches(rateLimit, Base, Base with { StatementsPerJob = headStatements });

        breaches.Any(x => x.Metric == GateMetric.Statements).ShouldBe(breached);
    }

    [TimedTheory]
    [InlineData(10.99, false)]
    [InlineData(11.01, true)]
    public void Breaches_Commands_FailPastTenPercent(double headCommands, bool breached)
    {
        var breaches = PerfGates.Breaches(PostgresDraining, Base, Base with { CommandsPerJob = headCommands });

        breaches.Any(x => x.Metric == GateMetric.Commands).ShouldBe(breached);
    }

    [TimedTheory]
    [InlineData(1059, false)]
    [InlineData(1061, true)]
    public void Breaches_Wal_FailPastSixPercent(double headWal, bool breached)
    {
        var breaches = PerfGates.Breaches(PostgresDraining, Base, Base with { WalBytesPerJob = headWal });

        breaches.Any(x => x.Metric == GateMetric.Wal).ShouldBe(breached);
    }

    [TimedFact]
    public void Breaches_WalOnAnIdleScenario_NotJudged()
    {
        var idle = PostgresDraining with { PerSecond = true };

        var breaches = PerfGates.Breaches(idle, Base, Base with { WalBytesPerJob = 2000 });

        breaches.ShouldBeEmpty();
    }

    [TimedFact]
    public void Breaches_ThroughputCollapsedToUnderAThird_Breached()
    {
        var breaches = PerfGates.Breaches(PostgresDraining, Base, Base with { JobsPerSecond = 99 });

        breaches.Single().Metric.ShouldBe(GateMetric.Throughput);
    }

    [TimedFact]
    public void Breaches_ThroughputCollapseOnAClockBoundScenario_NotJudged()
    {
        var clockBound = PostgresDraining with { ClockBound = true };

        var breaches = PerfGates.Breaches(clockBound, Base, Base with { JobsPerSecond = 10 });

        breaches.ShouldBeEmpty();
    }

    [TimedFact]
    public void Breaches_BuffersBlownUpPastFiveTimes_Breached()
    {
        var breaches = PerfGates.Breaches(PostgresDraining, Base, Base with { BuffersPerJob = 501 });

        breaches.Single().Metric.ShouldBe(GateMetric.Buffers);
    }

    [TimedFact]
    public void Breaches_ChangeMissingOnOneSide_NotJudged()
    {
        var breaches = PerfGates.Breaches(PostgresDraining, Base with { CommandsPerJob = null }, Base with { CommandsPerJob = 50 });

        breaches.ShouldBeEmpty();
    }

    [TimedTheory]
    [InlineData(false, false, true, false)]
    [InlineData(true, false, true, true)]
    [InlineData(false, true, true, true)]
    [InlineData(false, false, false, true)]
    public void IsReportOnly_SqlServerIdleOrUncalibrated_ReportOnly(bool sqlServer, bool perSecond, bool calibrated, bool reportOnly)
    {
        var gateCase = PostgresDraining with { SqlServer = sqlServer, PerSecond = perSecond, Calibrated = calibrated };

        var result = PerfGates.IsReportOnly(gateCase);

        result.ShouldBe(reportOnly);
    }

    [TimedFact]
    public void LopsidedSides_OneSideUnderAFifth_NamesIt()
    {
        var sides = PerfGates.LopsidedSides(0.47, 0.12);

        sides.ShouldBe(["head"]);
    }

    [TimedFact]
    public void LopsidedSides_BothSidesShareTheWork_None()
    {
        var sides = PerfGates.LopsidedSides(0.5, 0.2);

        sides.ShouldBeEmpty();
    }

    [TimedTheory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void LopsidedFails_FailsOnPostgreSqlWarnsOnSqlServer(bool sqlServer, bool fails)
    {
        var result = PerfGates.LopsidedFails(sqlServer);

        result.ShouldBe(fails);
    }
}
