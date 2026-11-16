using Warp.Core.Services;

namespace Warp.Core.Data.Entities;

/// <summary>
/// One stored aggregate of a collected <c>System.Diagnostics.Metrics</c> series for one time bucket: count, sum,
/// min, max and — for a histogram — bucket counts. Written as partial fine-tier rows by each process's
/// <c>MetricFlusher</c>, compacted and rolled up to the hourly and daily tiers by <c>MetricRollup</c>; readers sum
/// by key, so several rows for one (series, bucket) are normal until compaction. Always-in-schema (§2.11),
/// mirrored by <c>WarpServerContext</c> (§2.14).
/// </summary>
public class MetricPoint
{
    public long Id { get; set; }

    /// <summary>The instrument name.</summary>
    public string Instrument { get; set; } = string.Empty;

    /// <summary>The application the series belongs to (<c>WarpConfiguration.ApplicationName</c>); null when none is set.</summary>
    public string? Application { get; set; }

    /// <summary>
    /// The application version (<c>WarpConfiguration.ApplicationVersion</c>), only when the writing host opted in with
    /// <c>AddMetrics(m =&gt; m.ByVersion())</c>; null otherwise, so a host that did not opt in writes the same rows as before.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>The canonical tag set <c>k1=v1,k2=v2</c> (keys sorted), or <c>-</c> when the series has no tags.</summary>
    public string Tags { get; set; } = string.Empty;

    public MetricTier Tier { get; set; }

    /// <summary>The UTC start of the bucket, aligned to <see cref="Tier"/>.</summary>
    public DateTime BucketStart { get; set; }

    /// <summary>The number of measurements recorded into the bucket.</summary>
    public long Count { get; set; }

    public double Sum { get; set; }

    /// <summary>The smallest measurement; null for kinds that do not track it.</summary>
    public double? Min { get; set; }

    /// <summary>The largest measurement; null for kinds that do not track it.</summary>
    public double? Max { get; set; }

    /// <summary>Histograms: CSV of counts aligned to <see cref="Bounds"/>, plus the overflow bucket.</summary>
    public string? Buckets { get; set; }

    /// <summary>Histograms: CSV of bucket upper bounds, invariant culture, round-trip format.</summary>
    public string? Bounds { get; set; }

    /// <summary>True once the row is the single compacted row for its key.</summary>
    public bool Compacted { get; set; }
}
