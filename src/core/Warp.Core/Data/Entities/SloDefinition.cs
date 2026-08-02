using Warp.Core.Enums;

namespace Warp.Core.Data.Entities;

/// <summary>
/// One durable Service-Level Objective (§8.31): a promise about a slice of Warp's traffic — a success-rate,
/// a queue-wait/execution latency percentile, a backlog depth, or a deadline-attainment ratio — scoped to a
/// queue / job-type (<see cref="Dimension"/>) and optionally an executor <see cref="Application"/> (§8.23).
/// Objectives are seeded from config (<c>AddSlo</c>) and editable in the dashboard; the DB row is the source of
/// truth (config seeding is insert-if-absent, so a dashboard edit is never clobbered on restart). Always-in-schema
/// (§2.11), mirrored by <c>WarpServerContext</c> (§2.14). The rolling status lives on <see cref="SloEvaluation"/>.
/// </summary>
public class SloDefinition
{
    public int Id { get; set; }

    /// <summary>Human label, shown in the dashboard and carried on the breach event.</summary>
    public string Name { get; set; } = string.Empty;

    public SloKind Kind { get; set; }

    /// <summary>What the objective is scoped to: a queue name, a job-type, or <c>*</c> for all. Sanitized on read.</summary>
    public string Dimension { get; set; } = "*";

    /// <summary>Executor application to scope to (§8.23), reading the <c>-app</c> aggregate slice. Null ⇒ not app-scoped.</summary>
    public string? Application { get; set; }

    /// <summary>The target: a ratio (0.995) for rate/attainment kinds, milliseconds for latency kinds, a job count for depth.</summary>
    public double TargetValue { get; set; }

    /// <summary>Percentile (90/95/99) for the latency kinds; null for rate/depth kinds.</summary>
    public int? Percentile { get; set; }

    /// <summary>Rolling evaluation window in seconds (e.g. 3600). The short burn window is derived as this / 12.</summary>
    public int WindowSeconds { get; set; } = 3600;

    public bool Enabled { get; set; } = true;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
