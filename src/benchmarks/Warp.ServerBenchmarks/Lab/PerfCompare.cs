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
                $"| `{policy.Metric}` | {b.Median:N3} | {h.Median:N3} | {change:+0.0;-0.0}% | {b.SpreadPct:N1}% / {h.SpreadPct:N1}% | {verdict} |");
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
            $"{policy.Metric}: {change:+0.0;-0.0}% (tolerance {policy.TolerancePct}%, spread {widest:N1}%)"));

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

    private enum Direction
    {
        Higher = 1,
        Lower = 2,
    }

    private sealed record MetricPolicy(string Metric, bool Gated, double? TolerancePct, Direction BadDirection);
}
