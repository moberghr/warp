using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Warp.ServerBenchmarks.Infrastructure;

namespace Warp.ServerBenchmarks.Lab;

/// <summary>
/// Benchmark results kept per release, and compared across releases.
/// <para>
/// A pull request is compared with its own base, which catches a regression in one change but not creep:
/// five changes adding 4% each all pass a 5% gate. Each release therefore gets its results attached as
/// <c>benchmarks.json</c>, where they never expire, and every run is also compared with the latest one of
/// those. Only the counts are compared across releases — statements and WAL per job reproduce to about 2%
/// whichever runner measured them, while jobs/s has moved 0.87x to 1.43x between identical runs and the
/// runners' CPUs differ from one job to the next.
/// </para>
/// </summary>
public static partial class PerfCompare
{
    // Since the last release, in total. A warning, not a failure: it measures everything merged since the
    // release, not the change under review, so failing on it blocked every pull request until the next
    // release for drift that was already on main — the first run against 7.0.0 failed a change that touches
    // nothing in Warp, over a SQL Server single-worker rise (3.59 -> 4.25 statements/job) merged weeks
    // earlier. The per-change gate still fails the change that causes a regression; this makes the total
    // since the release visible on every run until someone looks at it.
    private const double ReleaseCreepPct = 10.0;

    private static readonly JsonSerializerOptions HistoryOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Writes the measured side of one scenario's run as a list of cases, for <see cref="RecordRelease"/>
    /// to gather.
    /// </summary>
    public static int ExportBdn(string resultsPath, string outPath)
    {
        var results = ReadBdn(resultsPath);
        var cases = results
            .Select(x => ToReleaseCase(new BdnCase(x.Key), x.Value))
            .ToList();

        File.WriteAllText(outPath, JsonSerializer.Serialize(cases, HistoryOptions));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Exported {cases.Count} cases to {outPath}"));

        return 0;
    }

    /// <summary>Gathers every scenario's export under a directory into one release's results.</summary>
    public static int RecordRelease(string exportsPath, string release, string commit, string outPath)
    {
        var cases = Directory.GetFiles(exportsPath, "export.json", SearchOption.AllDirectories)
            .SelectMany(x => JsonSerializer.Deserialize<List<ReleaseCase>>(File.ReadAllText(x), HistoryOptions) ?? [])
            .OrderBy(x => x.FullName, StringComparer.Ordinal)
            .ToList();

        if (cases.Count == 0)
        {
            Console.Error.WriteLine($"No export.json under {exportsPath}: nothing to record for {release}.");

            return 1;
        }

        var recorded = new ReleaseBenchmarks(release, commit, MeasurementVersion.Current, DateTime.UtcNow, cases);
        File.WriteAllText(outPath, JsonSerializer.Serialize(recorded, HistoryOptions));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Recorded {cases.Count} cases for {release} (measurement version {MeasurementVersion.Current})."));

        return 0;
    }

    /// <summary>
    /// Release-over-release tables and charts, one section per scenario, from every release's results plus
    /// this run's exports.
    /// </summary>
    public static int History(string releasesPath, string? exportsPath, string? label, string outPath)
    {
        var releases = Directory.Exists(releasesPath)
            ? Directory.GetFiles(releasesPath, "*.json", SearchOption.AllDirectories)
                .Select(ReadRelease)
                .Where(x => x is not null)
                .Select(x => x!)
                .ToList()
            : [];

        var comparable = releases
            .Where(x => x.MeasurementVersion == MeasurementVersion.Current)
            .OrderBy(x => SortKey(x.Release))
            .ToList();
        var skipped = releases.Count - comparable.Count;

        var points = comparable.ConvertAll(x => (Label: x.Release, Cases: x.Cases.ToDictionary(y => y.FullName, StringComparer.Ordinal)));

        if (exportsPath is not null && Directory.Exists(exportsPath))
        {
            var current = Directory.GetFiles(exportsPath, "export.json", SearchOption.AllDirectories)
                .SelectMany(x => JsonSerializer.Deserialize<List<ReleaseCase>>(File.ReadAllText(x), HistoryOptions) ?? [])
                .ToDictionary(x => x.FullName, StringComparer.Ordinal);
            if (current.Count > 0)
            {
                points.Add((label ?? "this run", current));
            }
        }

        var report = new StringBuilder();
        report.AppendLine("<details><summary>History across releases</summary>");
        report.AppendLine();

        if (comparable.Count == 0)
        {
            report.AppendLine(
                "No release has results on the current measurement version yet. Publishing a release records one; "
                + "an existing release can be recorded with the Performance workflow's `record_release` input.");
        }
        else
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"Statements per job, release by release, for every release measured on measurement version {MeasurementVersion.Current}. Counts only: jobs/s is not comparable across runners. — means the release produced no measurement for that case, for example because it did not finish.");
            report.AppendLine();
            AppendHistoryTables(report, points);
        }

        if (skipped > 0)
        {
            report.AppendLine();
            report.AppendLine(CultureInfo.InvariantCulture, $"<sub>{skipped} release(s) were measured on an older measurement version and are left out, not compared.</sub>");
        }

        report.AppendLine();
        report.AppendLine("</details>");

        File.WriteAllText(outPath, report.ToString());
        Console.WriteLine(report.ToString());

        return 0;
    }

    /// <summary>The since-last-release line under a scenario's table, and its gate.</summary>
    private static void AppendSinceRelease(
        StringBuilder section,
        List<BdnCase> cases,
        Dictionary<string, BdnResult> headRun,
        ReleaseBenchmarks? release)
    {
        if (release is null)
        {
            return;
        }

        section.AppendLine();

        if (release.MeasurementVersion != MeasurementVersion.Current)
        {
            section.AppendLine(CultureInfo.InvariantCulture, $"<sub>Since {release.Release}: not comparable, measured on version {release.MeasurementVersion} and this run on {MeasurementVersion.Current}.</sub>");

            return;
        }

        var baseline = release.Cases.ToDictionary(x => x.FullName, StringComparer.Ordinal);
        var parts = new List<string>();
        foreach (var benchmark in cases)
        {
            if (!headRun.TryGetValue(benchmark.FullName, out var head) || head.PerSecond
                || head.StatementsPerJob is not { } now
                || !baseline.TryGetValue(benchmark.FullName, out var then) || then.StatementsPerJob is not { } before || before <= 0)
            {
                continue;
            }

            var change = (now - before) / before * 100;
            var drifted = change > ReleaseCreepPct;
            var mark = drifted ? "⚠️" : "✅";
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{mark} {CaseLabelOf(benchmark)} stmt {change:+0.0;−0.0;0.0}%"));

            if (drifted)
            {
                // Counted by the report job for its headline; see ReleaseCreepPct for why it never fails.
                section.AppendLine("<!-- drift -->");
            }
        }

        section.AppendLine(parts.Count == 0
            ? string.Create(CultureInfo.InvariantCulture, $"<sub>Since {release.Release}: no case in common.</sub>")
            : string.Create(CultureInfo.InvariantCulture, $"<sub>Since {release.Release} (⚠️ above +{ReleaseCreepPct:0}%, reported, never failed): {string.Join(" · ", parts)}</sub>"));
    }

    private static void AppendHistoryTables(StringBuilder report, List<(string Label, Dictionary<string, ReleaseCase> Cases)> points)
    {
        var scenarios = points
            .SelectMany(x => x.Cases.Values)
            .GroupBy(x => x.Scenario, StringComparer.Ordinal)
            .OrderBy(x => x.Key, StringComparer.Ordinal);

        foreach (var scenario in scenarios)
        {
            // One row per benchmark case, keyed on its full name. The label is display only and can change
            // between releases (a clearer case name), so the most recent one wins rather than splitting the row.
            var names = scenario
                .GroupBy(x => x.FullName, StringComparer.Ordinal)
                .Select(x => (FullName: x.Key, Case: x.Last().Case))
                .OrderBy(x => x.FullName, StringComparer.Ordinal)
                .ToList();

            // An idle server runs no jobs; its counters are per second, and are labelled so.
            var unit = scenario.All(x => x.PerSecond) ? "statements / s" : "statements / job";
            report.AppendLine(CultureInfo.InvariantCulture, $"**{scenario.Key}** ({unit})");
            report.AppendLine();
            report.AppendLine("| case | " + string.Join(" | ", points.Select(x => x.Label)) + " |");
            report.AppendLine("| :--- | " + string.Join(" | ", points.Select(_ => "---:")) + " |");

            foreach (var (fullName, caseName) in names)
            {
                var cells = points.Select(x => x.Cases.TryGetValue(fullName, out var c) && c.StatementsPerJob is { } v
                    ? v.ToString("N2", CultureInfo.InvariantCulture)
                    : "—");
                report.AppendLine(CultureInfo.InvariantCulture, $"| {caseName} | {string.Join(" | ", cells)} |");
            }

            // A chart only once there is a line to draw: two points or more.
            if (points.Count >= 2)
            {
                foreach (var (fullName, caseName) in names)
                {
                    var values = points.ConvertAll(x => x.Cases.TryGetValue(fullName, out var c) ? c.StatementsPerJob : null);
                    if (values.Count(x => x is not null) < 2)
                    {
                        continue;
                    }

                    var top = Math.Ceiling((values.Max() ?? 1) * 1.2);
                    report.AppendLine();
                    report.AppendLine("```mermaid");
                    report.AppendLine("xychart-beta");
                    report.AppendLine(CultureInfo.InvariantCulture, $"    title \"{scenario.Key}: {caseName}\"");
                    report.AppendLine("    x-axis [" + string.Join(", ", points.Select(x => "\"" + x.Label + "\"")) + "]");
                    report.AppendLine(CultureInfo.InvariantCulture, $"    y-axis \"{unit}\" 0 --> {top:0}");
                    report.AppendLine("    line [" + string.Join(", ", values.Select(x => (x ?? 0).ToString("0.00", CultureInfo.InvariantCulture))) + "]");
                    report.AppendLine("```");
                }
            }

            report.AppendLine();
        }
    }

    private static ReleaseCase ToReleaseCase(BdnCase benchmark, BdnResult result)
    {
        var jobs = JobCountOf(benchmark);
        double? rate = jobs is null || result.Mean <= 0 ? null : jobs.Value / (result.Mean / 1e9);

        return new ReleaseCase(
            benchmark.FullName,
            ScenarioTitleOf(benchmark),
            CaseLabelOf(benchmark),
            result.PerSecond,
            result.StatementsPerJob,
            result.WalBytesPerJob,
            result.BuffersPerJob,
            rate,
            result.Bytes);
    }

    private static string ScenarioTitleOf(BdnCase benchmark) =>
        typeof(PerfCompare).Assembly.GetType(benchmark.TypeName)?.GetCustomAttribute<CiScenarioAttribute>()?.Title
        ?? benchmark.TypeName[(benchmark.TypeName.LastIndexOf('.') + 1)..];

    /// <summary>
    /// The labels of the parameters the benchmark labels. A parameter with no <c>[CaseLabel]</c> is one the
    /// scenario holds fixed — the job count, the payload size — and says nothing about which case this is.
    /// </summary>
    private static string CaseLabelOf(BdnCase benchmark)
    {
        var labels = typeof(PerfCompare).Assembly.GetType(benchmark.TypeName)?.GetCustomAttributes<CaseLabelAttribute>().ToList() ?? [];
        var parts = benchmark.Parameters
            .Where(x => labels.Any(y => string.Equals(y.Parameter, x.Name, StringComparison.Ordinal)))
            .Select(x => ParameterCell(benchmark, x.Name, labels))
            .ToList();

        if (parts.Count > 0)
        {
            return string.Join(" · ", parts);
        }

        return JobCountOf(benchmark) is { } jobs
            ? jobs.ToString("N0", CultureInfo.InvariantCulture) + " jobs"
            : "single case";
    }

    private static ReleaseBenchmarks? ReadRelease(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<ReleaseBenchmarks>(File.ReadAllText(path), HistoryOptions);
        }
        catch (JsonException)
        {
            Console.Error.WriteLine($"Skipping {path}: not a release benchmarks file.");

            return null;
        }
    }

    /// <summary>Loads a release's results for the since-release gate; null when there is none to compare with.</summary>
    private static ReleaseBenchmarks? LoadReleaseBaseline(string? path) =>
        path is null || !File.Exists(path) ? null : ReadRelease(path);

    private static (Version Version, string Tag) SortKey(string tag) =>
        Version.TryParse(tag.TrimStart('v'), out var version) ? (version, tag) : (new Version(0, 0), tag);

    private sealed record ReleaseBenchmarks(string Release, string Commit, int MeasurementVersion, DateTime RecordedAt, List<ReleaseCase> Cases);

    private sealed record ReleaseCase(
        string FullName,
        string Scenario,
        string Case,
        bool PerSecond,
        double? StatementsPerJob,
        double? WalBytesPerJob,
        double? BuffersPerJob,
        double? JobsPerSecond,
        long? AllocatedBytes);
}
