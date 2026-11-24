using System.Text.Json;
using System.Text.Json.Serialization;

namespace Warp.MatrixBench;

/// <summary>One cell's file: its configuration and every round it ran.</summary>
public sealed record CellResult
{
    public required string Name { get; init; }

    public required string Ref { get; init; }

    public required string Commit { get; init; }

    public required string Provider { get; init; }

    public required string Mode { get; init; }

    public required bool Push { get; init; }

    public required string JobType { get; init; }

    public required int Jobs { get; init; }

    public required int Workers { get; init; }

    public required int WarmupJobs { get; init; }

    public required string StartedAt { get; init; }

    public required string Runtime { get; init; }

    public required int ProcessorCount { get; init; }

    public string? Error { get; init; }

    public required List<RoundResult> Rounds { get; init; }
}

public sealed record RoundResult
{
    public required int Round { get; init; }

    /// <summary>Job rows the round measured (Kind = Job, warm-up and recurring excluded): the per-job denominator.</summary>
    public required int MeasuredJobs { get; init; }

    public required int ExpectedExecutions { get; init; }

    public required double InsertSeconds { get; init; }

    public required bool TimedOut { get; init; }

    /// <summary>From the marker to the first poll that found no job row left in a live state.</summary>
    public double? DrainedSeconds { get; init; }

    /// <summary>First to last handler finish, as seen in process.</summary>
    public required double FirstToLastSeconds { get; init; }

    public required double JobsPerSecond { get; init; }

    public required double ThroughputP10 { get; init; }

    public required double ThroughputP50 { get; init; }

    public required double ThroughputP90 { get; init; }

    /// <summary>Handler finishes per second after the marker, second 0 first.</summary>
    public required int[] Histogram { get; init; }

    public required double CpuMsPerJob { get; init; }

    public required double AllocatedBytesPerJob { get; init; }

    public required int Gen0 { get; init; }

    public required int Gen1 { get; init; }

    public required int Gen2 { get; init; }

    public DbMetrics? Db { get; init; }

    public required LogMetrics Logs { get; init; }

    public required List<CorrectnessCheck> Correctness { get; init; }

    public bool Correct => Correctness.TrueForAll(x => x.Pass);
}

public sealed record DbMetrics
{
    /// <summary>Statements in the window, the harness's own drain polls already taken out.</summary>
    public required long Statements { get; init; }

    public required long HarnessStatements { get; init; }

    public required double StatementsPerJob { get; init; }

    public required double BuffersPerJob { get; init; }

    public double? WalBytesPerJob { get; init; }

    public double? StatementWalBytesPerJob { get; init; }

    public double? BatchRequestsPerJob { get; init; }

    public double? TransactionsPerJob { get; init; }

    public double? DbCpuMs { get; init; }

    public double? DbCpuMsPerJob { get; init; }
}

public sealed record LogMetrics(int Warnings, int Errors, IReadOnlyList<string> Samples);

public sealed record CorrectnessCheck(string Name, bool Pass, string Detail);

public static class ResultJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };
}
