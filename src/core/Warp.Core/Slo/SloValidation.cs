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

    /// <summary>Minimum <c>WindowSeconds</c> for metric kinds: one closed bucket plus the flush lag.</summary>
    public const int MinMetricWindowSeconds = 600;

    public static bool IsMetricKind(SloKind kind) => kind is SloKind.MetricRatio or SloKind.MetricThreshold or SloKind.MetricVolumeFloor or SloKind.MetricGap;

    /// <summary>
    /// Validates and normalizes <paramref name="definition"/> in place. Returns <c>true</c> when valid; otherwise
    /// <c>false</c> with a human-readable <paramref name="error"/> the endpoint returns as a 400. This overload has
    /// no configuration or instrument registry, so the retention (R29) and gauge checks are skipped; the upsert
    /// endpoint (B8) should call the overload below.
    /// </summary>
    public static bool TryValidate(SloDefinition definition, out string? error)
        => TryValidate(definition, null, null, out error);

    /// <summary>
    /// As <see cref="TryValidate(SloDefinition, out string?)"/>, plus two checks that need outside knowledge.
    /// <paramref name="hourlyStatisticsRetention"/> (<c>WarpConfiguration.HourlyStatisticsRetention</c>) enables R29:
    /// a 7-day baseline needs retention of at least 7 days + window + 1 hour; <c>null</c> skips it.
    /// <paramref name="instrumentKind"/> is the registry kind of the objective's instrument when the caller knows it
    /// (the evaluator and the endpoint can look it up); a <see cref="MetricKind.Gauge"/> is rejected for metric
    /// kinds, <c>null</c> (unknown) is allowed. Non-metric kinds ignore both.
    /// </summary>
    public static bool TryValidate(SloDefinition definition, TimeSpan? hourlyStatisticsRetention, MetricKind? instrumentKind, out string? error)
        => TryValidate(definition, hourlyStatisticsRetention, instrumentKind, null, out error);

    /// <summary>
    /// As <see cref="TryValidate(SloDefinition, TimeSpan?, MetricKind?, out string?)"/>, plus the registry kind of a
    /// <see cref="SloKind.MetricGap"/> objective's <c>SecondaryMetric</c>: a <see cref="MetricKind.Gauge"/> secondary is
    /// rejected like a gauge primary (it has no event count to subtract); <c>null</c> (unknown) is allowed, and every
    /// other kind ignores it.
    /// </summary>
    public static bool TryValidate(SloDefinition definition, TimeSpan? hourlyStatisticsRetention, MetricKind? instrumentKind, MetricKind? secondaryInstrumentKind, out string? error)
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

        if (IsMetricKind(definition.Kind))
        {
            return TryValidateMetric(definition, hourlyStatisticsRetention, instrumentKind, secondaryInstrumentKind, out error);
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

    private static bool TryValidateMetric(SloDefinition definition, TimeSpan? hourlyRetention, MetricKind? instrumentKind, MetricKind? secondaryInstrumentKind, out string? error)
    {
        var kind = definition.Kind;

        if (definition.Dimension.Length > 128)
        {
            error = "Dimension must be a valid instrument name of at most 128 characters.";

            return false;
        }

        if (definition.WindowSeconds < MinMetricWindowSeconds)
        {
            error = $"{kind} WindowSeconds must be at least {MinMetricWindowSeconds}.";

            return false;
        }

        if (instrumentKind == MetricKind.Gauge)
        {
            error = $"{kind} cannot use gauge instrument '{definition.Dimension}': gauges have no event count. Use a counter, up-down counter or histogram.";

            return false;
        }

        if (kind == SloKind.MetricGap && secondaryInstrumentKind == MetricKind.Gauge)
        {
            error = $"{kind} cannot use gauge instrument '{definition.SecondaryMetric}' as SecondaryMetric: gauges have no event count. Use a counter, up-down counter or histogram.";

            return false;
        }

        if (!TryValidateLength(definition.TagFilter, 512, "TagFilter", out error)
            || !TryValidateLength(definition.SecondaryTagFilter, 512, "SecondaryTagFilter", out error)
            || !TryValidateLength(definition.SecondaryMetric, 128, "SecondaryMetric", out error)
            || !TryValidateLength(definition.RatioTag, 64, "RatioTag", out error)
            || !TryValidateLength(definition.GoodValues, 512, "GoodValues", out error)
            || !TryValidateLength(definition.BadValues, 512, "BadValues", out error))
        {
            return false;
        }

        if (!MetricFilter.TryParse(definition.TagFilter, out _, out var filterError))
        {
            error = $"TagFilter is invalid: {filterError}";

            return false;
        }

        if (!MetricFilter.TryParse(definition.SecondaryTagFilter, out _, out filterError))
        {
            error = $"SecondaryTagFilter is invalid: {filterError}";

            return false;
        }

        if (!TryValidateKindFields(definition, out error))
        {
            return false;
        }

        // Percentile is meaningless for metric kinds; fields that belong to other kinds are cleared so they can't mislead a reader.
        definition.Percentile = null;
        if (kind != SloKind.MetricRatio)
        {
            definition.RatioTag = null;
            definition.GoodValues = null;
            definition.BadValues = null;
        }

        if (kind != SloKind.MetricGap)
        {
            definition.SecondaryMetric = null;
            definition.SecondaryTagFilter = null;
        }

        if (kind is not (SloKind.MetricRatio or SloKind.MetricVolumeFloor))
        {
            definition.BaselineDays = null;
        }

        // R29 runs after the normalisation above, so a stray BaselineDays on a kind that never reads a baseline is
        // cleared instead of failing a retention check it has nothing to do with.
        if (definition.BaselineDays == 7 && hourlyRetention is { } retention)
        {
            var required = TimeSpan.FromDays(7) + TimeSpan.FromSeconds(definition.WindowSeconds) + TimeSpan.FromHours(1);
            if (retention < required)
            {
                error = $"BaselineDays = 7 requires HourlyStatisticsRetention of at least {required} (7 days + the {definition.WindowSeconds}s window + 1 hour) but it is {retention}. Raise HourlyStatisticsRetention or use BaselineDays = 1.";

                return false;
            }
        }

        error = null;

        return true;
    }

    private static bool TryValidateKindFields(SloDefinition definition, out string? error)
    {
        var target = definition.TargetValue;

        if (double.IsNaN(target) || double.IsInfinity(target))
        {
            error = $"{definition.Kind} target must be a finite number.";

            return false;
        }

        switch (definition.Kind)
        {
            case SloKind.MetricRatio:
                return TryValidateRatio(definition, out error);

            case SloKind.MetricVolumeFloor:
                if (definition.BaselineDays is not (1 or 7))
                {
                    error = "MetricVolumeFloor requires BaselineDays of 1 or 7.";

                    return false;
                }

                if (target is <= 0 or >= 1)
                {
                    error = "MetricVolumeFloor target must be a ratio between 0 and 1 (exclusive), e.g. 0.5.";

                    return false;
                }

                break;

            case SloKind.MetricGap:
                if (string.IsNullOrWhiteSpace(definition.SecondaryMetric))
                {
                    error = "MetricGap requires SecondaryMetric (the instrument subtracted from the primary one).";

                    return false;
                }

                if (target < 0)
                {
                    error = "MetricGap target must be zero or greater.";

                    return false;
                }

                break;

            default:
                if (target < 0)
                {
                    error = "MetricThreshold target must be zero or greater.";

                    return false;
                }

                break;
        }

        error = null;

        return true;
    }

    private static bool TryValidateRatio(SloDefinition definition, out string? error)
    {
        if (string.IsNullOrWhiteSpace(definition.RatioTag))
        {
            error = "MetricRatio requires RatioTag (the tag whose value marks an event good or bad).";

            return false;
        }

        if (string.IsNullOrWhiteSpace(definition.GoodValues) || string.IsNullOrWhiteSpace(definition.BadValues))
        {
            error = "MetricRatio requires non-empty GoodValues and BadValues.";

            return false;
        }

        if (!MetricValueList.TryParse(definition.GoodValues, out var good, out var listError))
        {
            error = $"GoodValues is invalid: {listError}";

            return false;
        }

        if (!MetricValueList.TryParse(definition.BadValues, out var bad, out listError))
        {
            error = $"BadValues is invalid: {listError}";

            return false;
        }

        // Spec §E: the two lists must not overlap. Equal literals, a literal under the other side's prefix ("500" vs
        // "5*"), nested prefixes ("5*" vs "50*") and a bare "*" all could count one event as both good and bad.
        if (good.OverlapsWith(bad))
        {
            error = "GoodValues and BadValues must not overlap: no value may match a pattern in both lists.";

            return false;
        }

        if (definition.BaselineDays is { } days && days is not (1 or 7))
        {
            error = "BaselineDays must be 1 or 7.";

            return false;
        }

        if (definition.TargetValue is <= 0 or >= 1)
        {
            error = definition.BaselineDays is null
                ? "MetricRatio target must be a ratio between 0 and 1 (exclusive), e.g. 0.99."
                : "MetricRatio target with a baseline is the maximum drop in ratio points and must be between 0 and 1 (exclusive).";

            return false;
        }

        error = null;

        return true;
    }

    private static bool TryValidateLength(string? value, int max, string field, out string? error)
    {
        if (value is not null && value.Length > max)
        {
            error = $"{field} must be at most {max} characters.";

            return false;
        }

        error = null;

        return true;
    }
}
