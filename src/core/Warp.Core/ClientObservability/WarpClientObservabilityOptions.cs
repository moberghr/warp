using Warp.Core.Observability;

namespace Warp.Core.ClientObservability;

/// <summary>
/// Configuration for client (browser) observability (§8.27), set via <c>opt.AddClientObservability(o =&gt; …)</c>
/// in the <c>AddWarp</c> lambda. All values are plain config (no <c>HttpContext</c> dependency), so the options
/// live in Core; the HTTP binding (<c>MapWarpClientObservability</c>) reads the ingest-facing knobs
/// (keys/origins/limits) and Core reads the recording knobs (sink/capture/cardinality). Log-retention is
/// global on <c>WarpConfiguration</c> (§8.22).
/// </summary>
public sealed class WarpClientObservabilityOptions
{
    /// <summary>Where completed events are routed (§8.24). <c>Otel</c> skips the DB recorder/flusher; the meters still fire.</summary>
    public RecordingSink Sink { get; set; } = RecordingSink.Database;

    /// <summary>The path the ingest endpoint + browser script are mapped at by <c>MapWarpClientObservability()</c>. The script is served at <c>{IngestPath}/client.js</c>.</summary>
    public string IngestPath { get; set; } = "/warp/ingest";

    /// <summary>Public write-only ingest keys → the TRUSTED application name they authorize (a DSN). Empty ⇒ ingest disabled (endpoint 404s).</summary>
    public IDictionary<string, string> IngestKeys { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Browser origins allowed to post (CORS allowlist). Empty ⇒ no cross-origin posts accepted.</summary>
    public ISet<string> AllowedOrigins { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Capture the caller IP (PII, §1.2) — off by default; opt in explicitly.</summary>
    public bool CaptureRemoteIp { get; set; }

    public bool CaptureUserAgent { get; set; } = true;

    /// <summary>Byte cap for each captured string field (message/stack/properties/breadcrumbs), truncated on ingest.</summary>
    public int MaxCapturedBodySize { get; set; } = 8 * 1024;

    /// <summary>Hard cap on a single ingest POST body; larger ⇒ 413, dropped.</summary>
    public int MaxIngestBytes { get; set; } = 64 * 1024;

    /// <summary>Max events accepted in one batch; the rest are dropped.</summary>
    public int MaxEventsPerBatch { get; set; } = 100;

    /// <summary>Per-key in-memory rate cap (events/minute) — a public endpoint spam guard. Never DB-backed (the ingest path must not touch the DB).</summary>
    public int RateLimitPerMinute { get; set; } = 6_000;

    /// <summary>Cardinality caps for the per-name aggregate dimension (§8.19); names beyond the cap fold to <c>{other}</c>.</summary>
    public int MaxDistinctErrorNames { get; set; } = 200;

    public int MaxDistinctEventNames { get; set; } = 200;

    /// <summary>Property keys whose values are redacted before storage (case-insensitive, §1.2). Prepopulated with common secrets; fully overridable.</summary>
    public ISet<string> RedactedKeys { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "cookie", "password", "token", "secret", "apikey", "api_key",
    };

    /// <summary>Registers a DSN: a public write-only <paramref name="key"/> that authorizes writes as <paramref name="application"/>.</summary>
    public WarpClientObservabilityOptions AddIngestKey(string application, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(application);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        IngestKeys[key] = application;

        return this;
    }
}
