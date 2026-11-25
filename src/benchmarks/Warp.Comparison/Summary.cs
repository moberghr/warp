using System.Globalization;
using System.Text;

namespace Warp.Comparison;

/// <summary>One table per job kind: a row per measurement, a column per engine, each cell the median of the runs and their range.</summary>
internal static class Summary
{
    private static readonly (string Label, Func<RunResult, double> Value, string Format)[] Rows =
    [
        ("Time to drain (s)", x => x.DrainSeconds, "0.0"),
        ("Jobs/s, first job to last", x => x.JobsPerSecondFirstToLast, "N0"),
        ("Jobs/s, p10 to p90", x => x.JobsPerSecondP10ToP90, "N0"),
        ("Statements per job", x => x.StatementsPerJob, "0.00"),
        ("Commits per job", x => x.CommitsPerJob, "0.00"),
        ("WAL per job (bytes)", x => x.WalBytesPerJob, "N0"),
        ("Database time per job (ms)", x => x.DbMsPerJob, "0.000"),
        ("Worker process CPU per job (ms)", x => x.ProcessCpuMsPerJob, "0.000"),
        ("Allocated per job (KB)", x => x.AllocatedKbPerJob, "0.0"),
        ("Peak connections", x => x.PeakConnections, "0"),
        ("Idle connections (average)", x => x.AverageIdleConnections, "0.0"),
    ];

    private static readonly string[] EngineOrder = ["warp", "warp-dispatcher", "hangfire"];

    public static string Render(List<RunResult> results)
    {
        var text = new StringBuilder();
        var first = results.FirstOrDefault();
        if (first is null)
        {
            return "No results.";
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"## Warp vs Hangfire · {first.Servers} server × {first.Workers} workers · PostgreSQL {first.PostgresVersion}");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Warp `{first.Commit}`, Hangfire {first.HangfireVersion} with Hangfire.PostgreSql. Each cell is the median of the runs, with the lowest and highest in brackets.");
        text.AppendLine();

        foreach (var job in results.GroupBy(x => x.Job).OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var engines = job
                .GroupBy(x => x.Engine)
                .OrderBy(x => Array.IndexOf(EngineOrder, x.Key) is var i && i < 0 ? int.MaxValue : i)
                .ToList();
            var jobs = job.First().Jobs;
            text.AppendLine(CultureInfo.InvariantCulture, $"### {(string.Equals(job.Key, "work", StringComparison.Ordinal) ? "Jobs that read and update a row" : "Empty jobs")} · {jobs:N0} jobs");
            text.AppendLine();
            text.AppendLine("| | " + string.Join(" | ", engines.Select(x => $"{Label(x.Key)} ({x.Count()} runs)")) + " |");
            text.AppendLine("|---|" + string.Concat(engines.Select(_ => "---:|")));
            foreach (var (label, value, format) in Rows)
            {
                text.AppendLine($"| {label} | " + string.Join(" | ", engines.Select(x => Cell([.. x.Select(value)], format))) + " |");
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    private static string Label(string engine) => engine switch
    {
        "warp" => "Warp (default)",
        "warp-dispatcher" => "Warp (dispatcher)",
        "hangfire" => "Hangfire",
        _ => engine,
    };

    private static string Cell(List<double> values, string format)
    {
        List<double> sorted = [.. values.Order()];
        var median = sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
        var text = median.ToString(format, CultureInfo.InvariantCulture);

        return sorted.Count > 1
            ? string.Create(CultureInfo.InvariantCulture, $"{text} ({sorted[0].ToString(format, CultureInfo.InvariantCulture)}–{sorted[^1].ToString(format, CultureInfo.InvariantCulture)})")
            : text;
    }
}
