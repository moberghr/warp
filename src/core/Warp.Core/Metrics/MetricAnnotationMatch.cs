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

    // A note covers a tile when it is about EVERYTHING the tile reads: same instrument, and either no filter or a filter
    // every one of whose constraints the tile's own filter implies (same key, the tile's pattern equal or narrower; for a
    // trailing '*', the tile's value or prefix starts with the note's). Only a covering note may acknowledge the tile: a
    // note on part of it ("outcome=error" on an unfiltered tile, "status=503" on a "status=5*" tile) is shown on the tile
    // (AppliesTo) but cannot excuse a problem in the rest. An unparseable tile filter is never covered (nothing to prove).
    public static bool Covers(MetricAnnotationModel annotation, MetricBoardTile tile)
    {
        if (!AppliesTo(annotation, tile)
            || !MetricFilter.TryParse(annotation.TagFilter, out var noteFilter, out _))
        {
            return false;
        }

        if (noteFilter.IsEmpty)
        {
            return true;
        }

        return MetricFilter.TryParse(tile.TagFilter, out var tileFilter, out _) && noteFilter.Covers(tileFilter);
    }

    // Acknowledged noise: a Warning or Breaching tile reads Acknowledged while an acknowledged-noise note that COVERS it
    // (callers pass only those, see Covers) is active at the window's end. The events still count (they cannot be told
    // apart from the rest), so only the verdict changes; Healthy and NoData are never touched.
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
