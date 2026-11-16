using Warp.Core.Enums;

namespace Warp.Core.Data.Entities;

/// <summary>
/// The registry row of one collected instrument, keyed by its name: its meter, kind, unit, bucket bounds, the
/// tag keys observed on it and whether it ever collapsed on a cardinality cap. Lets the dashboard list
/// instruments without scanning <see cref="MetricPoint"/>. Written by <c>MetricFlusher</c> in a separate save from
/// the points, so a registry failure never loses data. Always-in-schema (§2.11), mirrored by
/// <c>WarpServerContext</c> (§2.14).
/// </summary>
public class MetricInstrument
{
    /// <summary>The instrument name; the primary key. If two meters publish one name, the first wins.</summary>
    public string Name { get; set; } = string.Empty;

    public string Meter { get; set; } = string.Empty;

    public MetricKind Kind { get; set; }

    public string? Unit { get; set; }

    public string? Description { get; set; }

    /// <summary>Histograms: CSV of bucket upper bounds, invariant culture, round-trip format.</summary>
    public string? Bounds { get; set; }

    /// <summary>CSV of the tag keys observed on the instrument.</summary>
    public string? TagKeys { get; set; }

    public DateTime FirstSeenAt { get; set; }

    public DateTime LastSeenAt { get; set; }

    /// <summary>When the instrument last collapsed on a cardinality cap; null when it never did.</summary>
    public DateTime? CollapsedAt { get; set; }

    /// <summary>CSV of the tag keys that collapsed (the collapse reasons when no key is attributable). Never a tag value (§1.2).</summary>
    public string? CollapsedTags { get; set; }
}
