using Microsoft.AspNetCore.Http;
using Warp.Core.Enums;

namespace Warp.Http.Observability;

/// <summary>
/// Configuration for inbound endpoint observability (<c>opt.AddEndpointObservability(...)</c>) — the
/// inbound mirror of the outbound adapter recording options. Controls per-call capture for requests to
/// Warp-exposed HTTP endpoints. Retention (age + count) is global on <c>WarpConfiguration</c>
/// (<c>EndpointCallLogRetention</c> / <c>EndpointCallLogRetentionCount</c>); this type carries the
/// HTTP-specific capture knobs.
/// <para>
/// Caller metadata (IP / user-agent / authenticated user) is captured onto the row by default because it
/// is the point of the feature. It is PII (§1.2): header values pass through <see cref="RedactedHeaders"/>,
/// the feature is opt-in, and captured payloads are never logged at Info+.
/// </para>
/// </summary>
public sealed class WarpEndpointObservabilityOptions
{
    /// <summary>Whether a row is written per call. <c>All</c> (default) writes every call; <c>FailuresOnly</c> is the volume knob for chatty endpoints (counters still record all calls).</summary>
    public CallRecording RecordCalls { get; set; } = CallRecording.All;

    /// <summary>
    /// Fraction of <b>successful</b> request rows to keep (0.0–1.0). Applies to the raw call-log row only:
    /// failures (status &gt;= 500 or an exception) are always kept, and the <c>Counter</c>/<c>Statistic</c>
    /// aggregates (counts, error rate, latency percentiles) always record every request regardless of this
    /// value. <c>1.0</c> (default) keeps all rows; <c>0.0</c> keeps none (equivalent to
    /// <see cref="CallRecording.FailuresOnly"/> for successes); <c>0.1</c> keeps ~10%. A request matched by
    /// <see cref="ForceCapture"/> writes the row regardless of the sample.
    /// </summary>
    public double SampleRate { get; set; } = 1.0;

    /// <summary>
    /// Per-request predicate evaluated at request START (before body buffering) — when it returns
    /// <c>true</c>, the request is captured at full fidelity (bodies and headers, even on success and even
    /// if the capture tier is <c>None</c>/<c>OnFailure</c>) and its call-log row is always written,
    /// bypassing <see cref="SampleRate"/> and <see cref="RecordCalls"/>. Null (default) forces nothing. Use
    /// for targeted diagnostics (a debug header, a specific caller). It is PII-owned (§1.2): forcing capture
    /// stores request/response bodies + headers (still redacted per <see cref="RedactedHeaders"/>).
    /// </summary>
    public Func<HttpContext, bool>? ForceCapture { get; set; }

    /// <summary>
    /// Capture tier for the request body. Unlike the response body, the request body must be buffered
    /// up-front (before the handler consumes it), so <c>OnFailure</c> would force buffering EVERY request
    /// (spilling large uploads to disk). To avoid that, request bodies are captured only under
    /// <c>Always</c> or a matched <c>ForceCapture</c> — <c>OnFailure</c> here behaves like <c>None</c> for
    /// the request body (response bodies + headers still honour <c>OnFailure</c>). Default OnFailure.
    /// </summary>
    public CaptureMode CaptureRequestBodies { get; set; } = CaptureMode.OnFailure;

    /// <summary>Capture tier for the response body (None / OnFailure / Always). Default OnFailure.</summary>
    public CaptureMode CaptureResponseBodies { get; set; } = CaptureMode.OnFailure;

    /// <summary>Capture tier for request + response headers (None / OnFailure / Always). Default OnFailure.</summary>
    public CaptureMode CaptureHeaders { get; set; } = CaptureMode.OnFailure;

    /// <summary>Truncation cap for a captured body, in bytes. Default 8 KB.</summary>
    public int MaxCapturedBodySize { get; set; } = 8 * 1024;

    /// <summary>Truncation cap for captured headers, in bytes. Default 4 KB.</summary>
    public int MaxCapturedHeaderSize { get; set; } = 4 * 1024;

    /// <summary>
    /// Trust <c>X-Forwarded-For</c> for the caller IP instead of the immediate peer. Uses the <b>leftmost</b>
    /// entry — the original client — which is what you want for caller attribution, but is also the
    /// client-<i>controlled</i> end and therefore spoofable unless your proxy strips/rewrites inbound
    /// <c>X-Forwarded-For</c>. Off by default; only enable behind a trusted proxy you control, and treat the
    /// captured IP as advisory (it's a diagnostic, not an authorization input).
    /// </summary>
    public bool UseForwardedForIp { get; set; }

    /// <summary>
    /// Resolves an optional low-cardinality caller GROUP key from the request (e.g. a client-id header, an
    /// API-key label, a tenant claim) for per-caller stats. Null (default) records no group. Keep it bounded
    /// — group is a metrics dimension; a high-cardinality value (raw IP, user id) inflates the stat tables.
    /// </summary>
    public Func<HttpContext, string?>? GroupSelector { get; set; }

    /// <summary>
    /// Custom per-request enrichment. Called once per observed request to add free-form key/value tags
    /// (user id, tenant, correlation id, …) stored on the call-log row and shown in the dashboard call
    /// drawer. Unlike <see cref="GroupSelector"/>, tags are NOT a metrics dimension (no cardinality limit),
    /// so high-cardinality values are fine. It is PII-owned (§1.2) — do not put secrets here. Throwing is
    /// swallowed (recording never fails a request).
    /// </summary>
    public Action<HttpContext, IDictionary<string, string>>? Enrich { get; set; }

    /// <summary>
    /// Case-insensitive header denylist whose values are stored as <c>***</c> when headers are captured.
    /// Fully user-owned (§1.2): prepopulated with the common credential-bearing headers; callers may
    /// <c>Add</c>/<c>Remove</c>/<c>Clear</c> freely.
    /// </summary>
    public ISet<string> RedactedHeaders { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Proxy-Authorization",
        "Cookie",
        "Set-Cookie",
        "X-Api-Key",
    };
}
