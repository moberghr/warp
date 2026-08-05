namespace Warp.Core.Metrics;

/// <summary>
/// The single source of truth for Warp's latency-histogram bucket boundaries (§8.30/§8.33). The internal DB key
/// families (<c>JobStatsKeys</c>, <c>QueueWaitKeys</c>, <c>AdapterCounterKeys</c>, <c>EndpointCounterKeys</c>,
/// <c>ClientEventKeys</c>) build their <c>Buckets</c> ladders from these finite bounds (appending an
/// <see cref="int.MaxValue"/> overflow rung for the percentile walk), and an OpenTelemetry consumer configures the
/// matching histogram Views from <see cref="Views"/> — so the DB percentile walk and a Prometheus
/// <c>histogram_quantile</c> read the same distribution instead of two hand-copied ladders drifting apart.
/// </summary>
public static class WarpHistogramBuckets
{
    /// <summary>Job-domain latency (execution + queue-wait) — extends to 5 min so a 30 s/60 s latency SLO is observable (§8.31).</summary>
    public static readonly int[] JobScale = [5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000, 30000, 60000, 300000];

    /// <summary>HTTP-scale latency (outbound adapters, inbound endpoints) — caps at 10 s.</summary>
    public static readonly int[] HttpScale = [5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000];

    /// <summary>Core-Web-Vital value distribution (client vitals) — a vitals-tuned ladder, finer through the 100–3000 ms band.</summary>
    public static readonly int[] ClientVitals = [50, 100, 200, 300, 500, 800, 1000, 1500, 2000, 2500, 3000, 4000, 5000, 7500, 10000];

    /// <summary>
    /// The canonical OTel latency-histogram instrument → boundary-ladder mapping. An OpenTelemetry consumer adds an
    /// explicit-bucket View per entry (the finite bounds here; the <c>+Inf</c> overflow bucket is implicit) so the
    /// exported histogram matches the DB ladder the local dashboard uses.
    /// </summary>
    public static readonly IReadOnlyList<(string Instrument, int[] Bounds)> Views =
    [
        ("warp.job.execution.duration", JobScale),
        ("warp.job.queue.wait", JobScale),
        ("warp.adapter.duration", HttpScale),
        ("warp.endpoint.duration", HttpScale),
        ("warp.client.vitals", ClientVitals),
    ];

    /// <summary>A ladder plus its <see cref="int.MaxValue"/> overflow rung — the shape the DB <c>*Keys.Buckets</c> use.</summary>
    public static int[] WithOverflow(int[] finiteBounds) => [.. finiteBounds, int.MaxValue];
}
