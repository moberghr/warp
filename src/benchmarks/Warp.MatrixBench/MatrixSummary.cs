using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Warp.MatrixBench;

/// <summary>
/// Folds every cell's file into one JSON document and one markdown page: per job type, a row per
/// provider x mode x push, main beside this branch for every metric, medians across rounds.
/// </summary>
public static class MatrixSummary
{
    private const string MainRef = "main";
    private const string BranchRef = "pr";

    public static int Run(string inputDirectory, string markdownPath, string jsonPath)
    {
        var cells = Load(inputDirectory);
        Console.WriteLine($"loaded {cells.Count} cell files from {inputDirectory}");

        var combined = new
        {
            GeneratedAt = DateTime.UtcNow.ToString("O"),
            Cells = cells,
        };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(combined, ResultJson.Options));

        var markdown = Render(cells);
        File.WriteAllText(markdownPath, markdown);
        Console.WriteLine(markdown);

        return cells.Count == 0 ? 1 : 0;
    }

    private static List<CellResult> Load(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var cells = new List<CellResult>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            try
            {
                var cell = JsonSerializer.Deserialize<CellResult>(File.ReadAllText(file), ResultJson.Options);
                if (cell is not null)
                {
                    cells.Add(cell);
                }
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"skipped {file}: {ex.Message}");
            }
        }

        return cells;
    }

    private static string Render(List<CellResult> cells)
    {
        var page = new StringBuilder();
        var failures = cells
            .Where(x => x.Error is not null || x.Rounds.Count == 0 || x.Rounds.Exists(y => !y.Correct))
            .ToList();

        page.AppendLine("## Performance matrix: main vs this branch");
        page.AppendLine();
        page.AppendLine(failures.Count == 0
            ? $"All {cells.Count} cells ran and passed every correctness check."
            : $"**{failures.Count} of {cells.Count} cells failed a correctness check or did not complete** — see the correctness tables.");
        page.AppendLine();
        page.AppendLine($"- **main**: `{Commits(cells, MainRef)}`");
        page.AppendLine($"- **this branch**: `{Commits(cells, BranchRef)}`");
        page.AppendLine();
        page.AppendLine("Each value is `main → branch (change)`, the median across rounds. Every cell ran on its own runner with fresh database containers: one server process, 10 workers, 1 s polling with no backoff growth, a 200-job warm-up, 5 s idle, then one bulk insert; everything is measured from the moment that insert committed. Runner-to-runner variance applies to the timings; counts (statements, buffers, allocations) are the steadier comparison.");
        page.AppendLine();

        foreach (var jobType in new[] { Workloads.Empty, Workloads.Db, Workloads.Mixed })
        {
            var rows = cells
                .Where(x => string.Equals(x.JobType, jobType, StringComparison.Ordinal))
                .GroupBy(x => (x.Provider, x.Mode, x.Push))
                .OrderBy(x => x.Key.Provider, StringComparer.Ordinal)
                .ThenBy(x => x.Key.Mode, StringComparer.Ordinal)
                .ThenBy(x => x.Key.Push)
                .Select(x => new Row(
                    x.Key.Provider,
                    x.Key.Mode,
                    x.Key.Push,
                    x.FirstOrDefault(y => string.Equals(y.Ref, MainRef, StringComparison.Ordinal)),
                    x.FirstOrDefault(y => string.Equals(y.Ref, BranchRef, StringComparison.Ordinal))))
                .ToList();

            if (rows.Count == 0)
            {
                continue;
            }

            var jobs = rows
                .SelectMany(x => new[] { x.Main, x.Branch })
                .Where(x => x is not null)
                .Select(x => x!.Jobs)
                .FirstOrDefault();
            var rounds = rows
                .SelectMany(x => new[] { x.Main, x.Branch })
                .Where(x => x is not null)
                .Select(x => x!.Rounds.Count)
                .DefaultIfEmpty()
                .Max();

            page.AppendLine($"### `{jobType}` jobs ({jobs:N0} per round, up to {rounds} round(s))");
            page.AppendLine();
            RenderTable(page, rows, "Time and throughput", ThroughputColumns);
            RenderTable(page, rows, "Cost per job", CostColumns);
            RenderTable(page, rows, "Database per job", DatabaseColumns);
            RenderCorrectness(page, rows);
        }

        page.AppendLine("<details><summary>What each column is</summary>");
        page.AppendLine();
        page.AppendLine("- **Drained s**: marker to the first poll that found no job row in Enqueued, Awaiting, Processing or Scheduled.");
        page.AppendLine("- **Jobs/s**: handler executions divided by first-to-last handler finish, seen in process. **p10/p50/p90**: of the per-second finish counts over whole seconds.");
        page.AppendLine("- **CPU ms/job**: the server process's CPU time (all threads, the harness's drain polls included) over the window. **Alloc B/job**: `GC.GetTotalAllocatedBytes` delta. **Gen0/1/2**: collections in the window.");
        page.AppendLine("- **Stmts/job** and **Buffers/job**: PostgreSQL `pg_stat_statements` for the round's database (shared blocks hit + read); SQL Server `dm_exec_query_stats` compared plan by plan (logical reads). The harness's own drain polls are subtracted from the statements. **WAL B/job**: `pg_stat_wal` (cluster-wide). **Batches/job** and **Tx/job**: SQL Server's Batch Requests/sec and the database's Transactions/sec counters, as deltas.");
        page.AppendLine("- **DB CPU ms/job**: the database container's cgroup CPU usage over the window.");
        page.AppendLine("- **Per-job denominator**: measured job rows (Kind = Job): for mixed, routed message children and batch children included, warm-up and the recurring job excluded.");
        page.AppendLine();
        page.AppendLine("</details>");

        return page.ToString();
    }

    private static readonly Column[] ThroughputColumns =
    [
        new("Drained s", x => x.DrainedSeconds, "N2", true),
        new("Jobs/s", x => x.JobsPerSecond, "N0", false),
        new("p10 jobs/s", x => x.ThroughputP10, "N0", false),
        new("p50 jobs/s", x => x.ThroughputP50, "N0", false),
        new("p90 jobs/s", x => x.ThroughputP90, "N0", false),
    ];

    private static readonly Column[] CostColumns =
    [
        new("CPU ms/job", x => x.CpuMsPerJob, "N3", true),
        new("Alloc B/job", x => x.AllocatedBytesPerJob, "N0", true),
        new("Gen0", x => x.Gen0, "N0", true),
        new("Gen1", x => x.Gen1, "N0", true),
        new("Gen2", x => x.Gen2, "N0", true),
    ];

    private static readonly Column[] DatabaseColumns =
    [
        new("Stmts/job", x => x.Db?.StatementsPerJob, "N2", true),
        new("Buffers/job", x => x.Db?.BuffersPerJob, "N1", true),
        new("WAL B/job", x => x.Db?.WalBytesPerJob, "N0", true),
        new("Batches/job", x => x.Db?.BatchRequestsPerJob, "N2", true),
        new("Tx/job", x => x.Db?.TransactionsPerJob, "N2", true),
        new("DB CPU ms/job", x => x.Db?.DbCpuMsPerJob, "N3", true),
    ];

    private static void RenderTable(StringBuilder page, List<Row> rows, string title, Column[] columns)
    {
        page.AppendLine($"**{title}**");
        page.AppendLine();
        page.AppendLine("| Provider | Mode | Push | " + string.Join(" | ", columns.Select(x => x.Title)) + " |");
        page.AppendLine("|---|---|---|" + string.Concat(columns.Select(_ => "---|")));
        foreach (var row in rows)
        {
            page.Append($"| {row.Provider} | {row.Mode} | {(row.Push ? "on" : "off")} |");
            foreach (var column in columns)
            {
                page.Append(' ').Append(Compare(Median(row.Main, column.Read), Median(row.Branch, column.Read), column)).Append(" |");
            }

            page.AppendLine();
        }

        page.AppendLine();
    }

    private static void RenderCorrectness(StringBuilder page, List<Row> rows)
    {
        page.AppendLine("**Correctness and logs**");
        page.AppendLine();
        page.AppendLine("| Provider | Mode | Push | main | branch | main warn/err | branch warn/err |");
        page.AppendLine("|---|---|---|---|---|---|---|");

        var details = new List<string>();
        foreach (var row in rows)
        {
            page.AppendLine(
                $"| {row.Provider} | {row.Mode} | {(row.Push ? "on" : "off")} | {Verdict(row.Main)} | {Verdict(row.Branch)} | {Logs(row.Main)} | {Logs(row.Branch)} |");

            foreach (var cell in new[] { row.Main, row.Branch }.Where(x => x is not null).Select(x => x!))
            {
                if (cell.Error is not null)
                {
                    details.Add($"- `{cell.Name}`: harness error: {cell.Error}");
                }

                foreach (var round in cell.Rounds)
                {
                    details.AddRange(round.Correctness
                        .Where(x => !x.Pass)
                        .Select(x => $"- `{cell.Name}` round {round.Round}, **{x.Name}**: {x.Detail}"));
                }

                var samples = cell.Rounds
                    .SelectMany(x => x.Logs.Samples)
                    .Distinct(StringComparer.Ordinal)
                    .Take(3)
                    .ToList();
                if (samples.Count > 0 && cell.Rounds.Exists(x => x.Logs.Warnings + x.Logs.Errors > 0))
                {
                    details.Add($"- `{cell.Name}` logged: " + string.Join(" / ", samples.Select(x => $"`{Escape(x)}`")));
                }
            }
        }

        page.AppendLine();
        if (details.Count > 0)
        {
            page.AppendLine("<details><summary>Failed checks and log samples</summary>");
            page.AppendLine();
            foreach (var line in details)
            {
                page.AppendLine(line);
            }

            page.AppendLine();
            page.AppendLine("</details>");
            page.AppendLine();
        }
    }

    private static string Verdict(CellResult? cell)
    {
        if (cell is null)
        {
            return "⚠️ missing";
        }

        if (cell.Error is not null)
        {
            return $"❌ error after {cell.Rounds.Count} round(s)";
        }

        var failed = cell.Rounds.Count(x => !x.Correct);

        return failed == 0 ? $"✅ {cell.Rounds.Count}/{cell.Rounds.Count}" : $"❌ {failed} of {cell.Rounds.Count} rounds failed";
    }

    private static string Logs(CellResult? cell) =>
        cell is null
            ? "–"
            : $"{cell.Rounds.Sum(x => x.Logs.Warnings)}/{cell.Rounds.Sum(x => x.Logs.Errors)}";

    private static string Compare(double? main, double? branch, Column column)
    {
        if (main is null && branch is null)
        {
            return "–";
        }

        var left = main?.ToString(column.Format, CultureInfo.InvariantCulture) ?? "–";
        var right = branch?.ToString(column.Format, CultureInfo.InvariantCulture) ?? "–";
        if (main is not { } a || branch is not { } b || Math.Abs(a) < double.Epsilon)
        {
            return $"{left} → {right}";
        }

        var change = (b - a) / a * 100;

        return $"{left} → {right} ({change:+0.0;-0.0;0.0}%)";
    }

    private static double? Median(CellResult? cell, Func<RoundResult, double?> read)
    {
        var values = cell?.Rounds
            .Select(read)
            .Where(x => x is not null)
            .Select(x => x!.Value)
            .Order()
            .ToArray() ?? [];

        if (values.Length == 0)
        {
            return null;
        }

        return values.Length % 2 == 1
            ? values[values.Length / 2]
            : (values[(values.Length / 2) - 1] + values[values.Length / 2]) / 2;
    }

    private static string Commits(List<CellResult> cells, string reference) =>
        string.Join(", ", cells
            .Where(x => string.Equals(x.Ref, reference, StringComparison.Ordinal))
            .Select(x => x.Commit)
            .Distinct(StringComparer.Ordinal)
            .DefaultIfEmpty("(no cells)"));

    private static string Escape(string text) => text.Replace('`', '\'').Replace('|', '/').Replace('\n', ' ').Replace('\r', ' ');

    private sealed record Row(string Provider, string Mode, bool Push, CellResult? Main, CellResult? Branch);

    private sealed record Column(string Title, Func<RoundResult, double?> Read, string Format, bool LowerIsBetter);
}
