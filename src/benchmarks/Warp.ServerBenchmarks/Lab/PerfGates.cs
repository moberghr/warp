namespace Warp.ServerBenchmarks.Lab;

/// <summary>
/// Which perf-CI lines fail a build and where they sit, kept apart from the report's formatting so the
/// decision can be tested on its own (linked into Warp.Tests, which cannot reference this executable).
/// <para>
/// Calibrated by three no-op runs on CI (main compared with itself, runs 46080133283, 46080140651 and
/// 46080148230): every line sits well clear of the worst noise those runs measured. Rule §6.24 in
/// performance-worker.md; the calibration table is in its design note.
/// </para>
/// </summary>
internal static class PerfGates
{
    /// <summary>
    /// Statements per job, PostgreSQL. Deliberately loose: a gate that fires on drift gets ignored, then
    /// switched off. Identical builds moved at most 2% nearly everywhere; the regressions this suite was built
    /// after were +48.6% (the claim predicate) and -51% (the lock pool).
    /// </summary>
    public const double StatementTolerancePct = 5.0;

    /// <summary>
    /// Commands per job (sent at the driver), PostgreSQL. Identical builds moved up to +7.4% (long-running
    /// jobs) and +5.8% (rate limit).
    /// </summary>
    public const double CommandTolerancePct = 10.0;

    /// <summary>
    /// WAL bytes per job, PostgreSQL, draining scenarios only. Identical builds moved at most 3% on every
    /// draining scenario (2.2% long-running, 2.1% mutex); idle seconds swing 6-14% and are not gated.
    /// </summary>
    public const double WalTolerancePct = 6.0;

    /// <summary>
    /// Only a collapse fails: head this many times slower than base. Identical builds moved 0.70x to 1.43x.
    /// </summary>
    public const double ThroughputCollapseFactor = 3.0;

    /// <summary>
    /// Pages read per job move up to 70% between identical builds, but every large jump seen so far was a query
    /// plan gone wrong (18x and more), so only a blow-up to this factor fails.
    /// </summary>
    public const double BuffersBlowUpFactor = 5.0;

    /// <summary>
    /// Two-server scenarios: the server that completed fewer jobs must have done at least this share, or the
    /// case did not measure two servers.
    /// </summary>
    public const double MinServerShare = 0.2;

    /// <summary>
    /// A row whose lines are shown and fail nothing. Calibration found these too noisy to gate: every SQL Server
    /// row (statements moved up to 37% and commands 17% between identical builds), an idle scenario measured per
    /// second (10-27% on everything), and a scenario not yet calibrated (<c>[CiScenario(Calibrated = false)]</c>).
    /// </summary>
    public static bool IsReportOnly(GateCase gateCase) =>
        !gateCase.Calibrated || gateCase.SqlServer || gateCase.PerSecond;

    /// <summary>The statement line for a scenario: its own when it declares one, else the default.</summary>
    public static double StatementLimit(GateCase gateCase) =>
        gateCase.StatementTolerancePct > 0 ? gateCase.StatementTolerancePct : StatementTolerancePct;

    /// <summary>
    /// Every line the head crossed, in the order the report shows them. Each one fails the build unless the
    /// row is report-only (<see cref="IsReportOnly"/>).
    /// </summary>
    public static IReadOnlyList<GateBreach> Breaches(GateCase gateCase, GateSide baseSide, GateSide headSide)
    {
        var breaches = new List<GateBreach>();

        var statements = PercentChange(baseSide.StatementsPerJob, headSide.StatementsPerJob);
        var statementLimit = StatementLimit(gateCase);
        if (statements > statementLimit)
        {
            breaches.Add(new GateBreach(GateMetric.Statements, statements.Value, statementLimit));
        }

        var commands = PercentChange(baseSide.CommandsPerJob, headSide.CommandsPerJob);
        if (commands > CommandTolerancePct)
        {
            breaches.Add(new GateBreach(GateMetric.Commands, commands.Value, CommandTolerancePct));
        }

        var wal = gateCase.PerSecond ? null : PercentChange(baseSide.WalBytesPerJob, headSide.WalBytesPerJob);
        if (wal > WalTolerancePct)
        {
            breaches.Add(new GateBreach(GateMetric.Wal, wal.Value, WalTolerancePct));
        }

        // A clock sets a clock-bound scenario's duration, so its rate says how long the clock ran.
        if (!gateCase.ClockBound
            && baseSide.JobsPerSecond is not null
            && headSide.JobsPerSecond is not null
            && headSide.JobsPerSecond.Value * ThroughputCollapseFactor < baseSide.JobsPerSecond.Value)
        {
            breaches.Add(new GateBreach(GateMetric.Throughput, baseSide.JobsPerSecond.Value / headSide.JobsPerSecond.Value, ThroughputCollapseFactor));
        }

        if (!gateCase.PerSecond
            && baseSide.BuffersPerJob is > 0
            && headSide.BuffersPerJob is not null
            && headSide.BuffersPerJob.Value > baseSide.BuffersPerJob.Value * BuffersBlowUpFactor)
        {
            breaches.Add(new GateBreach(GateMetric.Buffers, headSide.BuffersPerJob.Value / baseSide.BuffersPerJob.Value, BuffersBlowUpFactor));
        }

        return breaches;
    }

    /// <summary>
    /// The sides ("base", "head") on which one server of a two-server case completed under
    /// <see cref="MinServerShare"/> of the jobs.
    /// </summary>
    public static IReadOnlyList<string> LopsidedSides(double? baseShare, double? headShare) =>
        [
            .. new[] { (Side: "base", Share: baseShare), (Side: "head", Share: headShare) }
                .Where(x => x.Share < MinServerShare)
                .Select(x => x.Side),
        ];

    /// <summary>
    /// Whether a lopsided two-server case fails the build. On PostgreSQL the two servers always split 47-50%,
    /// so a lopsided case did not measure two servers. On SQL Server one server did 12-40% across identical
    /// builds (one run failed on it), so there it is a warning in the table.
    /// </summary>
    public static bool LopsidedFails(bool sqlServer) => !sqlServer;

    /// <summary>Head against base, in percent; null when either side is missing or the base is zero.</summary>
    public static double? PercentChange(double? baseValue, double? headValue) =>
        baseValue is null or 0 || headValue is null
            ? null
            : (headValue.Value - baseValue.Value) / baseValue.Value * 100;
}

/// <summary>What the gates need to know about a case.</summary>
/// <param name="SqlServer">The case ran on SQL Server.</param>
/// <param name="PerSecond">An idle scenario, measured per second rather than per job.</param>
/// <param name="Calibrated">The scenario's gates have been calibrated by no-op runs.</param>
/// <param name="ClockBound">A clock sets the scenario's duration, so its rate is not gated.</param>
/// <param name="StatementTolerancePct">The scenario's own statement line, or 0 for the default.</param>
internal sealed record GateCase(bool SqlServer, bool PerSecond, bool Calibrated, bool ClockBound, double StatementTolerancePct);

/// <summary>One side's measurements, per job (or per second on an idle scenario).</summary>
internal sealed record GateSide(double? StatementsPerJob, double? CommandsPerJob, double? WalBytesPerJob, double? BuffersPerJob, double? JobsPerSecond);

/// <summary>A line the head crossed: a percentage for the count lines, a factor for throughput and buffers.</summary>
internal sealed record GateBreach(GateMetric Metric, double Change, double Limit);

internal enum GateMetric
{
    Statements = 1,
    Commands = 2,
    Wal = 3,
    Throughput = 4,
    Buffers = 5,
}
