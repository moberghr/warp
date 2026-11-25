using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Warp.ServerBenchmarks.Lab;

/// <summary>
/// One table for a run that measured every case several times (the workflow's <c>repeats</c> input): the median
/// of each count on both sides, and how far the repeats were apart.
/// <para>
/// One run is enough to see a regression in the counts, which repeat to about 2%. Numbers that are published,
/// and anything timed, need more than one sample: jobs per second on a shared runner moves too much for a
/// single reading, and a case whose pages per job swing between repeats (a query flipping plans) only shows
/// when there are repeats to compare. Each repeat's artifact holds the head's cases (<c>export.json</c>) and
/// the base's (<c>export-base.json</c>); a case is paired with itself across the repeats by name and database.
/// </para>
/// </summary>
public static partial class PerfCompare
{
    // A count that differs by more than this between repeats of one side is marked: the counts repeat to about
    // 2%, so a wider spread means the case did not do the same work each time.
    private const double UnsteadyCountPct = 5.0;

    // Transactions are counted database-wide, so the server tasks' own (which run on a timer, not per job) are
    // in them: 54 of 102 cases were over 5% apart across three runs of identical code, against 6 for statements.
    private const double UnsteadyTransactionsPct = 25.0;

    // Pages per job are noisier than the counts (vacuum, dead index entries), so only a large spread is marked:
    // the plan flips this is here to show were 3x to 7x.
    private const double UnsteadyPagesPct = 50.0;

    // ... and by more than this in absolute terms: 0.02 against 0.08 transactions per job is a spread of
    // several hundred percent that says nothing.
    private const double UnsteadyCountFloor = 0.1;
    private const double UnsteadyWalFloor = 100;

    public static int Medians(string resultsPath, string outPath)
    {
        var repeats = Directory.GetFiles(resultsPath, "export.json", SearchOption.AllDirectories)
            .Select(x => (Head: x, Base: Path.Combine(Path.GetDirectoryName(x)!, "export-base.json")))
            .Where(x => File.Exists(x.Base))
            .SelectMany(x => PairCases(ReadCases(x.Base), ReadCases(x.Head)))
            .GroupBy(x => HistoryKey(x.Head), StringComparer.Ordinal)
            .Select(x => x.ToList())
            .OrderBy(x => x[0].Head.Scenario, StringComparer.Ordinal)
            .ThenBy(x => x[0].Head.Case, StringComparer.Ordinal)
            .ThenBy(x => x[0].Head.Database, StringComparer.Ordinal)
            .ToList();

        if (repeats.Count == 0)
        {
            Console.WriteLine($"No repeat with both sides under {resultsPath}: no medians written.");

            return 0;
        }

        var runs = repeats.Max(x => x.Count);
        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"## Medians of {runs} runs");
        report.AppendLine();
        report.AppendLine("Each cell is `base → head (change)`, the median over the runs. ⚠️ marks a head value whose runs were more than "
            + $"{UnsteadyCountPct:0}% apart (transactions: {UnsteadyTransactionsPct:0}%, pages: {UnsteadyPagesPct:0}%): that case did not do the same work every time. Speed is head ÷ base jobs per second "
            + "within each run, median and range; it is the only timing here, and a ratio because the runners differ.");
        report.AppendLine();
        report.AppendLine("| Scenario | Case | Database | Runs | Statements | WAL | Commands | Transactions | Pages | Peak conns | Idle conns | Speed |");
        report.AppendLine("| :--- | :--- | :--- | ---: | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |");

        foreach (var cases in repeats)
        {
            var first = cases[0].Head;
            var unit = first.PerSecond ? "/s" : string.Empty;
            report.AppendLine(string.Join(
                " | ",
                $"| {first.Scenario}",
                first.Case,
                first.Database ?? "—",
                cases.Count.ToString(CultureInfo.InvariantCulture),
                MedianCell(cases, x => x.StatementsPerJob, "0.00", unit, UnsteadyCountPct, UnsteadyCountFloor),
                MedianCell(cases, x => x.WalBytesPerJob, "N0", first.PerSecond ? " B/s" : " B", UnsteadyCountPct, UnsteadyWalFloor),
                MedianCell(cases, x => x.RoundTripsPerJob, "0.00", unit, UnsteadyCountPct, UnsteadyCountFloor),
                MedianCell(cases, x => x.TransactionsPerJob, "0.00", unit, UnsteadyTransactionsPct, UnsteadyCountFloor),
                MedianCell(cases, x => x.BuffersPerJob, "0.0", unit, UnsteadyPagesPct, 0),
                MedianCell(cases, x => x.PeakConnections, "0", string.Empty, UnsteadyPagesPct, 2),
                MedianCell(cases, x => x.IdleConnections, "0.0", string.Empty, UnsteadyPagesPct, 2),
                SpeedCell(cases) + " |"));
        }

        File.WriteAllText(outPath, report.ToString());
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Wrote medians of {repeats.Count} cases over {runs} runs to {outPath}"));

        return 0;
    }

    private static List<ReleaseCase> ReadCases(string path) =>
        JsonSerializer.Deserialize<List<ReleaseCase>>(File.ReadAllText(path), HistoryOptions) ?? [];

    private static IEnumerable<(ReleaseCase Base, ReleaseCase Head)> PairCases(List<ReleaseCase> baseCases, List<ReleaseCase> headCases)
    {
        var byName = baseCases
            .GroupBy(x => x.FullName, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);

        return headCases
            .Where(x => byName.ContainsKey(x.FullName))
            .Select(x => (byName[x.FullName], x));
    }

    private static string MedianCell(List<(ReleaseCase Base, ReleaseCase Head)> cases, Func<ReleaseCase, double?> metric, string format, string unit, double unsteadyPct, double unsteadyFloor)
    {
        var baseValues = cases.Select(x => metric(x.Base)).OfType<double>().ToList();
        var headValues = cases.Select(x => metric(x.Head)).OfType<double>().ToList();
        if (baseValues.Count == 0 || headValues.Count == 0)
        {
            return "—";
        }

        var baseMedian = MedianOf(baseValues);
        var headMedian = MedianOf(headValues);
        var change = baseMedian > 0
            ? string.Create(CultureInfo.InvariantCulture, $" ({(headMedian - baseMedian) / baseMedian * 100:+0;−0;0}%)")
            : string.Empty;
        var spread = headMedian > 0 ? (headValues.Max() - headValues.Min()) / headMedian * 100 : 0;
        var unsteady = spread > unsteadyPct && headValues.Max() - headValues.Min() > unsteadyFloor
            ? string.Create(CultureInfo.InvariantCulture, $" ⚠️ {headValues.Min().ToString(format, CultureInfo.InvariantCulture)}–{headValues.Max().ToString(format, CultureInfo.InvariantCulture)}")
            : string.Empty;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{baseMedian.ToString(format, CultureInfo.InvariantCulture)} → {headMedian.ToString(format, CultureInfo.InvariantCulture)}{unit}{change}{unsteady}");
    }

    private static string SpeedCell(List<(ReleaseCase Base, ReleaseCase Head)> cases)
    {
        var ratios = cases
            .Where(x => x.Base.JobsPerSecond > 0 && x.Head.JobsPerSecond > 0)
            .Select(x => x.Head.JobsPerSecond!.Value / x.Base.JobsPerSecond!.Value)
            .ToList();
        if (ratios.Count == 0)
        {
            return "—";
        }

        return ratios.Count == 1
            ? string.Create(CultureInfo.InvariantCulture, $"{ratios[0]:0.00}×")
            : string.Create(CultureInfo.InvariantCulture, $"{MedianOf(ratios):0.00}× ({ratios.Min():0.00}–{ratios.Max():0.00})");
    }

    private static double MedianOf(List<double> values)
    {
        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;

        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
