using System.Globalization;

namespace Warp.MatrixBench;

public enum BenchProvider
{
    PostgreSql = 1,
    SqlServer = 2,
}

/// <summary>One cell of the matrix: which configuration to run, against which server, and where to write it.</summary>
public sealed record CellOptions
{
    public BenchProvider Provider { get; init; } = BenchProvider.PostgreSql;

    public bool Dispatcher { get; init; }

    public bool Push { get; init; }

    public string JobType { get; init; } = Workloads.Empty;

    public int Jobs { get; init; } = 10_000;

    public int Rounds { get; init; } = 3;

    public int Workers { get; init; } = 10;

    public int WarmupJobs { get; init; } = 200;

    public int SpecialsPerFeature { get; init; } = 25;

    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromMinutes(15);

    public string Connection { get; init; } = string.Empty;

    public string? DbContainer { get; init; }

    public string Ref { get; init; } = "local";

    public string Commit { get; init; } = "unknown";

    public string Out { get; init; } = "cell.json";

    public string Mode => Dispatcher ? "dispatcher" : "workers";

    public string Name => $"{Ref}-{ProviderName}-{Mode}-{(Push ? "push" : "nopush")}-{JobType}";

    public string ProviderName => Provider == BenchProvider.SqlServer ? "sqlserver" : "postgresql";

    public static CellOptions Parse(string[] args)
    {
        var provider = Value(args, "--provider=") ?? "postgresql";
        var job = (Value(args, "--job=") ?? Workloads.Empty).ToLowerInvariant();
        if (job is not (Workloads.Empty or Workloads.Db or Workloads.Mixed))
        {
            throw new ArgumentException($"Unknown --job '{job}'. Expected empty, db or mixed.", nameof(args));
        }

        var mode = (Value(args, "--mode=") ?? "workers").ToLowerInvariant();
        if (mode is not ("dispatcher" or "workers"))
        {
            throw new ArgumentException($"Unknown --mode '{mode}'. Expected dispatcher or workers.", nameof(args));
        }

        return new CellOptions
        {
            Provider = provider.StartsWith("sql", StringComparison.OrdinalIgnoreCase) ? BenchProvider.SqlServer : BenchProvider.PostgreSql,
            Dispatcher = string.Equals(mode, "dispatcher", StringComparison.Ordinal),
            Push = args.Any(x => string.Equals(x, "--push", StringComparison.OrdinalIgnoreCase)),
            JobType = job,
            Jobs = Int(args, "--jobs=") ?? 10_000,
            Rounds = Int(args, "--rounds=") ?? 3,
            Workers = Int(args, "--workers=") ?? 10,
            WarmupJobs = Int(args, "--warmup=") ?? 200,
            SpecialsPerFeature = Int(args, "--specials=") ?? 25,
            DrainTimeout = TimeSpan.FromMinutes(Int(args, "--drain-timeout-minutes=") ?? 15),
            Connection = Value(args, "--connection=") ?? throw new ArgumentException("--connection is required.", nameof(args)),
            DbContainer = Value(args, "--db-container="),
            Ref = Value(args, "--ref=") ?? "local",
            Commit = Value(args, "--commit=") ?? "unknown",
            Out = Value(args, "--out=") ?? "cell.json",
        };
    }

    public static string? Value(string[] args, string prefix) =>
        args.Skip(1)
            .Where(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(x => x[prefix.Length..])
            .FirstOrDefault();

    private static int? Int(string[] args, string prefix) =>
        Value(args, prefix) is { } text ? int.Parse(text, CultureInfo.InvariantCulture) : null;
}
