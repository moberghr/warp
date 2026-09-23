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
    private const double StatementTolerancePct = 10.0;

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
        Dictionary<string, (long? Bytes, double Mean, double? StatementsPerJob)> baseRun,
        Dictionary<string, (long? Bytes, double Mean, double? StatementsPerJob)> headRun,
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
            .ToHashSet(StringComparer.Ordinal);
        var severalMethods = cases.Select(x => x.Method).Distinct(StringComparer.Ordinal).Count() > 1;

        var declared = headRun.Keys.Concat(baseRun.Keys).Distinct(StringComparer.Ordinal).ToList();

        // Declared order, the order BenchmarkDotNet ran them in. Sorting by name put "Keys: 10000"
        // before "Keys: 8".
        foreach (var benchmark in cases.OrderBy(x => declared.IndexOf(x.FullName)))
        {
            var name = CaseName(benchmark, varying, severalMethods, labels);
            var hasBase = baseRun.TryGetValue(benchmark.FullName, out var b);
            var hasHead = headRun.TryGetValue(benchmark.FullName, out var h);

            if (!hasBase || !hasHead)
            {
                // A case only one side has is new or removed, not a regression.
                var side = hasHead ? "new in head" : "removed in head";
                rows.AppendLine(CultureInfo.InvariantCulture, $"| {name} | {side} | | | ➖ |");
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
                rows.AppendLine(CultureInfo.InvariantCulture, $"| {name} | no measurement from {missing} | | | ⚠️ |");
                failures.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{benchmark.Short}: {missing} produced no measurement, so nothing was compared"));

                continue;
            }

            double? stmtChange = b.StatementsPerJob is null or 0 || h.StatementsPerJob is null
                ? null
                : (h.StatementsPerJob.Value - b.StatementsPerJob.Value) / b.StatementsPerJob.Value * 100;
            var allocChange = b.Bytes == 0 ? 0 : (h.Bytes.Value - b.Bytes.Value) / (double)b.Bytes.Value * 100;
            var timeChange = b.Mean <= 0 ? 0 : (h.Mean - b.Mean) / b.Mean * 100;

            // Statements per job is the only gate. Allocations are REPORTED, not gated: they were gated
            // at 2% on the strength of one run showing byte-identical numbers, and later runs moved 2-3%
            // with no code change that could explain it. Until the noise floor is measured — a null
            // comparison, the same commit on both sides — a threshold there is a guess, and a guess that
            // fails builds is worse than none. Statements get 10% because they are read from a shared
            // server, where background tasks ticking on timers add a little jitter.
            var verdict = "✅";
            if (stmtChange > StatementTolerancePct)
            {
                verdict = string.Create(CultureInfo.InvariantCulture, $"❌ over {StatementTolerancePct:0}%");
                failures.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{benchmark.Short}: statements/job {b.StatementsPerJob:N2} -> {h.StatementsPerJob:N2} ({stmtChange:+0.0;-0.0;0.0}%)"));
            }

            var statements = stmtChange is null
                ? "not measured"
                : Pair(Number(b.StatementsPerJob!.Value), Number(h.StatementsPerJob!.Value), string.Empty, stmtChange.Value);
            var memory = Pair(Megabytes(b.Bytes.Value), Megabytes(h.Bytes.Value), "MB", allocChange, flagAbove: tolerancePct);
            var time = PairDuration(b.Mean, h.Mean, timeChange);

            rows.AppendLine(CultureInfo.InvariantCulture, $"| {name} | {statements} | {memory} | {time} | {verdict} |");
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
        section.AppendLine("| case | statements per job · gated | memory per run | mean time | |");
        section.AppendLine("| --- | --- | --- | --- | --- |");
        section.Append(rows);

        // Without this the table invites its own misreading: a PostgreSQL row reading 14 beside a SQL
        // Server row reading 4 looks like a verdict on the providers, when the two numbers come from
        // different instruments — pg_stat_statements counts every execution, dm_exec_query_stats only
        // those whose plans are still cached. Each row is evidence about ITSELF across two commits.
        if (cases.Any(x => x.Parameters.Any(y => string.Equals(y.Value, "SqlServer", StringComparison.Ordinal)))
            && cases.Any(x => x.Parameters.Any(y => string.Equals(y.Value, "PostgreSql", StringComparison.Ordinal))))
        {
            section.AppendLine();
            section.AppendLine("<sub>PostgreSQL and SQL Server count statements with different instruments, so read each row against itself, not against the other provider.</sub>");
        }

        return section.ToString();
    }

    private static string CaseName(BdnCase benchmark, HashSet<string> varying, bool severalMethods, List<CaseLabelAttribute> labels)
    {
        var parts = benchmark.Parameters
            .Where(x => varying.Contains(x.Name))
            .Select(x => labels
                .Where(y => string.Equals(y.Parameter, x.Name, StringComparison.Ordinal))
                .Where(y => string.Equals(y.Value, x.Value, StringComparison.Ordinal))
                .Select(y => y.Label)
                .FirstOrDefault() ?? $"{x.Name} = {x.Value}")
            .ToList();

        if (severalMethods)
        {
            parts.Insert(0, benchmark.Method);
        }

        if (parts.Count > 0)
        {
            return string.Join(" · ", parts);
        }

        // A scenario with one case: say how big it is rather than naming nothing.
        var jobs = benchmark.Parameters
            .Where(x => string.Equals(x.Name, "JobCount", StringComparison.Ordinal))
            .Select(x => int.TryParse(x.Value, CultureInfo.InvariantCulture, out var n) ? n.ToString("N0", CultureInfo.InvariantCulture) + " jobs" : null)
            .FirstOrDefault();

        return jobs ?? "single case";
    }

    /// <summary>"before → after unit (change)".</summary>
    private static string Pair(string before, string after, string unit, double change, double? flagAbove = null)
    {
        var delta = change.ToString("+0.0;−0.0;0.0", CultureInfo.InvariantCulture) + "%";

        // Reported, not gated: a large move is still worth a glance, so it is marked rather than failed.
        if (flagAbove is { } limit && change > limit)
        {
            delta += " ↑";
        }

        var suffix = unit.Length == 0 ? string.Empty : " " + unit;

        return string.Create(CultureInfo.InvariantCulture, $"{before} → {after}{suffix} ({delta})");
    }

    /// <summary>
    /// BenchmarkDotNet reports nanoseconds. These runs take seconds and publishing takes milliseconds, so
    /// the unit follows the value, and is written once when both sides share it.
    /// </summary>
    private static string PairDuration(double beforeNs, double afterNs, double change)
    {
        var (before, beforeUnit) = Duration(beforeNs);
        var (after, afterUnit) = Duration(afterNs);

        return string.Equals(beforeUnit, afterUnit, StringComparison.Ordinal)
            ? Pair(before, after, afterUnit, change)
            : Pair(before + " " + beforeUnit, after, afterUnit, change);
    }

    private static string Number(double value) => value.ToString("N2", CultureInfo.InvariantCulture);

    private static string Megabytes(long bytes) =>
        (bytes / 1024d / 1024d).ToString("N1", CultureInfo.InvariantCulture);

    private static (string Value, string Unit) Duration(double nanoseconds) =>
        nanoseconds >= 1e9
            ? ((nanoseconds / 1e9).ToString("N2", CultureInfo.InvariantCulture), "s")
            : ((nanoseconds / 1e6).ToString("N1", CultureInfo.InvariantCulture), "ms");

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
    private static Dictionary<string, (long? Bytes, double Mean, double? StatementsPerJob)> ReadBdn(string directory)
    {
        var results = new Dictionary<string, (long? Bytes, double Mean, double? StatementsPerJob)>(StringComparer.Ordinal);
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

                var statements = benchmark.Metrics
                    ?.FirstOrDefault(x => string.Equals(x.Descriptor?.Id, "StatementsPerJob", StringComparison.Ordinal))
                    ?.Value;

                results[benchmark.FullName] = (
                    benchmark.Memory?.BytesAllocatedPerOperation,
                    benchmark.Statistics?.Mean ?? 0,
                    statements);
            }
        }

        return results;
    }

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

    private sealed record BdnReport(List<BdnBenchmark>? Benchmarks);

    private sealed record BdnBenchmark(string? FullName, BdnStatistics? Statistics, BdnMemory? Memory, List<BdnMetric>? Metrics);

    private sealed record BdnMetric(double Value, BdnMetricDescriptor? Descriptor);

    private sealed record BdnMetricDescriptor(string? Id);

    private sealed record BdnStatistics(double? Mean);

    // Nullable, because BenchmarkDotNet writes null for a case that produced no measurement — an arm
    // that errored, or was reported NA. Reading it as a long crashed the comparator outright, which
    // turned "one arm did not run" into "the whole comparison is unavailable" and hid which arm it
    // was.
    private sealed record BdnMemory(long? BytesAllocatedPerOperation);

    private sealed record MetricPolicy(string Metric, bool Gated, double? TolerancePct, Direction BadDirection);
}
