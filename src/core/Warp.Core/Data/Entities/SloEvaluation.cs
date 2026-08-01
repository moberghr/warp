using Warp.Core.Enums;

namespace Warp.Core.Data.Entities;

/// <summary>
/// The rolling computed status of one <see cref="SloDefinition"/> (§8.30), one row per objective (1:1 on
/// <see cref="SloDefinitionId"/>). Upserted every tick by the <c>SloEvaluator</c> off already-folded
/// <c>Statistic</c>/<c>Counter</c> aggregates — zero hot-path cost. The dashboard reads this directly; because
/// it is a single bounded row per objective there is no retention sweep. Always-in-schema (§2.11), mirrored by
/// <c>WarpServerContext</c> (§2.14).
/// </summary>
public class SloEvaluation
{
    /// <summary>PK and FK to the owning <see cref="SloDefinition"/> — one evaluation row per objective.</summary>
    public int SloDefinitionId { get; set; }

    /// <summary>Measured value over the window: a ratio for rate/attainment kinds, the observed percentile ms / depth otherwise.</summary>
    public double Attainment { get; set; }

    /// <summary>Fraction of the error budget still available in <c>[0..1]</c>; negative once the budget is blown.</summary>
    public double BudgetRemaining { get; set; }

    /// <summary>Burn rate over the short window (WindowSeconds / 12) — the fast-burn signal.</summary>
    public double BurnRateShort { get; set; }

    /// <summary>Burn rate over the full window — the slow-burn signal.</summary>
    public double BurnRateLong { get; set; }

    public SloState State { get; set; } = SloState.Healthy;

    /// <summary>When set and in the future, alerts are suppressed (operator ack). Null ⇒ not acknowledged.</summary>
    public DateTime? AcknowledgedUntil { get; set; }

    public DateTime LastEvaluatedAt { get; set; }
}
