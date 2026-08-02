using Warp.Core.Data.Entities;
using Warp.Core.Enums;

namespace Warp.Core.Slo;

/// <summary>
/// Boundary validation for an <see cref="SloDefinition"/> (§8.31), shared by the dashboard upsert endpoint and the
/// config seeder so a bad objective can never be persisted from either path. Without it, an out-of-range target or
/// window silently breaks evaluation in one direction or the other — a rate target &gt; 1 pins the objective to
/// Breaching forever; a window ≤ 0 pins it to Healthy (breaches disabled). On success the definition is normalized:
/// latency objectives get an explicit default percentile (95) instead of a silent one, and non-latency objectives
/// have their percentile cleared.
/// </summary>
public static class SloValidation
{
    /// <summary>The default percentile applied to a latency objective that leaves <c>Percentile</c> unset.</summary>
    public const int DefaultLatencyPercentile = 95;

    public static bool IsRateKind(SloKind kind) => kind is SloKind.SuccessRate or SloKind.DeadlineAttainment;

    public static bool IsLatencyKind(SloKind kind) => kind is SloKind.QueueWaitLatency or SloKind.ExecutionLatency;

    /// <summary>
    /// Validates and normalizes <paramref name="definition"/> in place. Returns <c>true</c> when valid; otherwise
    /// <c>false</c> with a human-readable <paramref name="error"/> the endpoint returns as a 400.
    /// </summary>
    public static bool TryValidate(SloDefinition definition, out string? error)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (!Enum.IsDefined(definition.Kind))
        {
            error = $"Kind '{(int)definition.Kind}' is not a valid SLO kind.";

            return false;
        }

        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            error = "Name is required.";

            return false;
        }

        if (string.IsNullOrWhiteSpace(definition.Dimension))
        {
            error = "Dimension is required (job type, queue, or application).";

            return false;
        }

        if (definition.WindowSeconds <= 0)
        {
            error = "WindowSeconds must be greater than zero.";

            return false;
        }

        if (IsRateKind(definition.Kind))
        {
            if (definition.TargetValue is <= 0 or >= 1)
            {
                error = $"{definition.Kind} target must be a ratio between 0 and 1 (exclusive), e.g. 0.995.";

                return false;
            }
        }
        else if (definition.TargetValue <= 0)
        {
            error = $"{definition.Kind} target must be greater than zero.";

            return false;
        }

        if (IsLatencyKind(definition.Kind))
        {
            if (definition.Percentile is { } p && p is < 1 or > 99)
            {
                error = "Percentile must be between 1 and 99.";

                return false;
            }

            definition.Percentile ??= DefaultLatencyPercentile;
        }
        else
        {
            // Percentile is meaningless for rate/backlog kinds — clear it so a stray value can't mislead a reader.
            definition.Percentile = null;
        }

        error = null;

        return true;
    }
}
