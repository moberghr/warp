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
    /// The most <see cref="MetricTileKind.Chart"/> tiles one board holds (within <see cref="MaxTiles"/>): each chart is its
    /// own series request every refresh, while value and ratio tiles share one batched read.
    /// </summary>
    public const int MaxChartTiles = 12;

    /// <summary>The longest chart tile query (<see cref="MetricBoardTile.ChartQuery"/>).</summary>
    public const int MaxChartQueryLength = MetricChartQuery.MaxLength;

    /// <summary>The most boards a host holds, the Default board included.</summary>
    public const int MaxBoards = 100;

    /// <summary>The name the Default board is created with (it may be renamed later; its identity is <c>IsDefault</c>).</summary>
    public const string DefaultBoardName = "Default";

    /// <summary>The longest board name.</summary>
    public const int MaxBoardNameLength = 200;

    /// <summary>The longest board description.</summary>
    public const int MaxBoardDescriptionLength = 500;

    /// <summary>
    /// Validates and normalizes a board's editable fields in place: the name is trimmed, required and at most
    /// <see cref="MaxBoardNameLength"/> characters; a blank description or application is cleared, the rest bounded; the
    /// position is zero or greater (zero appends an insert after the last board).
    /// </summary>
    public static bool TryValidateBoard(MetricBoard board, out string? error)
    {
        ArgumentNullException.ThrowIfNull(board);

        board.Name = board.Name?.Trim() ?? string.Empty;
        if (board.Name.Length == 0 || board.Name.Length > MaxBoardNameLength)
        {
            error = $"Name is required and must be at most {MaxBoardNameLength} characters.";

            return false;
        }

        board.Description = string.IsNullOrWhiteSpace(board.Description) ? null : board.Description.Trim();
        if (board.Description?.Length > MaxBoardDescriptionLength)
        {
            error = $"Description must be at most {MaxBoardDescriptionLength} characters.";

            return false;
        }

        board.Application = string.IsNullOrWhiteSpace(board.Application) ? null : board.Application.Trim();
        if (board.Application?.Length > 200)
        {
            error = "Application must be at most 200 characters.";

            return false;
        }

        if (board.Position < 0)
        {
            error = "Position must be zero or greater.";

            return false;
        }

        error = null;

        return true;
    }

    /// <summary>
    /// Whether a tile of <paramref name="tile"/> kind can read an instrument of registry kind
    /// <paramref name="instrument"/>: a ratio counts events, so it refuses a gauge (the <c>MetricRatio</c> rule); a value
    /// tile reads anything (a gauge as its level, the event-counting kinds as their events), and so does a chart (the
    /// metric page charts every kind).
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

        // An SLO tile reads its objective, not an instrument: the non-null column is stored empty.
        if (tile.Kind == MetricTileKind.Slo)
        {
            tile.Instrument = string.Empty;
        }
        else if (string.IsNullOrWhiteSpace(tile.Instrument) || tile.Instrument.Length > 128)
        {
            error = "Instrument is required and must be a valid instrument name of at most 128 characters.";

            return false;
        }

        if (tile.Position < 0)
        {
            error = "Position must be zero or greater.";

            return false;
        }

        // A chart carries its application and filters in ChartQuery (the metric page's grammar), never in these columns; an
        // SLO tile shows its objective, which carries its own application and filters.
        if (tile.Kind is MetricTileKind.Chart or MetricTileKind.Slo)
        {
            tile.Application = null;
            tile.TagFilter = null;
        }

        // No board, or a non-positive id, is the Default board.
        tile.BoardId = tile.BoardId > 0 ? tile.BoardId : null;
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
            error = $"Kind '{(int)tile.Kind}' is not valid: use Value (1), Ratio (2), Chart (3) or Slo (4).";

            return false;
        }

        if (tile.Kind == MetricTileKind.Slo)
        {
            return TryValidateSlo(tile, out error);
        }

        tile.SloDefinitionId = null;
        if (tile.Kind == MetricTileKind.Chart)
        {
            return TryValidateChart(tile, out error);
        }

        tile.ChartQuery = null;

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

    // A chart is the metric page's chart: every value/ratio column is cleared, and the query is checked and stored canonical.
    private static bool TryValidateChart(MetricBoardTile tile, out string? error)
    {
        tile.RatioTag = null;
        tile.GoodValues = null;
        tile.BadValues = null;
        tile.GaugeAggregate = null;

        if (!MetricChartQuery.TryNormalize(tile.ChartQuery, out var normalized, out error))
        {
            return false;
        }

        tile.ChartQuery = normalized;

        return true;
    }

    // An SLO tile shows its objective: every value/ratio/chart column is cleared, and the objective id is required (that it
    // names an existing objective is checked at write time by the caller, against the store).
    private static bool TryValidateSlo(MetricBoardTile tile, out string? error)
    {
        tile.ChartQuery = null;
        tile.RatioTag = null;
        tile.GoodValues = null;
        tile.BadValues = null;
        tile.GaugeAggregate = null;

        if (tile.SloDefinitionId is not > 0)
        {
            error = "An SLO tile requires SloDefinitionId, the id of an existing objective.";

            return false;
        }

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

        // A chart is a picture, not a check: it has no status, so it never counts in the deploy verdict.
        if (tile.Kind == MetricTileKind.Chart && tile.StatusRule != MetricTileStatusRule.None)
        {
            error = "A chart tile has no status rule: use StatusRule None (1).";

            return false;
        }

        // An SLO tile shows the objective's own state; it adds no rule of its own.
        if (tile.Kind == MetricTileKind.Slo && tile.StatusRule != MetricTileStatusRule.None)
        {
            error = "An SLO tile has no status rule of its own (it shows the objective's state): use StatusRule None (1).";

            return false;
        }

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
    /// <summary>The error an SLO tile gets when its <see cref="MetricBoardTile.SloDefinitionId"/> names no objective.</summary>
    public const string ObjectiveNotFound = "The objective was not found: no SLO has that SloDefinitionId.";

    public static Task<(bool Ok, string? Error)> ValidateAsync(MetricBoardTile tile, IMetricQueryService metrics, CancellationToken ct)
        => ValidateAsync(tile, metrics, null, ct);

    /// <summary>
    /// Validates <paramref name="tile"/> against the registry and, for an SLO tile when <paramref name="objectives"/> is
    /// given, checks that its objective exists (one single-row read; <see cref="ObjectiveNotFound"/> otherwise).
    /// </summary>
    public static async Task<(bool Ok, string? Error)> ValidateAsync(MetricBoardTile tile, IMetricQueryService metrics, ISloQueryService? objectives, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tile);
        ArgumentNullException.ThrowIfNull(metrics);

        var kind = tile.Kind == MetricTileKind.Slo || string.IsNullOrEmpty(tile.Instrument) ? null : await metrics.GetInstrumentKindAsync(tile.Instrument, ct);
        if (!MetricBoardValidation.TryValidate(tile, kind, out var error))
        {
            return (false, error);
        }

        if (tile.Kind == MetricTileKind.Slo && objectives is not null && await objectives.GetObjective(tile.SloDefinitionId!.Value, ct) is null)
        {
            return (false, ObjectiveNotFound);
        }

        return (true, null);
    }
}
