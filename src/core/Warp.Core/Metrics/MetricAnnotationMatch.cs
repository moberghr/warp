using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Models;
using Warp.Core.Slo;

namespace Warp.Core.Metrics;

// Pure rules relating annotations (#333) to windows and health-board tiles.
internal static class MetricAnnotationMatch
{
    // Active at the window's last moment: started before its end and not ended before it. A live board's window ends
    // now, so this is "active now"; a past window asks about its own end, never about today.
    public static bool ActiveAtEnd(MetricAnnotationModel annotation, DateTime to)
    {
        ArgumentNullException.ThrowIfNull(annotation);

        return annotation.StartsAt < to && (annotation.EndsAt is null || annotation.EndsAt >= to);
    }

    // An instrument note applies to a tile on the same instrument when some event could match both the note's filter
    // and the tile's: no shared tag key with incompatible values. A note without a filter applies to every tile on the
    // instrument; a note on another tag than the tile filters by applies too (it is about part of what the tile reads).
    public static bool AppliesTo(MetricAnnotationModel annotation, MetricBoardTile tile)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        ArgumentNullException.ThrowIfNull(tile);

        if (annotation.Target != MetricAnnotationTarget.Instrument
            || !string.Equals(annotation.Instrument, tile.Instrument, StringComparison.Ordinal))
        {
            return false;
        }

        if (!MetricFilter.TryParse(annotation.TagFilter, out var noteFilter, out _))
        {
            return false;
        }

        return !MetricFilter.TryParse(tile.TagFilter, out var tileFilter, out _) || noteFilter.Overlaps(tileFilter);
    }

    // Acknowledged noise: a Warning or Breaching tile reads Acknowledged while an acknowledged-noise note that applies to
    // it is active at the window's end. The events still count (they cannot be told apart from the rest), so only the
    // verdict changes; Healthy and NoData are never touched.
    public static SloState? Acknowledge(SloState? status, IEnumerable<MetricAnnotationModel> applying, DateTime to)
    {
        ArgumentNullException.ThrowIfNull(applying);

        if (status is not (SloState.Warning or SloState.Breaching))
        {
            return status;
        }

        return applying.Any(x => x.AcknowledgedNoise && ActiveAtEnd(x, to)) ? SloState.Acknowledged : status;
    }
}
