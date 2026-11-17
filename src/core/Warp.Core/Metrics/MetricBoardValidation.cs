using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Models;
using Warp.Core.Services;
using Warp.Core.Slo;

namespace Warp.Core.Metrics;

/// <summary>
/// Boundary validation for a <see cref="MetricBoardTile"/> (#326), shared by the dashboard upsert endpoint and the
/// config seeder so a bad tile can be persisted from neither. On success the tile is normalized: fields that do not
/// apply to its kind or status rule are cleared, and defaults are made explicit (at most, average, either direction).
/// The filter and good/bad grammar is the one <c>MetricFilter</c> grammar of the Metrics page and the SLOs.
/// </summary>
public static class MetricBoardValidation
{
    /// <summary>The most tiles one board holds; the board read and the upsert endpoint both enforce it.</summary>
    public const int MaxTiles = 48;

    /// <summary>
    /// Whether a tile of <paramref name="tile"/> kind can read an instrument of registry kind
    /// <paramref name="instrument"/>: a ratio counts events, so it refuses a gauge (the <c>MetricRatio</c> rule); a value
    /// tile reads anything (a gauge as its level, the event-counting kinds as their events).
    /// </summary>
    public static bool AcceptsInstrument(MetricTileKind tile, MetricKind instrument)
        => tile != MetricTileKind.Ratio || instrument != MetricKind.Gauge;

    /// <summary>
    /// Validates and normalizes <paramref name="tile"/> in place. <paramref name="instrumentKind"/> is the registry kind
    /// of its instrument when the caller knows it; an instrument not in the registry yet (null) is allowed.
    /// </summary>
    public static bool TryValidate(MetricBoardTile tile, MetricKind? instrumentKind, out string? error)
    {
        ArgumentNullException.ThrowIfNull(tile);

        if (!TryValidateShape(tile, out error) || !TryValidateKind(tile, instrumentKind, out error) || !TryValidateRule(tile, out error))
        {
            return false;
        }

        error = null;

        return true;
    }

    private static bool TryValidateShape(MetricBoardTile tile, out string? error)
    {
        if (string.IsNullOrWhiteSpace(tile.Name) || tile.Name.Length > 200)
        {
            error = "Name is required and must be at most 200 characters.";

            return false;
        }

        if (string.IsNullOrWhiteSpace(tile.Instrument) || tile.Instrument.Length > 128)
        {
            error = "Instrument is required and must be a valid instrument name of at most 128 characters.";

            return false;
        }

        if (tile.Position < 0)
        {
            error = "Position must be zero or greater.";

            return false;
        }

        tile.Application = string.IsNullOrWhiteSpace(tile.Application) ? null : tile.Application.Trim();
        tile.TagFilter = string.IsNullOrWhiteSpace(tile.TagFilter) ? null : tile.TagFilter.Trim();
        if (tile.Application?.Length > 200)
        {
            error = "Application must be at most 200 characters.";

            return false;
        }

        if (tile.TagFilter?.Length > 512)
        {
            error = "TagFilter must be at most 512 characters.";

            return false;
        }

        if (!MetricFilter.TryParse(tile.TagFilter, out var filter, out var filterError))
        {
            error = $"TagFilter is invalid: {filterError}";

            return false;
        }

        if (filter.Constrains(MetricSeriesQuery.VersionKey))
        {
            error = $"TagFilter is invalid: {MetricFilter.ReservedVersionKeyError}";

            return false;
        }

        error = null;

        return true;
    }

    private static bool TryValidateKind(MetricBoardTile tile, MetricKind? instrumentKind, out string? error)
    {
        if (!Enum.IsDefined(tile.Kind))
        {
            error = $"Kind '{(int)tile.Kind}' is not valid: use Value (1) or Ratio (2).";

            return false;
        }

        if (instrumentKind is { } known && !AcceptsInstrument(tile.Kind, known))
        {
            error = $"A ratio tile cannot use gauge instrument '{tile.Instrument}': gauges have no event count. Use a counter, up-down counter or histogram, or a value tile.";

            return false;
        }

        if (tile.Kind == MetricTileKind.Ratio)
        {
            tile.GaugeAggregate = null;

            return TryValidateRatio(tile, out error);
        }

        tile.RatioTag = null;
        tile.GoodValues = null;
        tile.BadValues = null;

        if (tile.GaugeAggregate is { } aggregate && !Enum.IsDefined(aggregate))
        {
            error = $"GaugeAggregate '{(int)aggregate}' is not valid: use Average (1), Min (2) or Max (3).";

            return false;
        }

        // The aggregate means something only on a gauge; on a known other kind it is cleared so it cannot mislead.
        tile.GaugeAggregate = instrumentKind switch
        {
            MetricKind.Gauge => tile.GaugeAggregate ?? SloGaugeAggregate.Average,
            null => tile.GaugeAggregate,
            _ => null,
        };

        error = null;

        return true;
    }

    private static bool TryValidateRatio(MetricBoardTile tile, out string? error)
    {
        if (string.IsNullOrWhiteSpace(tile.RatioTag) || tile.RatioTag.Length > 64)
        {
            error = "A ratio tile requires RatioTag (the tag whose value marks an event good or bad), at most 64 characters.";

            return false;
        }

        if (string.Equals(tile.RatioTag.Trim(), MetricSeriesQuery.VersionKey, StringComparison.Ordinal))
        {
            error = $"RatioTag is invalid: {MetricFilter.ReservedVersionKeyError}";

            return false;
        }

        if (string.IsNullOrWhiteSpace(tile.GoodValues) || string.IsNullOrWhiteSpace(tile.BadValues)
            || tile.GoodValues.Length > 512 || tile.BadValues.Length > 512)
        {
            error = "A ratio tile requires non-empty GoodValues and BadValues of at most 512 characters each.";

            return false;
        }

        if (!MetricValueList.TryParse(tile.GoodValues, out var good, out var listError))
        {
            error = $"GoodValues is invalid: {listError}";

            return false;
        }

        if (!MetricValueList.TryParse(tile.BadValues, out var bad, out listError))
        {
            error = $"BadValues is invalid: {listError}";

            return false;
        }

        if (good.OverlapsWith(bad))
        {
            error = "GoodValues and BadValues must not overlap: no value may match a pattern in both lists.";

            return false;
        }

        error = null;

        return true;
    }

    private static bool TryValidateRule(MetricBoardTile tile, out string? error)
    {
        error = null;
        switch (tile.StatusRule)
        {
            case MetricTileStatusRule.None:
                ClearThreshold(tile);
                ClearDeviation(tile);

                return true;

            case MetricTileStatusRule.Threshold:
                if (tile.Threshold is not { } threshold || !double.IsFinite(threshold))
                {
                    error = "A threshold rule requires a finite Threshold.";

                    return false;
                }

                // A ratio tile reads a 0–1 share: a bound outside it could never be met (or never be missed).
                if (tile.Kind == MetricTileKind.Ratio && threshold is < 0 or > 1)
                {
                    error = "A ratio tile's Threshold is a share between 0 and 1.";

                    return false;
                }

                if (tile.Comparison is { } comparison && !Enum.IsDefined(comparison))
                {
                    error = $"Comparison '{(int)comparison}' is not valid: use AtMost (1) or AtLeast (2).";

                    return false;
                }

                tile.Comparison ??= SloComparison.AtMost;
                ClearDeviation(tile);

                return true;

            case MetricTileStatusRule.Deviation:
                if (!TryValidateDeviation(tile, out error))
                {
                    return false;
                }

                ClearThreshold(tile);

                return true;

            default:
                error = $"StatusRule '{(int)tile.StatusRule}' is not valid: use None (1), Threshold (2) or Deviation (3).";

                return false;
        }
    }

    private static bool TryValidateDeviation(MetricBoardTile tile, out string? error)
    {
        if (tile.BaselineDays is not (1 or 7))
        {
            error = "A deviation rule requires BaselineDays of 1 or 7.";

            return false;
        }

        if (tile.DeviationDirection is { } direction && !Enum.IsDefined(direction))
        {
            error = $"DeviationDirection '{(int)direction}' is not valid: use Drop (1), Rise (2) or Either (3).";

            return false;
        }

        tile.DeviationDirection ??= MetricDeviationDirection.Either;
        if (tile.MaxDeviation is not { } max || !double.IsFinite(max) || max <= 0)
        {
            error = "A deviation rule requires a MaxDeviation greater than zero.";

            return false;
        }

        // A ratio moves in ratio points (at most 1); a value can drop at most all of itself, but may rise by any factor.
        var bounded = tile.Kind == MetricTileKind.Ratio || tile.DeviationDirection != MetricDeviationDirection.Rise;
        if (bounded && max >= 1)
        {
            error = tile.Kind == MetricTileKind.Ratio
                ? "A ratio tile's MaxDeviation is in ratio points and must be below 1 (0.02 = 2 points)."
                : "A value tile's MaxDeviation for a drop is a fraction of the baseline and must be below 1 (0.3 = 30 %).";

            return false;
        }

        error = null;

        return true;
    }

    private static void ClearThreshold(MetricBoardTile tile)
    {
        tile.Threshold = null;
        tile.Comparison = null;
    }

    private static void ClearDeviation(MetricBoardTile tile)
    {
        tile.BaselineDays = null;
        tile.MaxDeviation = null;
        tile.DeviationDirection = null;
    }
}

/// <summary>
/// The one place a tile is validated against the live instrument registry (one single-row read), shared by the
/// dashboard upsert and the config seeder, as <see cref="SloDefinitionValidator"/> is for objectives.
/// </summary>
public static class MetricBoardTileValidator
{
    public static async Task<(bool Ok, string? Error)> ValidateAsync(MetricBoardTile tile, IMetricQueryService metrics, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tile);
        ArgumentNullException.ThrowIfNull(metrics);

        var kind = string.IsNullOrEmpty(tile.Instrument) ? null : await metrics.GetInstrumentKindAsync(tile.Instrument, ct);
        var ok = MetricBoardValidation.TryValidate(tile, kind, out var error);

        return (ok, error);
    }
}
