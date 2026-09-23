using System.Globalization;
using System.Text;
using System.Text.Json;

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

        var report = new StringBuilder();
        var failures = new List<string>();

        report.AppendLine("### Benchmarks — allocations and database statements");
        report.AppendLine();
        report.AppendLine("| benchmark | base alloc | head alloc | alloc | base stmt/job | head stmt/job | stmt | verdict |");
        report.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |");

        foreach (var head in headRun.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (!baseRun.TryGetValue(head.Key, out var b))
            {
                // A benchmark the base does not have is new, not a regression.
                report.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"| `{Shorten(head.Key)}` | — | {Format(head.Value.Bytes)} | new | — | {Format(head.Value.StatementsPerJob)} | new | new |");
                continue;
            }

            var h = head.Value;

            // A side that measured NOTHING is not a pass. BenchmarkDotNet writes null for an arm that
            // errored or timed out, and coercing that to zero made the row read "base 0, head 346M,
            // 0.0%, ok" — a green verdict for a comparison that never happened. Left alone it would
            // hide the gate breaking entirely: if the base build stopped producing results, every pull
            // request would sail through reporting success.
            if (b.Bytes is null || h.Bytes is null)
            {
                var missing = MissingSide(b.Bytes, h.Bytes);

                report.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"| `{Shorten(head.Key)}` | {Format(b.Bytes)} | {Format(h.Bytes)} | — | {Format(b.StatementsPerJob)} | {Format(h.StatementsPerJob)} | — | **NO MEASUREMENT** |");

                failures.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Shorten(head.Key)}: {missing} produced no measurement, so nothing was compared"));

                continue;
            }

            var allocChange = b.Bytes == 0 ? 0 : (h.Bytes.Value - b.Bytes.Value) / (double)b.Bytes.Value * 100;
            var stmtChange = b.StatementsPerJob is null or 0 || h.StatementsPerJob is null
                ? 0
                : (h.StatementsPerJob.Value - b.StatementsPerJob.Value) / b.StatementsPerJob.Value * 100;

            var verdict = "ok";

            // Allocations are REPORTED, not gated. They were gated at 2% on the strength of one run
            // showing byte-identical numbers, and the assumption did not survive: later runs moved
            // 2-3% systematically with no code change that could explain it, and the gate fired on
            // arms whose statement counts were clean. Until the noise floor is measured — a null
            // comparison, the same commit on both sides — a threshold here is a guess, and a guess
            // that fails builds is worse than no guess at all.
            if (allocChange > tolerancePct)
            {
                verdict = "alloc +" + allocChange.ToString("N1", CultureInfo.InvariantCulture) + "%";
            }

            // Statements get their own, looser bound: unlike allocations they are read from a shared
            // server, so background tasks ticking on timers add a little jitter that bytes do not have.
            if (stmtChange > StatementTolerancePct)
            {
                verdict = "**REGRESSION**";
                failures.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Shorten(head.Key)}: statements/job {b.StatementsPerJob:N2} -> {h.StatementsPerJob:N2} ({stmtChange:+0.0;-0.0;0.0}%)"));
            }

            report.AppendLine(
                CultureInfo.InvariantCulture,
                $"| `{Shorten(head.Key)}` | {Format(b.Bytes)} | {Format(h.Bytes)} | {allocChange:+0.0;-0.0;0.0}% | {Format(b.StatementsPerJob)} | {Format(h.StatementsPerJob)} | {FormatChange(b.StatementsPerJob, h.StatementsPerJob, stmtChange)} | {verdict} |");
        }

        report.AppendLine();
        report.AppendLine(
            CultureInfo.InvariantCulture,
            $"Statements per job is the only gated metric, at {StatementTolerancePct}%. Allocations and time are reported: allocations have moved 2-3% between identical builds, and time has measured an Error of 108 s against a 25 s mean. A move worth acting on in either is still visible in the table.");

        // Only when a run actually swept providers, so a single-provider summary stays uncluttered.
        // Without this the table invites its own misreading: a PostgreSQL row reading 14 beside a SQL
        // Server row reading 25 looks like a verdict on the providers, when the two numbers come from
        // different instruments - pg_stat_statements counts every execution, dm_exec_query_stats only
        // those whose plans are still cached. Each row is evidence about ITSELF across two commits.
        if (headRun.Keys.Any(x => x.Contains("Provider: SqlServer", StringComparison.Ordinal))
            && headRun.Keys.Any(x => x.Contains("Provider: PostgreSql", StringComparison.Ordinal)))
        {
            report.AppendLine();
            report.AppendLine(
                "**Rows are comparable to themselves, not to each other.** PostgreSQL and SQL Server "
                + "statement counts come from different instruments and different accounting; a "
                + "difference between two providers' rows is not a finding. What each row answers is "
                + "whether that arm moved between base and head.");
        }

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

    private static string MissingSide(long? baseBytes, long? headBytes)
    {
        if (baseBytes is null && headBytes is null)
        {
            return "neither side";
        }

        return baseBytes is null ? "the base" : "the head";
    }

    private static string Format(long? value) => value is null ? "—" : value.Value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Format(double? value) => value is null ? "—" : value.Value.ToString("N2", CultureInfo.InvariantCulture);

    private static string FormatChange(double? baseValue, double? headValue, double change) =>
        baseValue is null || headValue is null
            ? "—"
            : string.Create(CultureInfo.InvariantCulture, $"{change:+0.0;-0.0;0.0}%");

    /// <summary>Trims the namespace off a BDN FullName so the table stays readable.</summary>
    private static string Shorten(string fullName)
    {
        var parameters = fullName.IndexOf('(', StringComparison.Ordinal);
        var head = parameters < 0 ? fullName : fullName[..parameters];
        var tail = parameters < 0 ? string.Empty : fullName[parameters..];
        var lastDot = head.LastIndexOf('.');
        var secondLast = lastDot <= 0 ? -1 : head.LastIndexOf('.', lastDot - 1);

        return (secondLast < 0 ? head : head[(secondLast + 1)..]) + tail;
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
