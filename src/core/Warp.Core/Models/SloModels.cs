using Warp.Core.Enums;

namespace Warp.Core.Models;

/// <summary>
/// Read-side shape for one SLO objective plus its rolling evaluation (§8.31) — the dashboard list row and detail.
/// The <c>SloDefinition</c> fields describe the objective; the evaluation fields are the latest computed status
/// (<c>Evaluated</c> is false until the <c>SloEvaluator</c> has run against it at least once).
/// </summary>
public sealed class SloObjectiveModel
{
    public int Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public SloKind Kind { get; init; }

    public string Dimension { get; init; } = string.Empty;

    public string? Application { get; init; }

    public double TargetValue { get; init; }

    public int? Percentile { get; init; }

    public int WindowSeconds { get; init; }

    public bool Enabled { get; init; }

    /// <summary>False until the evaluator has produced a status for this objective.</summary>
    public bool Evaluated { get; init; }

    /// <summary>Measured value over the window: a ratio for rate/attainment kinds, observed ms / depth for threshold kinds.</summary>
    public double Attainment { get; init; }

    /// <summary>Fraction of error budget remaining; negative when the objective is being missed.</summary>
    public double BudgetRemaining { get; init; }

    public double BurnRateShort { get; init; }

    public double BurnRateLong { get; init; }

    public SloState State { get; init; }

    public DateTime? AcknowledgedUntil { get; init; }

    public DateTime? LastEvaluatedAt { get; init; }
}

public sealed class SloListModel
{
    public IReadOnlyList<SloObjectiveModel> Items { get; init; } = [];
}
