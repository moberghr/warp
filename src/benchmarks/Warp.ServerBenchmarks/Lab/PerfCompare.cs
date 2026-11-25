using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

using Warp.ServerBenchmarks.Infrastructure;

namespace Warp.ServerBenchmarks.Lab;

/// <summary>One metric's median across an arm's runs, carried with the spread that produced it.</summary>
/// <remarks>
/// The spread travels with the median deliberately. A comparator that sees only medians will report a
/// 3% "regression" that sits inside a 20% band, which is how a performance gate earns a reputation for
/// crying wolf and gets switched off. <see cref="PerfCompare"/> refuses to judge a difference smaller
/// than the noise it is measured against.
/// </remarks>
public sealed record MetricSummary(double Median, double Min, double Max, double SpreadPct);

/// <summary>An arm's full result: what ran, and what it measured.</summary>
public sealed record ArmResult(string Scenario, ArmParameters Parameters, int N, Dictionary<string, MetricSummary> Metrics);

/// <summary>
/// Compares two <c>load --json</c> results and decides whether to fail the build.
/// <para>
/// Shares <see cref="ArmResult"/> with the code that writes it, so the schema cannot drift: renaming a
/// field breaks this file at compile time instead of silently producing a wrong comparison in CI.
/// </para>
/// <para>
/// What is gated is a measurement question, not a preference. <c>statements_per_job</c> is a COUNT and
/// reproduced to 0.1-2.7% within a run on a quiet machine, so a move past a few percent is real. The
/// timings on these arms are not gateable at any threshold: their measured spread reached 417%
/// (<c>db_ms_per_job</c>) and 56% (<c>jobs_per_sec</c>) across the 7.1.0 revalidation, because one
/// iteration is a multi-second drain and no affordable number of repeats averages that away. They are
/// reported so a human can look, and never failed on. See docs/plans/2026-09-22-performance-ci.md.
/// </para>
/// </summary>
public static partial class PerfCompare
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    // BenchmarkDotNet writes a STRING where a statistic is undefined: the confidence interval of a case
    // that ran one or two iterations, which is what a single-window scenario runs. Read as a number it
    // crashed the comparison after the measurement had succeeded, so a spread that is not a number is
    // read as "none" (UndefinedAsNullConverter).
    private static readonly JsonSerializerOptions BdnOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new UndefinedAsNullConverter() },
    };

    /// <summary>
    /// What a regression has to exceed before it FAILS a build rather than merely being reported.
    /// <para>
    /// Deliberately loose, and the looseness is the point. A gate that fires on drift gets ignored,
    /// then switched off, and leaves the project worse off than no gate — it has spent its credibility.
    /// Nothing this suite exists to catch lives near these numbers: the claim predicate it was built
    /// after was 615x on EXPLAIN and +48.6% in statements, the lock pool was -51%. A 3% move is not
    /// the shape of a real regression, it is the shape of a runner having a bad afternoon.
    /// </para>
    /// <para>
    /// Everything smaller is still printed, with its direction and size, so a human can look. The
    /// gate's job is to stop a catastrophe reaching main unnoticed, not to adjudicate every percent.
    /// </para>
    /// </summary>
    private const double StatementTolerancePct = 5.0;

    // WAL bytes per job, PostgreSQL only. Calibrated the same way as statements: across three no-op
    // comparisons (42 cases, the same commit on both sides) statements moved at most 1.8% and WAL at
    // most 2.5%, so each gate sits at roughly three to four times the worst noise measured.
    private const double WalTolerancePct = 10.0;

    // Only a collapse fails: head must be this many times slower than base. A no-op comparison — the
    // same commit on both sides — measured anywhere from 0.70x to 1.43x, so halving (2x) sat too close
    // to noise. Both real regressions found so far were far past 3x: the dispatcher sat out its backoff
    // at 10x, and the over-claiming claim walked the table.
    private const double ThroughputCollapseFactor = 3.0;

    // Pages read per job. Not gated on a percentage: between identical builds it moves by half again
    // with cache state and vacuum timing. But every large jump seen so far was a real defect and none
    // was noise: a query the planner could serve from the wrong index (10,004 pages a call against 6,
    // rule 6.11) and a per-row check that should have been per-parent (2,948 buffers a job against
    // 165), both of which statements, WAL and throughput let through. So a blow-up fails, like a
    // throughput collapse does. Those two were 18x and more; 5x is far from both them and the noise.
    //
    // A plan flip can be intermittent. If this fires on a change that cannot have caused it, the
    // finding is still real: some query in that scenario has a plan that depends on statistics.
    private const double BuffersBlowUpFactor = 5.0;

    // Database time per job (PostgreSQL: pg_stat_statements execution + planning time; SQL Server: Query Store
    // CPU time) is REPORTED, not gated. It is a timing, so it carries the runner's noise where statements and WAL
    // are counts, and it has not been calibrated by comparing a commit with itself on CI the way the lines above
    // were. A provisional +25% gate failed the message-routing scenario on a swing it shows between identical
    // builds (its buffers per job read ~330 or ~600 on either side, the base side too). Gate it once no-op runs
    // have measured its band (performance.md).

    // Two-server scenarios: the server that completed fewer jobs must have done at least this share, or the
    // case did not measure two servers (one sat the drain out) and fails as an invalid measurement.
    private const double MinServerShare = 0.2;

    /// <summary>
    /// The database versions the run measured (<c>--database=pg17+mssql2022</c>), shown in each section's heading
    /// and stamped on exported cases, so a release's record and the history compare like with like.
    /// </summary>
    public static string? DatabaseLabel { get; set; }

    /// <summary>
    /// What the base side was when it was chosen by hand (<c>--base-label="7.7.0 (74443f8c)"</c>), shown in each
    /// section's heading. Empty for the default merge base, whose headings stay as they were.
    /// </summary>
    public static string? BaseLabel { get; set; }

    private static readonly MetricPolicy[] Policies =
    [
        new("statements_per_job", Gated: true, TolerancePct: 3.0, BadDirection: Direction.Higher),
        new("db_ms_per_job", Gated: false, TolerancePct: null, BadDirection: Direction.Higher),
        new("jobs_per_sec", Gated: false, TolerancePct: null, BadDirection: Direction.Lower),
        new("wall_seconds", Gated: false, TolerancePct: null, BadDirection: Direction.Higher),
    ];

    public static JsonSerializerOptions SerializerOptions => JsonOptions;

    /// <summary>Returns a process exit code: 0 pass, 1 regression, 2 the two arms are not comparable.</summary>
    public static int Run(string basePath, string headPath, string? label, string? summaryPath)
    {
        var baseArm = Read(basePath);
        var headArm = Read(headPath);

        var mismatch = Incomparable(baseArm, headArm);
        if (mismatch is not null)
        {
            Console.Error.WriteLine($"REFUSING TO COMPARE: {mismatch}");

            return 2;
        }

        var report = new StringBuilder();
        var failures = new List<string>();

        report.AppendLine(CultureInfo.InvariantCulture, $"### Performance — {label ?? baseArm.Scenario}");
        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"`{JsonSerializer.Serialize(baseArm.Parameters, JsonOptions)}`");
        report.AppendLine();
        report.AppendLine("| metric | base | head | change | spread (base/head) | verdict |");
        report.AppendLine("| --- | ---: | ---: | ---: | --- | --- |");

        foreach (var policy in Policies)
        {
            if (!baseArm.Metrics.TryGetValue(policy.Metric, out var b) || !headArm.Metrics.TryGetValue(policy.Metric, out var h))
            {
                continue;
            }

            var change = Math.Abs(b.Median) < double.Epsilon ? 0 : (h.Median - b.Median) / b.Median * 100;
            var worse = policy.BadDirection == Direction.Higher ? change > 0 : change < 0;
            var widest = Math.Max(b.SpreadPct, h.SpreadPct);
            var verdict = Judge(policy, change, worse, widest, failures);

            report.AppendLine(
                CultureInfo.InvariantCulture,
                $"| `{policy.Metric}` | {b.Median:N3} | {h.Median:N3} | {change:+0.0;-0.0;0.0}% | {b.SpreadPct:N1}% / {h.SpreadPct:N1}% | {verdict} |");
        }

        report.AppendLine();
        report.AppendLine(
            CultureInfo.InvariantCulture,
            $"n = {baseArm.N} (base), {headArm.N} (head). Times are reported, never gated.");

        Console.WriteLine(report.ToString());

        if (!string.IsNullOrEmpty(summaryPath))
        {
            File.AppendAllText(summaryPath, report.ToString() + Environment.NewLine);
        }

        if (failures.Count == 0)
        {
            return 0;
        }

        Console.Error.WriteLine("FAILED:");
        foreach (var failure in failures)
        {
            Console.Error.WriteLine("  " + failure);
        }

        return 1;
    }

    private static string Judge(MetricPolicy policy, double change, bool worse, double widest, List<string> failures)
    {
        if (!policy.Gated)
        {
            return "report only";
        }

        if (!worse || Math.Abs(change) <= policy.TolerancePct)
        {
            return "ok";
        }

        // Inside one arm's own noise band: say so rather than failing. This is the case that decides
        // whether anyone still trusts the gate in six months.
        if (Math.Abs(change) <= widest)
        {
            return string.Create(CultureInfo.InvariantCulture, $"inconclusive (inside {widest:N1}% spread)");
        }

        failures.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"{policy.Metric}: {change:+0.0;-0.0;0.0}% (tolerance {policy.TolerancePct}%, spread {widest:N1}%)"));

        return "**REGRESSION**";
    }

    /// <summary>Two arms are comparable only if they ran the same shape. A filename is not evidence.</summary>
    private static string? Incomparable(ArmResult baseArm, ArmResult headArm)
    {
        if (!string.Equals(baseArm.Scenario, headArm.Scenario, StringComparison.Ordinal))
        {
            return $"scenario differs: {baseArm.Scenario} vs {headArm.Scenario}";
        }

        // Records compare structurally, so this catches every parameter at once — including one added
        // later that nobody remembered to check here. Reflection then NAMES the offenders, because
        // "these two arms differ" without saying how is a message that sends someone diffing JSON by eye.
        if (baseArm.Parameters != headArm.Parameters)
        {
            var differing = typeof(ArmParameters)
                .GetProperties()
                .Where(x => !Equals(x.GetValue(baseArm.Parameters), x.GetValue(headArm.Parameters)))
                .Select(x => $"{x.Name} {x.GetValue(baseArm.Parameters)} vs {x.GetValue(headArm.Parameters)}");

            return "arm parameters differ: " + string.Join(", ", differing);
        }

        return null;
    }

    private static ArmResult Read(string path)
    {
        var json = File.ReadAllText(path);

        return JsonSerializer.Deserialize<ArmResult>(json, JsonOptions)
            ?? throw new InvalidOperationException($"{path} did not contain an arm result.");
    }

    /// <summary>
    /// Compares two BenchmarkDotNet runs of the same benchmarks, built from different versions.
    /// <para>
    /// Gates the two DETERMINISTIC quantities — allocations and database statements per job — and
    /// reports time without gating it. That split is the whole design: both counts reproduce exactly
    /// enough to compare across separate processes, where the timings of these same runs have shown
    /// an <c>Error</c> of 108 s on a 25 s mean. Interleaving and statistical tests exist to beat timing
    /// noise; a count needs neither. Cross-version TIME wants BDN's own multi-version job
    /// (<c>Job.WithNuGet</c>), where both versions run interleaved in one process.
    /// </para>
    /// </summary>
    public static int RunBdn(string basePath, string headPath, double tolerancePct, string? summaryPath, string? releasePath = null)
    {
        var baseRun = ReadBdn(basePath);
        var headRun = ReadBdn(headPath);
        var release = LoadReleaseBaseline(releasePath);

        var failures = new List<string>();
        var report = new StringBuilder();

        // One section per benchmark class, in the order the classes declare. A CI job runs one class,
        // so its file holds one section; a local run over several gets them all, in the same order the
        // combined CI report uses.
        var scenarios = headRun.Keys
            .Concat(baseRun.Keys)
            .Distinct(StringComparer.Ordinal)
            .Select(x => new BdnCase(x))
            .GroupBy(x => x.TypeName, StringComparer.Ordinal)
            .Select(x => (Type: typeof(PerfCompare).Assembly.GetType(x.Key), Cases: x.ToList()))
            .OrderBy(x => x.Type?.GetCustomAttribute<CiScenarioAttribute>()?.Order ?? int.MaxValue)
            .ThenBy(x => x.Cases[0].TypeName, StringComparer.Ordinal);

        foreach (var (type, cases) in scenarios)
        {
            report.Append(RenderScenario(type, cases, baseRun, headRun, tolerancePct, failures, release));
            report.AppendLine();
        }

        Console.WriteLine(report.ToString());

        if (!string.IsNullOrEmpty(summaryPath))
        {
            File.AppendAllText(summaryPath, report.ToString());
        }

        if (failures.Count == 0)
        {
            return 0;
        }

        Console.Error.WriteLine("FAILED:");
        foreach (var failure in failures)
        {
            Console.Error.WriteLine("  " + failure);
        }

        return 1;
    }

    private static string RenderScenario(
        Type? type,
        List<BdnCase> cases,
        Dictionary<string, BdnResult> baseRun,
        Dictionary<string, BdnResult> headRun,
        double tolerancePct,
        List<string> failures,
        ReleaseBenchmarks? release)
    {
        var scenario = type?.GetCustomAttribute<CiScenarioAttribute>();
        var labels = type?.GetCustomAttributes<CaseLabelAttribute>().ToList() ?? [];
        var failuresBefore = failures.Count;
        var rows = new StringBuilder();
        var notes = new StringBuilder();

        // Gates on a measurement not yet calibrated by comparing a commit with itself are shown, never failed
        // (performance.md lists what to re-enable once no-op runs have measured them): a whole scenario that is
        // new or was reworked, and SQL Server's per-job rows, recounted from Query Store.
        var calibrated = scenario?.Calibrated ?? true;

        // A parameter with one value is the same on every row: it belongs to the scenario, not the case.
        var varying = cases
            .SelectMany(x => x.Parameters)
            .GroupBy(x => x.Name, StringComparer.Ordinal)
            .Where(x => x.Select(y => y.Value).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(x => x.Key)
            .ToList();
        var severalMethods = cases.Select(x => x.Method).Distinct(StringComparer.Ordinal).Count() > 1;
        var declared = headRun.Keys.Concat(baseRun.Keys).Distinct(StringComparer.Ordinal).ToList();

        // Worked out before the rows, so a case that drifted since the latest release says so in its own
        // row and in the heading. On the line under the table alone it was easy to miss beside a ✅.
        var sinceRelease = SinceRelease(cases, headRun, release);
        var drifted = sinceRelease.Values.Count(x => x > ReleaseCreepPct);

        // BenchmarkDotNet's own layout: one column per parameter, then the statistics, one row per
        // result. A comparison is two rows per case, base then head, the way BDN shows a benchmark
        // against its Baseline: the base row carries Ratio 1.00 and the head row its ratio to it.
        var columns = new List<string>();
        if (severalMethods)
        {
            columns.Add("Method");
        }

        columns.AddRange(varying);
        // An idle scenario runs no jobs, so the diagnoser reports its counters per second instead, into
        // the same columns. The headers say which.
        // Decided by what the diagnoser measured, not by whether the benchmark declares a JobCount
        // parameter. The rate-limit scenario has none (its job count follows its Shape), and going by
        // the parameter labelled its per-job numbers "per second" and exempted it from every gate.
        // Declared on the benchmark where it can be read (MeasuredPerSecond), else read off the measurement.
        var perSecond = type is not null
            ? DatabaseStatementsDiagnoser.IsPerSecond(type)
            : cases.All(x => headRun.TryGetValue(x.FullName, out var measured) ? measured.PerSecond : JobCountOf(x) is null);
        var per = perSecond ? "s" : "job";
        var header = string.Join(" | ", columns.Append("Commit")) + $" | Mean | Error | StdDev | Ratio | Jobs/s | Statements/{per} | Buffers/{per} | WAL/{per} | DB ms/{per} | Commands/{per} | Xacts/{per} | Lock wait ms/{per} | Allocated | Alloc Ratio | Gate";
        var alignment = string.Join(" | ", columns.Append("Commit").Select(_ => ":---")) + " | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | :---";

        // Declared order, the order BenchmarkDotNet ran them in. Sorting by name put "Keys: 10000"
        // before "Keys: 8".
        foreach (var benchmark in cases.OrderBy(x => declared.IndexOf(x.FullName)))
        {
            var cells = new List<string>();
            if (severalMethods)
            {
                cells.Add(benchmark.Method);
            }

            cells.AddRange(varying.Select(x => ParameterCell(benchmark, x, labels)));
            var prefix = string.Join(" | ", cells);
            prefix = prefix.Length == 0 ? string.Empty : prefix + " | ";

            var hasBase = baseRun.TryGetValue(benchmark.FullName, out var b);
            var hasHead = headRun.TryGetValue(benchmark.FullName, out var h);

            if (!hasBase || !hasHead || b is null || h is null)
            {
                // A case only one side has is new or removed, not a regression.
                var side = hasHead ? "head only (new)" : "base only (removed)";
                rows.AppendLine(CultureInfo.InvariantCulture, $"| {prefix}{side} | | | | | | | | | | | | | | | ➖ |");
                continue;
            }

            // A side that measured NOTHING is not a pass. BenchmarkDotNet writes null for an arm that
            // errored or timed out, and coercing that to zero made the row read "base 0, head 346M,
            // 0.0%, ok" — a green verdict for a comparison that never happened. Left alone it would
            // hide the gate breaking entirely: if the base build stopped producing results, every pull
            // request would sail through reporting success.
            if (b.Bytes is null || h.Bytes is null)
            {
                var missing = MissingSide(b.Bytes, h.Bytes);
                rows.AppendLine(CultureInfo.InvariantCulture, $"| {prefix}base | {(b.Bytes is null ? "NA" : Seconds(b.Mean))} | | | | | | | | | | | | | | |");
                rows.AppendLine(CultureInfo.InvariantCulture, $"| {prefix}head | {(h.Bytes is null ? "NA" : Seconds(h.Mean))} | | | | | | | | | | | | | | ⚠️ no measurement from {missing} |");
                failures.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{benchmark.Short}: {missing} produced no measurement, so nothing was compared"));

                continue;
            }

            // A database figure on one side only is a measurement that failed, not a pass: the diagnoser
            // yields nothing when a counter read failed or the window wrote no job. Likewise a per-second
            // figure on one side against a per-job one on the other compares two different units.
            //
            // On NEITHER side is no better when the database was there to be read: a counter read that fails on
            // both sides (Query Store off, a job count that went backwards) must not read as a pass.
            string? unmeasured = null;
            if ((b.StatementsPerJob is null) != (h.StatementsPerJob is null))
            {
                unmeasured = "statements measured on one side only";
            }
            else if (b.StatementsPerJob is null && DatabaseConfigured(benchmark))
            {
                unmeasured = "statements measured on neither side, with the database configured";
            }
            else if (!perSecond && (b.WalBytesPerJob is null) != (h.WalBytesPerJob is null))
            {
                unmeasured = "WAL measured on one side only";
            }
            else if (b.PerSecond != h.PerSecond)
            {
                unmeasured = "per-job figures on one side, per-second on the other";
            }

            if (unmeasured is not null)
            {
                rows.AppendLine(CultureInfo.InvariantCulture, $"| {prefix}base | {Seconds(b.Mean)} | | | | | | | | | | | | | | |");
                rows.AppendLine(CultureInfo.InvariantCulture, $"| {prefix}head | {Seconds(h.Mean)} | | | | | | | | | | | | | | ⚠️ {unmeasured} |");
                failures.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{benchmark.Short}: {unmeasured}, so nothing was compared"));

                continue;
            }

            double? stmtChange = b.StatementsPerJob is null or 0 || h.StatementsPerJob is null
                ? null
                : (h.StatementsPerJob.Value - b.StatementsPerJob.Value) / b.StatementsPerJob.Value * 100;
            var baseRate = RateOf(b, benchmark);
            var headRate = RateOf(h, benchmark);
            double? rateChange = baseRate is null || headRate is null ? null : (headRate.Value - baseRate.Value) / baseRate.Value * 100;
            var reportOnly = !calibrated || (IsSqlServer(benchmark) && !perSecond);

            // Statements and WAL per job are the gates; see StatementTolerancePct and WalTolerancePct
            // for where the lines sit. Allocations are REPORTED, not gated: across the same no-op runs
            // they moved up to 4.9%, too close to any threshold worth having. Buffers moved up to 55%
            // with cache state and vacuum timing, so only a blow-up fails (BuffersBlowUpFactor, below).
            // Per-second counters (an idle server) are gated on statements too, on PostgreSQL: comparing
            // a commit with itself they moved 0.0%, 0.0%, 0.8% and 1.5% once the window was 60 s after a
            // 60 s settle (before that a once-a-minute task, and start-up work on whichever side ran
            // first, moved them 14%). SQL Server's still move about 4% between identical builds, by a
            // cause not found, and stay reported. Pages and WAL per second are reported on both: a
            // single background tick is a large share of an idle second.
            if (perSecond && IsSqlServer(benchmark))
            {
                stmtChange = null;
            }

            var gate = perSecond ? "reported" : "✅";
            if (stmtChange is not null)
            {
                gate = string.Create(CultureInfo.InvariantCulture, $"{(reportOnly ? "➖" : "✅")} stmt {stmtChange:+0.0;−0.0;0.0}%{(reportOnly ? " (report-only)" : string.Empty)}");
            }

            // A line crossed: fails the case, or on a report-only row is shown with ⚠️ and fails nothing.
            void Breach(string cell, string failure)
            {
                if (!reportOnly)
                {
                    gate = "❌ " + cell;
                    failures.Add(failure);

                    return;
                }

                if (!gate.StartsWith('❌'))
                {
                    gate = "⚠️ " + cell + " (report-only)";
                }
            }

            if (stmtChange > StatementTolerancePct)
            {
                Breach(
                    string.Create(CultureInfo.InvariantCulture, $"stmt {stmtChange:+0.0;−0.0;0.0}% (limit +{StatementTolerancePct:0}%)"),
                    string.Create(CultureInfo.InvariantCulture, $"{benchmark.Short}: statements/job {b.StatementsPerJob:N2} -> {h.StatementsPerJob:N2} ({stmtChange:+0.0;-0.0;0.0}%)"));
            }

            double? walChange = perSecond || b.WalBytesPerJob is null or 0 || h.WalBytesPerJob is null
                ? null
                : (h.WalBytesPerJob.Value - b.WalBytesPerJob.Value) / b.WalBytesPerJob.Value * 100;
            if (walChange > WalTolerancePct)
            {
                Breach(
                    string.Create(CultureInfo.InvariantCulture, $"WAL {walChange:+0.0;−0.0;0.0}% (limit +{WalTolerancePct:0}%)"),
                    string.Create(CultureInfo.InvariantCulture, $"{benchmark.Short}: WAL/job {b.WalBytesPerJob:N0} -> {h.WalBytesPerJob:N0} bytes ({walChange:+0.0;-0.0;0.0}%)"));
            }

            // Throughput is far too noisy on a shared runner to gate a small change, but a collapse is
            // not noise. See ThroughputCollapseFactor for where the line sits and why. Not on a scenario whose
            // duration a clock sets (a rate-limit window, a scheduler tick, a job that sleeps a second): its
            // rate says how long the clock ran, and a change in it is not Warp's.
            if (scenario?.ClockBound != true && baseRate is not null && headRate is not null && headRate.Value * ThroughputCollapseFactor < baseRate.Value)
            {
                Breach(
                    string.Create(CultureInfo.InvariantCulture, $"jobs/s {baseRate.Value / headRate.Value:0.0}× slower (limit {ThroughputCollapseFactor:0}×)"),
                    string.Create(CultureInfo.InvariantCulture, $"{benchmark.Short}: jobs/s {baseRate:N0} -> {headRate:N0} ({rateChange:+0.0;-0.0;0.0}%)"));
            }

            // Pages read are too noisy to gate a small change, but a blow-up is a query plan gone wrong.
            // See BuffersBlowUpFactor.
            if (!perSecond && b.BuffersPerJob is > 0 && h.BuffersPerJob is not null && h.BuffersPerJob.Value > b.BuffersPerJob.Value * BuffersBlowUpFactor)
            {
                Breach(
                    string.Create(CultureInfo.InvariantCulture, $"buffers {h.BuffersPerJob.Value / b.BuffersPerJob.Value:0.0}× more (limit {BuffersBlowUpFactor:0}×)"),
                    string.Create(CultureInfo.InvariantCulture, $"{benchmark.Short}: buffers/job {b.BuffersPerJob:N1} -> {h.BuffersPerJob:N1} ({h.BuffersPerJob.Value / b.BuffersPerJob.Value:0.0}x); a query in this scenario changed plan"));
            }

            // Not a regression gate but a validity check, so it fails whatever the calibration: a two-server case
            // in which one server completed almost nothing measured one server.
            if (b.SmallestServerShare is not null || h.SmallestServerShare is not null)
            {
                notes.AppendLine(CultureInfo.InvariantCulture, $"<sub>{prefix.TrimEnd(' ', '|').Replace(" | ", ", ", StringComparison.Ordinal)}: the server that completed fewer jobs did {Share(b)} (base) and {Share(h)} (head).</sub>");
                notes.AppendLine();
                var lopsided = new[] { (Side: "base", Result: b), (Side: "head", Result: h) }
                    .Where(x => x.Result.SmallestServerShare < MinServerShare)
                    .Select(x => x.Side)
                    .ToList();
                if (lopsided.Count > 0)
                {
                    gate = string.Create(CultureInfo.InvariantCulture, $"❌ one server did under {MinServerShare * 100:0}% of the jobs ({string.Join(", ", lopsided)})");
                    failures.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{benchmark.Short}: one server completed under {MinServerShare * 100:0}% of the jobs on {string.Join(" and ", lopsided)}, so two servers were not measured"));
                }
            }

            var ratio = b.Mean <= 0 ? "?" : (h.Mean / b.Mean).ToString("N2", CultureInfo.InvariantCulture);
            var allocRatio = b.Bytes.Value <= 0 ? "?" : (h.Bytes.Value / (double)b.Bytes.Value).ToString("N2", CultureInfo.InvariantCulture);

            // Reported, not gated: a large rise is still worth a glance, so it is marked, not failed.
            if (b.Bytes.Value > 0 && (h.Bytes.Value - b.Bytes.Value) / (double)b.Bytes.Value * 100 > tolerancePct)
            {
                allocRatio += " ↑";
            }

            rows.AppendLine(CultureInfo.InvariantCulture, $"| {prefix}base | {Row(b)} | 1.00 | {Rate(baseRate)} | {Row2(b)} | 1.00 | |");
            if (sinceRelease.TryGetValue(benchmark.FullName, out var drift) && drift > ReleaseCreepPct)
            {
                gate += string.Create(CultureInfo.InvariantCulture, $" · ⚠️ {drift:+0.0;−0.0;0.0}% since {release!.Release}");
            }

            rows.AppendLine(CultureInfo.InvariantCulture, $"| {prefix}head | {Row(h)} | {ratio} | {Rate(headRate)} | {Row2(h)} | {allocRatio} | {gate} |");
        }

        var sinceLine = new StringBuilder();
        AppendSinceRelease(sinceLine, cases, headRun, release, sinceRelease);
        AppendBackgroundCost(notes, type, cases, baseRun, headRun, labels);

        var failed = failures.Count > failuresBefore;
        var section = new StringBuilder();

        // Read by the report job to count passes without re-deriving anything.
        section.AppendLine(failed ? "<!-- verdict: fail -->" : "<!-- verdict: pass -->");

        var driftNote = string.Empty;
        if (drifted > 0)
        {
            var noun = drifted == 1 ? "case" : "cases";
            driftNote = string.Create(CultureInfo.InvariantCulture, $" · ⚠️ {drifted} {noun} drifted since {release!.Release}");
        }

        var database = DatabaseLabel is null ? string.Empty : $" · {DatabaseLabel}";
        var baseLabel = string.IsNullOrWhiteSpace(BaseLabel) ? string.Empty : $" · base {BaseLabel}";
        section.AppendLine(CultureInfo.InvariantCulture, $"### {(failed ? "❌" : "✅")} {scenario?.Title ?? type?.Name ?? cases[0].TypeName}{database}{baseLabel}{driftNote}");
        section.AppendLine();

        if (scenario is null)
        {
            section.AppendLine("_No description. Add `[CiScenario]` to the benchmark class so this report can say what it tests._");
        }
        else
        {
            section.AppendLine(scenario.Measures);
            section.AppendLine();
            section.AppendLine(CultureInfo.InvariantCulture, $"**Why it matters:** {scenario.Why}");
        }

        section.AppendLine();

        // The environment block BenchmarkDotNet prints above every summary. Per scenario, not per run:
        // each scenario ran on its own runner, and the hardware under one is not the hardware under the next.
        var environment = cases
            .Select(x => headRun.TryGetValue(x.FullName, out var r) ? r : null)
            .FirstOrDefault(x => x?.Host is not null);
        if (environment?.Host is { } host)
        {
            section.AppendLine("```");
            section.AppendLine(CultureInfo.InvariantCulture, $"BenchmarkDotNet v{host.BenchmarkDotNetVersion}, {host.OsVersion}");
            section.AppendLine(CultureInfo.InvariantCulture, $"{host.ProcessorName}, {host.PhysicalProcessorCount?.ToString(CultureInfo.InvariantCulture) ?? "?"} CPU, {host.LogicalCoreCount?.ToString(CultureInfo.InvariantCulture) ?? "?"} logical and {host.PhysicalCoreCount?.ToString(CultureInfo.InvariantCulture) ?? "?"} physical cores");
            section.AppendLine(CultureInfo.InvariantCulture, $".NET SDK {host.DotNetCliVersion}, {host.RuntimeVersion}, {host.Architecture}");
            if (environment.Job is { Length: > 0 } job)
            {
                section.AppendLine(job);
            }

            section.AppendLine("```");
            section.AppendLine();
        }

        section.AppendLine("| " + header + " |");
        section.AppendLine("| " + alignment + " |");
        section.Append(rows);
        section.Append(sinceLine);
        if (notes.Length > 0)
        {
            section.AppendLine();
            section.Append(notes);
        }

        // Without this the table invites its own misreading: a PostgreSQL row reading 14 beside a SQL
        // Server row reading 4 looks like a verdict on the providers, when the two numbers come from
        // different instruments — pg_stat_statements counts every execution, dm_exec_query_stats only
        // those whose plans are still cached. Each row is evidence about ITSELF across two commits.
        if (cases.Any(x => x.Parameters.Any(y => string.Equals(y.Value, "SqlServer", StringComparison.Ordinal)))
            && cases.Any(x => x.Parameters.Any(y => string.Equals(y.Value, "PostgreSql", StringComparison.Ordinal))))
        {
            section.AppendLine();
            section.AppendLine("<sub>PostgreSQL and SQL Server count statements with different instruments, so compare base with head within a provider, not across providers.</sub>");
        }

        return section.ToString();
    }

    /// <summary>Mean, Error, StdDev — in BenchmarkDotNet's own time format.</summary>
    private static string Row(BdnResult result) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Seconds(result.Mean)} | {Spread(result.Error)} | {Spread(result.StdDev)}");

    private static string Rate(double? jobsPerSecond) =>
        jobsPerSecond is null ? "—" : jobsPerSecond.Value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>The runs' database counters per job, then allocation, as BenchmarkDotNet prints its metric columns.</summary>
    private static string Row2(BdnResult result) =>
        string.Join(
            " | ",
            FormatMetric(result.StatementsPerJob, "N2"),
            FormatMetric(result.BuffersPerJob, "N1"),
            result.WalBytesPerJob is null ? "—" : FormatBytes(result.WalBytesPerJob.Value),
            FormatMetric(result.DbTimePerJob, "N3"),
            FormatMetric(result.RoundTripsPerJob, "N2"),
            FormatMetric(result.TransactionsPerJob, "N2"),
            FormatLockWait(result),
            result.Bytes is null ? "NA" : FormatBytes(result.Bytes.Value));

    // Lock wait, with the window's deadlocks beside it when there were any.
    private static string FormatLockWait(BdnResult result)
    {
        var wait = FormatMetric(result.LockWaitMsPerJob, "N3");

        return result.Deadlocks is > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{wait} ({result.Deadlocks:N0} deadlocks)")
            : wait;
    }

    private static string FormatMetric(double? value, string format) =>
        value is null ? "—" : value.Value.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>BenchmarkDotNet's size format: B, KB, MB with two decimals.</summary>
    private static string FormatBytes(double bytes)
    {
        if (bytes >= 1024 * 1024)
        {
            return (bytes / 1024 / 1024).ToString("N2", CultureInfo.InvariantCulture) + " MB";
        }

        return bytes >= 1024
            ? (bytes / 1024).ToString("N2", CultureInfo.InvariantCulture) + " KB"
            : bytes.ToString("N0", CultureInfo.InvariantCulture) + " B";
    }

    /// <summary>BenchmarkDotNet reports nanoseconds; its summary shows s or ms to three decimals.</summary>
    // An undefined spread (too few iterations to have one) is kept as zero: it is written to the
    // history file as JSON, which has no NaN.
    private static double FiniteOrZero(double? value) =>
        value is { } number && double.IsFinite(number) ? number : 0;

    // A spread of exactly zero means there was none to measure, not that the runs agreed to the nanosecond.
    private static string Spread(double nanoseconds) =>
        nanoseconds <= 0 ? "—" : Seconds(nanoseconds);

    private static string Seconds(double nanoseconds) =>
        nanoseconds >= 1e9
            ? (nanoseconds / 1e9).ToString("N3", CultureInfo.InvariantCulture) + " s"
            : (nanoseconds / 1e6).ToString("N3", CultureInfo.InvariantCulture) + " ms";

    private static string ParameterCell(BdnCase benchmark, string parameter, List<CaseLabelAttribute> labels)
    {
        var value = benchmark.Parameters
            .Where(x => string.Equals(x.Name, parameter, StringComparison.Ordinal))
            .Select(x => x.Value)
            .FirstOrDefault() ?? "?";

        return labels
            .Where(x => string.Equals(x.Parameter, parameter, StringComparison.Ordinal))
            .Where(x => string.Equals(x.Value, value, StringComparison.Ordinal))
            .Select(x => x.Label)
            .FirstOrDefault() ?? value;
    }

    /// <summary>
    /// Job rows per second: the job rows the diagnoser counted per measured operation, over the operation's mean
    /// time, so the rate and the per-job columns divide by the same rows. A record from before that count
    /// falls back to the case's <c>JobCount</c> parameter.
    /// </summary>
    private static double? RateOf(BdnResult result, BdnCase benchmark)
    {
        if (result.Mean <= 0)
        {
            return null;
        }

        double? perOperation = result.JobRows is { } rows && result.Operations > 0 ? rows / result.Operations : JobCountOf(benchmark);

        return perOperation / (result.Mean / 1e9);
    }

    private static string Share(BdnResult result) =>
        result.SmallestServerShare is { } share
            ? string.Create(CultureInfo.InvariantCulture, $"{share * 100:0}% of {result.ServerJobs:N0}")
            : "—";

    /// <summary>
    /// For a scenario that adds background work to a drain (<see cref="BackgroundCostAttribute"/>): what the
    /// background work cost PER SECOND of the drain, each case against the scenario's case without it, on each
    /// side. Per job it is that per-second work spread over the drain, so a faster drain read as a cheaper
    /// dashboard; per second it is what the database spends while the work runs, which a drain's speed does
    /// not move.
    /// </summary>
    private static void AppendBackgroundCost(
        StringBuilder notes,
        Type? type,
        List<BdnCase> cases,
        Dictionary<string, BdnResult> baseRun,
        Dictionary<string, BdnResult> headRun,
        List<CaseLabelAttribute> labels)
    {
        if (type?.GetCustomAttribute<BackgroundCostAttribute>() is not { } background)
        {
            return;
        }

        var without = $"{background.Parameter}: {background.Without}";
        foreach (var benchmark in cases)
        {
            var value = benchmark.Parameters.FirstOrDefault(x => string.Equals(x.Name, background.Parameter, StringComparison.Ordinal)).Value;
            if (value is null || string.Equals(value, background.Without, StringComparison.Ordinal))
            {
                continue;
            }

            var alone = benchmark.FullName.Replace($"{background.Parameter}: {value}", without, StringComparison.Ordinal);
            var cells = new[] { (Side: "base", Run: baseRun), (Side: "head", Run: headRun) }
                .Select(x => (x.Side, Cost: BackgroundCostPerSecond(x.Run, benchmark.FullName, alone, benchmark)))
                .Select(x => x.Cost is { } cost
                    ? string.Create(CultureInfo.InvariantCulture, $"{x.Side} {cost.Statements:N0} statements/s, {cost.DbMs:N0} DB ms/s")
                    : $"{x.Side} —");
            var name = string.Join(", ", benchmark.Parameters
                .Where(x => !string.Equals(x.Name, "JobCount", StringComparison.Ordinal))
                .Select(x => ParameterCell(benchmark, x.Name, labels)));
            notes.AppendLine(CultureInfo.InvariantCulture, $"<sub>Background work, per second of drain ({name}, beyond the drain alone): {string.Join("; ", cells)}. Reported, not gated.</sub>");
            notes.AppendLine();
        }
    }

    private static (double Statements, double DbMs)? BackgroundCostPerSecond(Dictionary<string, BdnResult> run, string withWork, string alone, BdnCase benchmark)
    {
        if (!run.TryGetValue(withWork, out var loaded) || !run.TryGetValue(alone, out var bare)
            || loaded.StatementsPerJob is not { } loadedStatements || bare.StatementsPerJob is not { } bareStatements
            || loaded.Mean <= 0)
        {
            return null;
        }

        // Per job times jobs per second of drain: per second of the loaded drain.
        var jobsPerSecond = RateOf(loaded, benchmark) ?? 0;
        var dbMs = (loaded.DbTimePerJob ?? 0) - (bare.DbTimePerJob ?? 0);

        return ((loadedStatements - bareStatements) * jobsPerSecond, dbMs * jobsPerSecond);
    }

    private static bool DatabaseConfigured(BdnCase benchmark) =>
        Environment.GetEnvironmentVariable(IsSqlServer(benchmark)
            ? PostgresServerFixture.SqlServerConnectionStringVariable
            : PostgresServerFixture.ConnectionStringVariable) is not null;

    private static bool IsSqlServer(BdnCase benchmark) =>
        benchmark.Parameters.Any(x =>
            string.Equals(x.Name, "Provider", StringComparison.Ordinal)
            && string.Equals(x.Value, "SqlServer", StringComparison.OrdinalIgnoreCase));

    private static int? JobCountOf(BdnCase benchmark) =>
        benchmark.Parameters
            .Where(x => string.Equals(x.Name, "JobCount", StringComparison.Ordinal))
            .Select(x => int.TryParse(x.Value, CultureInfo.InvariantCulture, out var n) ? n : (int?)null)
            .FirstOrDefault();

    private static string MissingSide(long? baseBytes, long? headBytes)
    {
        if (baseBytes is null && headBytes is null)
        {
            return "either side";
        }

        return baseBytes is null ? "the base" : "the head";
    }

    /// <summary>
    /// Reads every <c>*-report-full-compressed.json</c> under a BenchmarkDotNet artifacts directory.
    /// <para>
    /// The property names are BDN's own PascalCase, so this deliberately does NOT use
    /// <see cref="JsonOptions"/> — that one carries the snake_case policy the lab's own files use.
    /// </para>
    /// </summary>
    private static Dictionary<string, BdnResult> ReadBdn(string directory)
    {
        var results = new Dictionary<string, BdnResult>(StringComparer.Ordinal);
        var files = Directory.GetFiles(directory, "*-report-full-compressed.json", SearchOption.AllDirectories);

        if (files.Length == 0)
        {
            throw new InvalidOperationException($"No BenchmarkDotNet reports under {directory}.");
        }

        foreach (var file in files)
        {
            var report = JsonSerializer.Deserialize<BdnReport>(File.ReadAllText(file), BdnOptions);
            foreach (var benchmark in report?.Benchmarks ?? [])
            {
                if (benchmark.FullName is null)
                {
                    continue;
                }

                results[benchmark.FullName] = new BdnResult(
                    benchmark.Memory?.BytesAllocatedPerOperation,
                    benchmark.Statistics?.Mean ?? 0,
                    FiniteOrZero(benchmark.Statistics?.ConfidenceInterval?.Margin),
                    FiniteOrZero(benchmark.Statistics?.StandardDeviation),
                    MetricOf(benchmark, "StatementsPerJob") is null && MetricOf(benchmark, "StatementsPerSecond") is not null,
                    MetricOf(benchmark, "StatementsPerJob") ?? MetricOf(benchmark, "StatementsPerSecond"),
                    MetricOf(benchmark, "BuffersPerJob") ?? MetricOf(benchmark, "BuffersPerSecond"),
                    MetricOf(benchmark, "WalBytesPerJob") ?? MetricOf(benchmark, "WalBytesPerSecond"),
                    JobLine(benchmark.DisplayInfo),
                    report?.HostEnvironmentInfo,
                    MetricOf(benchmark, "DbTimePerJob") ?? MetricOf(benchmark, "DbTimePerSecond"),
                    MetricOf(benchmark, "RoundTripsPerJob") ?? MetricOf(benchmark, "RoundTripsPerSecond"),
                    MetricOf(benchmark, "TransactionsPerJob") ?? MetricOf(benchmark, "TransactionsPerSecond"),
                    MetricOf(benchmark, "LockWaitMsPerJob") ?? MetricOf(benchmark, "LockWaitMsPerSecond"),
                    MetricOf(benchmark, "Deadlocks"),
                    MetricOf(benchmark, "JobRows"),
                    MetricOf(benchmark, "SmallestServerShare"),
                    MetricOf(benchmark, "ServerJobs"),
                    ActualOperations(benchmark));
            }
        }

        return results;
    }

    /// <summary>
    /// BenchmarkDotNet's job line, from the case's display text:
    /// <c>ShortRun(InvocationCount=1, IterationCount=3)</c> becomes <c>Job=ShortRun  InvocationCount=1  IterationCount=3</c>.
    /// </summary>
    private static string? JobLine(string? displayInfo)
    {
        var colon = displayInfo?.IndexOf(": ", StringComparison.Ordinal) ?? -1;
        var bracket = displayInfo?.IndexOf(" [", StringComparison.Ordinal) ?? -1;
        if (displayInfo is null || colon < 0)
        {
            return null;
        }

        var job = (bracket > colon ? displayInfo[(colon + 2)..bracket] : displayInfo[(colon + 2)..]).Trim();
        var open = job.IndexOf('(', StringComparison.Ordinal);
        if (open < 0 || !job.EndsWith(')'))
        {
            return "Job=" + job;
        }

        var settings = job[(open + 1)..^1].Split(", ", StringSplitOptions.RemoveEmptyEntries);

        return "Job=" + job[..open] + "  " + string.Join("  ", settings);
    }

    // The operations of the measured window (the workload's actual iterations): what the diagnoser's job rows
    // were counted over.
    private static long ActualOperations(BdnBenchmark benchmark) =>
        benchmark.Measurements?
            .Where(x => string.Equals(x.IterationMode, "Workload", StringComparison.Ordinal))
            .Where(x => string.Equals(x.IterationStage, "Actual", StringComparison.Ordinal))
            .Sum(x => x.Operations) ?? 0;

    private static double? MetricOf(BdnBenchmark benchmark, string id) =>
        benchmark.Metrics
            ?.FirstOrDefault(x => string.Equals(x.Descriptor?.Id, id, StringComparison.Ordinal))
            ?.Value;

    private enum Direction
    {
        Higher = 1,
        Lower = 2,
    }

    /// <summary>A BenchmarkDotNet case name, split: <c>Ns.Type.Method(A: 1, B: x)</c>.</summary>
    private sealed class BdnCase
    {
        public BdnCase(string fullName)
        {
            FullName = fullName;
            var open = fullName.IndexOf('(', StringComparison.Ordinal);
            var qualified = open < 0 ? fullName : fullName[..open];
            var lastDot = qualified.LastIndexOf('.');
            TypeName = lastDot < 0 ? qualified : qualified[..lastDot];
            Method = lastDot < 0 ? qualified : qualified[(lastDot + 1)..];
            Parameters = open < 0
                ? []
                : [.. fullName[(open + 1)..fullName.LastIndexOf(')')]
                    .Split(", ", StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Split(": ", 2))
                    .Where(x => x.Length == 2)
                    .Select(x => (Name: x[0], Value: x[1])),];
            Short = TypeName[(TypeName.LastIndexOf('.') + 1)..] + "." + Method + (open < 0 ? string.Empty : fullName[open..]);
        }

        public string FullName { get; }

        public string TypeName { get; }

        public string Method { get; }

        public List<(string Name, string Value)> Parameters { get; }

        /// <summary>Without the namespace, for the failure lines in the log.</summary>
        public string Short { get; }
    }

    private sealed record BdnResult(
        long? Bytes,
        double Mean,
        double Error,
        double StdDev,
        bool PerSecond,
        double? StatementsPerJob,
        double? BuffersPerJob,
        double? WalBytesPerJob,
        string? Job,
        BdnHost? Host,
        double? DbTimePerJob = null,
        double? RoundTripsPerJob = null,
        double? TransactionsPerJob = null,
        double? LockWaitMsPerJob = null,
        double? Deadlocks = null,
        double? JobRows = null,
        double? SmallestServerShare = null,
        double? ServerJobs = null,
        long Operations = 0);

    private sealed record BdnHost(
        string? BenchmarkDotNetVersion,
        string? OsVersion,
        string? ProcessorName,
        // Nullable: BenchmarkDotNet leaves these null where it cannot read them (seen on Windows hosts),
        // and a non-nullable int made the whole report unreadable over one hardware field.
        int? PhysicalProcessorCount,
        int? PhysicalCoreCount,
        int? LogicalCoreCount,
        string? RuntimeVersion,
        string? Architecture,
        string? DotNetCliVersion);

    private sealed record BdnReport(List<BdnBenchmark>? Benchmarks, BdnHost? HostEnvironmentInfo);

    private sealed record BdnBenchmark(string? FullName, string? DisplayInfo, BdnStatistics? Statistics, BdnMemory? Memory, List<BdnMetric>? Metrics, List<BdnMeasurement>? Measurements);

    private sealed record BdnMeasurement(string? IterationMode, string? IterationStage, long Operations);

    private sealed record BdnMetric(double Value, BdnMetricDescriptor? Descriptor);

    private sealed record BdnMetricDescriptor(string? Id);

    private sealed record BdnStatistics(double? Mean, double? StandardDeviation, BdnConfidenceInterval? ConfidenceInterval);

    // BenchmarkDotNet's "Error" column: half the 99.9% confidence interval.
    private sealed record BdnConfidenceInterval(double? Margin);

    // Nullable, because BenchmarkDotNet writes null for a case that produced no measurement — an arm
    // that errored, or was reported NA. Reading it as a long crashed the comparator outright, which
    // turned "one arm did not run" into "the whole comparison is unavailable" and hid which arm it
    // was.
    private sealed record BdnMemory(long? BytesAllocatedPerOperation);

    private sealed record MetricPolicy(string Metric, bool Gated, double? TolerancePct, Direction BadDirection);
}
