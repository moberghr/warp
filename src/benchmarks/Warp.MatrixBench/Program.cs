using System.Globalization;
using Warp.MatrixBench;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

if (args.Length > 0 && string.Equals(args[0], "run", StringComparison.OrdinalIgnoreCase))
{
    var options = CellOptions.Parse(args);
    Environment.ExitCode = await MatrixRunner.RunAsync(options);
}
else if (args.Length > 0 && string.Equals(args[0], "summarize", StringComparison.OrdinalIgnoreCase))
{
    Environment.ExitCode = MatrixSummary.Run(
        CellOptions.Value(args, "--in=") ?? "results",
        CellOptions.Value(args, "--markdown=") ?? "perf-matrix-summary.md",
        CellOptions.Value(args, "--json=") ?? "perf-matrix.json");
}
else
{
    Console.WriteLine("usage:");
    Console.WriteLine("  run --provider=postgresql|sqlserver --mode=dispatcher|workers [--push] --job=empty|db|mixed");
    Console.WriteLine("      --jobs=N --rounds=N --connection=<admin connection string> [--db-container=name]");
    Console.WriteLine("      --ref=main|pr --commit=<sha> --out=<cell.json>");
    Console.WriteLine("  summarize --in=<dir of cell json files> --markdown=<file.md> --json=<file.json>");
    Environment.ExitCode = 2;
}
