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
public static class PerfCompare
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions BdnOptions = new() { PropertyNameCaseInsensitive = true };

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
    public static int RunBdn(string basePath, string headPath, double tolerancePct, string? summaryPath)
    {
        var baseRun = ReadBdn(basePath);
        var headRun = ReadBdn(headPath);

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
            report.Append(RenderScenario(type, cases, baseRun, headRun, tolerancePct, failures));
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
        List<string> failures)
    {
        var scenario = type?.GetCustomAttribute<CiScenarioAttribute>();
        var labels = type?.GetCustomAttributes<CaseLabelAttribute>().ToList() ?? [];
        var failuresBefore = failures.Count;
        var rows = new StringBuilder();

        // A parameter with one value is the same on every row: it belongs to the scenario, not the case.
        var varying = cases
            .SelectMany(x => x.Parameters)
            .GroupBy(x => x.Name, StringComparer.Ordinal)
            .Where(x => x.Select(y => y.Value).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(x => x.Key)
            .ToList();
        var severalMethods = cases.Select(x => x.Method).Distinct(StringComparer.Ordinal).Count() > 1;
        var declared = headRun.Keys.Concat(baseRun.Keys).Distinct(StringComparer.Ordinal).ToList();

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
        var perSecond = cases.All(x => JobCountOf(x) is null);
        var per = perSecond ? "s" : "job";
        var header = string.Join(" | ", columns.Append("Commit")) + $" | Mean | Error | StdDev | Ratio | Jobs/s | Statements/{per} | Buffers/{per} | WAL/{per} | Allocated | Alloc Ratio | Gate";
        var alignment = string.Join(" | ", columns.Append("Commit").Select(_ => ":---")) + " | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | :---";

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
                rows.AppendLine(CultureInfo.InvariantCulture, $"| {prefix}{side} | | | | | | | | | | | ➖ |");
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
                rows.AppendLine(CultureInfo.InvariantCulture, $"| {prefix}base | {(b.Bytes is null ? "NA" : Seconds(b.Mean))} | | | | | | | | | | |");
                rows.AppendLine(CultureInfo.InvariantCulture, $"| {prefix}head | {(h.Bytes is null ? "NA" : Seconds(h.Mean))} | | | | | | | | | | ⚠️ no measurement from {missing} |");
                failures.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{benchmark.Short}: {missing} produced no measurement, so nothing was compared"));

                continue;
            }

            double? stmtChange = b.StatementsPerJob is null or 0 || h.StatementsPerJob is null
                ? null
                : (h.StatementsPerJob.Value - b.StatementsPerJob.Value) / b.StatementsPerJob.Value * 100;
            var jobs = JobCountOf(benchmark);
            double? baseRate = jobs is null || b.Mean <= 0 ? null : jobs.Value / (b.Mean / 1e9);
            double? headRate = jobs is null || h.Mean <= 0 ? null : jobs.Value / (h.Mean / 1e9);
            double? rateChange = baseRate is null || headRate is null ? null : (headRate.Value - baseRate.Value) / baseRate.Value * 100;

            // Statements and WAL per job are the gates; see StatementTolerancePct and WalTolerancePct
            // for where the lines sit. Allocations are REPORTED, not gated: across the same no-op runs
            // they moved up to 4.9%, too close to any threshold worth having. Buffers moved up to 55%,
            // with cache state and vacuum timing, and are reported for a human to read.
            // Per-second counters (an idle server) have not had their noise measured: a background tick
            // landing just inside or outside the window moves them. Reported until calibrated.
            if (perSecond)
            {
                stmtChange = null;
            }

            var gate = perSecond ? "reported" : "✅";
            if (stmtChange is not null)
            {
                gate = string.Create(CultureInfo.InvariantCulture, $"✅ stmt {stmtChange:+0.0;−0.0;0.0}%");
            }

            if (stmtChange > StatementTolerancePct)
            {
                gate = string.Create(CultureInfo.InvariantCulture, $"❌ stmt {stmtChange:+0.0;−0.0;0.0}% (limit +{StatementTolerancePct:0}%)");
                failures.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{benchmark.Short}: statements/job {b.StatementsPerJob:N2} -> {h.StatementsPerJob:N2} ({stmtChange:+0.0;-0.0;0.0}%)"));
            }

            double? walChange = perSecond || b.WalBytesPerJob is null or 0 || h.WalBytesPerJob is null
                ? null
                : (h.WalBytesPerJob.Value - b.WalBytesPerJob.Value) / b.WalBytesPerJob.Value * 100;
            if (walChange > WalTolerancePct)
            {
                gate = string.Create(CultureInfo.InvariantCulture, $"❌ WAL {walChange:+0.0;−0.0;0.0}% (limit +{WalTolerancePct:0}%)");
                failures.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{benchmark.Short}: WAL/job {b.WalBytesPerJob:N0} -> {h.WalBytesPerJob:N0} bytes ({walChange:+0.0;-0.0;0.0}%)"));
            }

            // Throughput is far too noisy on a shared runner to gate a small change, but a collapse is
            // not noise. See ThroughputCollapseFactor for where the line sits and why.
            if (baseRate is not null && headRate is not null && headRate.Value * ThroughputCollapseFactor < baseRate.Value)
            {
                gate = string.Create(CultureInfo.InvariantCulture, $"❌ jobs/s {baseRate.Value / headRate.Value:0.0}× slower (limit {ThroughputCollapseFactor:0}×)");
                failures.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{benchmark.Short}: jobs/s {baseRate:N0} -> {headRate:N0} ({rateChange:+0.0;-0.0;0.0}%)"));
            }

            var ratio = b.Mean <= 0 ? "?" : (h.Mean / b.Mean).ToString("N2", CultureInfo.InvariantCulture);
            var allocRatio = b.Bytes.Value <= 0 ? "?" : (h.Bytes.Value / (double)b.Bytes.Value).ToString("N2", CultureInfo.InvariantCulture);

            // Reported, not gated: a large rise is still worth a glance, so it is marked, not failed.
            if (b.Bytes.Value > 0 && (h.Bytes.Value - b.Bytes.Value) / (double)b.Bytes.Value * 100 > tolerancePct)
            {
                allocRatio += " ↑";
            }

            rows.AppendLine(CultureInfo.InvariantCulture, $"| {prefix}base | {Row(b)} | 1.00 | {Rate(baseRate)} | {Row2(b)} | 1.00 | |");
            rows.AppendLine(CultureInfo.InvariantCulture, $"| {prefix}head | {Row(h)} | {ratio} | {Rate(headRate)} | {Row2(h)} | {allocRatio} | {gate} |");
        }

        var failed = failures.Count > failuresBefore;
        var section = new StringBuilder();

        // Read by the report job to count passes without re-deriving anything.
        section.AppendLine(failed ? "<!-- verdict: fail -->" : "<!-- verdict: pass -->");
        section.AppendLine(CultureInfo.InvariantCulture, $"### {(failed ? "❌" : "✅")} {scenario?.Title ?? type?.Name ?? cases[0].TypeName}");
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
            section.AppendLine(CultureInfo.InvariantCulture, $"{host.ProcessorName}, {host.PhysicalProcessorCount} CPU, {host.LogicalCoreCount} logical and {host.PhysicalCoreCount} physical cores");
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
            $"{Seconds(result.Mean)} | {Seconds(result.Error)} | {Seconds(result.StdDev)}");

    private static string Rate(double? jobsPerSecond) =>
        jobsPerSecond is null ? "—" : jobsPerSecond.Value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>The runs' database counters per job, then allocation, as BenchmarkDotNet prints its metric columns.</summary>
    private static string Row2(BdnResult result) =>
        string.Join(
            " | ",
            FormatMetric(result.StatementsPerJob, "N2"),
            FormatMetric(result.BuffersPerJob, "N1"),
            result.WalBytesPerJob is null ? "—" : FormatBytes(result.WalBytesPerJob.Value),
            result.Bytes is null ? "NA" : FormatBytes(result.Bytes.Value));

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
                    benchmark.Statistics?.ConfidenceInterval?.Margin ?? 0,
                    benchmark.Statistics?.StandardDeviation ?? 0,
                    MetricOf(benchmark, "StatementsPerJob") ?? MetricOf(benchmark, "StatementsPerSecond"),
                    MetricOf(benchmark, "BuffersPerJob") ?? MetricOf(benchmark, "BuffersPerSecond"),
                    MetricOf(benchmark, "WalBytesPerJob") ?? MetricOf(benchmark, "WalBytesPerSecond"),
                    JobLine(benchmark.DisplayInfo),
                    report?.HostEnvironmentInfo);
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
        double? StatementsPerJob,
        double? BuffersPerJob,
        double? WalBytesPerJob,
        string? Job,
        BdnHost? Host);

    private sealed record BdnHost(
        string? BenchmarkDotNetVersion,
        string? OsVersion,
        string? ProcessorName,
        int PhysicalProcessorCount,
        int PhysicalCoreCount,
        int LogicalCoreCount,
        string? RuntimeVersion,
        string? Architecture,
        string? DotNetCliVersion);

    private sealed record BdnReport(List<BdnBenchmark>? Benchmarks, BdnHost? HostEnvironmentInfo);

    private sealed record BdnBenchmark(string? FullName, string? DisplayInfo, BdnStatistics? Statistics, BdnMemory? Memory, List<BdnMetric>? Metrics);

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
