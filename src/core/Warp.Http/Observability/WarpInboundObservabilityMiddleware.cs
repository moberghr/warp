using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Endpoints;
using Warp.Core.Enums;
using Warp.Core.Logging;
using Warp.Core.Observability;

namespace Warp.Http.Observability;

/// <summary>
/// Observes inbound requests to Warp-mapped HTTP endpoints (the inbound mirror of the outbound adapter
/// <c>DelegatingHandler</c>). No-ops for any endpoint without a <see cref="WarpEndpointIdentity"/>, so it
/// never observes the host's own controllers or the dashboard. For each Warp request it records duration,
/// final status, outcome, caller metadata (IP / user-agent / authenticated user) and — per the capture
/// tiers — redacted/truncated request + response headers and bodies, handing a completed record to the
/// lossy <see cref="IEndpointCallRecorder"/>. Recording never blocks or fails the request.
/// </summary>
internal sealed class WarpInboundObservabilityMiddleware
{
    private const int MaxExceptionMessage = 4096;

    private readonly RequestDelegate _next;
    private readonly IEndpointCallRecorder? _recorder;
    private readonly WarpEndpointObservabilityOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _retention;
    private readonly string? _applicationName;
    private readonly bool _enrichSpan;

    public WarpInboundObservabilityMiddleware(
        RequestDelegate next,
        IEnumerable<IEndpointCallRecorder> recorders,
        IOptions<WarpEndpointObservabilityOptions> options,
        IOptions<WarpConfiguration> configuration,
        TimeProvider timeProvider)
    {
        _next = next;

        // Optional: the DB recorder is registered only under the Database/Both sink. Under Otel-only there is
        // none — the captured detail rides the request span instead (§8.24), so recording is span-only.
        _recorder = recorders.FirstOrDefault();
        _options = options.Value;
        _timeProvider = timeProvider;
        _retention = configuration.Value.EndpointCallLogRetention;

        // Endpoints have no Warp-created Activity, so we enrich the ambient ASP.NET request activity
        // instead. Read the process origin from the already-resolved WarpConfiguration (same value the
        // static WarpTelemetry.ApplicationName carries); null ⇒ nothing is stamped (feature off).
        _applicationName = configuration.Value.ApplicationName;

        // Under the Otel/Both sink, attach the captured call detail to the request span.
        _enrichSpan = _options.Sink is RecordingSink.Otel or RecordingSink.Both;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var identity = context.GetEndpoint()?.Metadata.GetMetadata<WarpEndpointIdentity>();
        if (identity is null)
        {
            await _next(context);

            return;
        }

        // Stamp the process origin on the ambient request activity once per observed request so
        // cross-application traces carry where the inbound call landed. Null ⇒ nothing added (feature off).
        if (_applicationName is not null)
        {
            Activity.Current?.SetTag(WarpTelemetryAttributes.WarpApplication, _applicationName);
        }

        // Force-capture is evaluated at request START (before body buffering) so a forced request buffers its
        // request/response bodies even when the tier is None/OnFailure. It also always writes the row,
        // bypassing SampleRate / RecordCalls (folded in RecordAsync).
        var forceCapture = _options.ForceCapture?.Invoke(context) ?? false;

        // Request-body capture requires buffering the body up-front (the transport consumes it), and the
        // decision must be made before the outcome is known. To avoid buffering EVERY request (spilling
        // large uploads to a temp file) for the common OnFailure default, we buffer only for Always or a
        // forced request — OnFailure applies to response bodies + headers, not request bodies.
        if (_options.CaptureRequestBodies == CaptureMode.Always || forceCapture)
        {
            context.Request.EnableBuffering();
        }

        var originalBody = context.Response.Body;
        CaptureBodyStream? capture = null;
        if (_options.CaptureResponseBodies != CaptureMode.None || forceCapture)
        {
            capture = new CaptureBodyStream(originalBody, _options.MaxCapturedBodySize);
            context.Response.Body = capture;
        }

        var start = _timeProvider.GetTimestamp();
        string? exceptionType = null;
        string? exceptionMessage = null;
        var clientAborted = false;

        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            // A client disconnect surfaces as a cancellation of the request-aborted token. That is not a
            // server error — don't record it (it would inflate the error rate and produce a StatusCode=200,
            // Outcome=Failed row for a request that never completed).
            clientAborted = ex is OperationCanceledException && context.RequestAborted.IsCancellationRequested;
            exceptionType = ex.GetType().FullName;
            exceptionMessage = HttpCaptureHelpers.TruncateToBytes(ex.Message, MaxExceptionMessage);

            throw;
        }
        finally
        {
            if (capture is not null)
            {
                context.Response.Body = originalBody;
            }

            if (!clientAborted)
            {
                await RecordAsync(context, identity, start, capture, forceCapture, exceptionType, exceptionMessage);
            }
        }
    }

    private async Task RecordAsync(
        HttpContext context,
        WarpEndpointIdentity identity,
        long start,
        CaptureBodyStream? capture,
        bool forceCapture,
        string? exceptionType,
        string? exceptionMessage)
    {
        try
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var durationMs = _timeProvider.GetElapsedTime(start).TotalMilliseconds;
            var statusCode = context.Response.StatusCode;
            var failed = exceptionType is not null || statusCode >= 500;
            var outcome = failed ? AdapterCallOutcome.Failed : AdapterCallOutcome.Success;

            // Always-on meters (independent of the recording Sink): an OTel-only user reconstructs
            // count / error-rate / latency (and per-app) from these even when no DB rows are written. Route is
            // the bounded {method} {template} identity; application is the process origin when set.
            WarpTelemetry.RecordEndpointCall($"{identity.Method} {identity.RouteTemplate}", Warp.Core.Metrics.WarpMetricCatalog.OutcomeToken(outcome), durationMs, _applicationName);

            // Capture tiers: full fidelity iff Always, OnFailure-and-failed, or forced. A forced request
            // captures bodies + headers even on success and even if the tier is None/OnFailure. Request
            // bodies are the exception — they are only captured for Always/force (see the buffering note in
            // InvokeAsync), never OnFailure, because that would require buffering every request up-front.
            var captureBodies = forceCapture || _options.CaptureResponseBodies == CaptureMode.Always || (failed && _options.CaptureResponseBodies == CaptureMode.OnFailure);
            var captureReq = forceCapture || _options.CaptureRequestBodies == CaptureMode.Always;
            var captureHeaders = forceCapture || _options.CaptureHeaders == CaptureMode.Always || (failed && _options.CaptureHeaders == CaptureMode.OnFailure);

            // Compute the captured detail once — both the span enrichment (Otel/Both) and the DB row
            // (Database/Both) draw from the same already-redacted/truncated values.
            var group = ResolveGroup(context);
            var remoteIp = ResolveRemoteIp(context);

            // Client session id from the incoming W3C baggage (ASP.NET extracts the baggage header into the
            // request Activity) — threads this request to the browser session that made it (§8.27).
            var session = NullIfEmpty(Activity.Current?.GetBaggageItem(WarpTelemetryAttributes.SessionId));
            var userAgent = NullIfEmpty(context.Request.Headers.UserAgent.ToString());
            var user = context.User.Identity?.Name;
            var requestHeaders = captureHeaders ? HttpCaptureHelpers.RedactHeaders(context.Request.Headers, _options.RedactedHeaders, _options.MaxCapturedHeaderSize) : null;
            var responseHeaders = captureHeaders ? HttpCaptureHelpers.RedactHeaders(context.Response.Headers, _options.RedactedHeaders, _options.MaxCapturedHeaderSize) : null;
            var requestBody = captureReq ? await ReadRequestBodyAsync(context) : null;
            var responseBody = captureBodies ? DecodeCaptured(capture) : null;

            // Otel/Both: attach the detail to the ambient request span, but only when it is being recorded
            // (sampled in), so the trace sampling decision governs the whole request coherently (§8.24). The
            // caller metadata (IP / user-agent / user) is captured-by-design PII riding the trace exporter
            // only — never the ILogger provider chain.
            if (_enrichSpan && Activity.Current is { IsAllDataRequested: true } activity)
            {
                EnrichSpan(activity, identity, statusCode, outcome, group, remoteIp, userAgent, user, requestHeaders, responseHeaders, requestBody, responseBody);
            }

            // session.id (OTel semantic convention) rides the request span whenever present — independent of
            // the recording sink, so any OTel backend can slice the whole trace by the browser session (§8.27).
            if (session is not null && Activity.Current is { IsAllDataRequested: true } sessionSpan)
            {
                sessionSpan.SetTag(WarpTelemetryAttributes.SessionId, session);
            }

            // Database/Both: hand the record to the DB recorder. Under Otel-only there is none — the span
            // enrichment above is the whole recording, so nothing more to do.
            if (_recorder is null)
            {
                return;
            }

            // A row is written for any failure, any forced request, and successes kept by both the record
            // mode and the sample rate; counters are ALWAYS written (never gated by sampling or suppression),
            // keeping the aggregates 100% exact.
#pragma warning disable CA5394 // Sampling is a volume knob, not a security decision — non-crypto RNG is fine.
            var sampledIn = _options.SampleRate >= 1.0 || Random.Shared.NextDouble() < _options.SampleRate;
#pragma warning restore CA5394

            var suppressLog = !failed
                && !forceCapture
                && (_options.RecordCalls == CallRecording.FailuresOnly || !sampledIn);

            var record = new EndpointCallRecord
            {
                Method = identity.Method,
                RouteTemplate = identity.RouteTemplate,
                Operation = identity.Operation,
                GroupName = group,
                Timestamp = now,
                DurationMs = durationMs,
                Outcome = outcome,
                StatusCode = statusCode,
                RemoteIp = remoteIp,
                Session = session,
                UserAgent = userAgent,
                User = user,
                ExceptionType = exceptionType,
                ExceptionMessage = exceptionMessage,
                RequestHeaders = requestHeaders,
                ResponseHeaders = responseHeaders,
                RequestBody = requestBody,
                ResponseBody = responseBody,
                MachineName = Environment.MachineName,
                TraceId = ResolveTraceId(),
                TagsJson = ResolveTags(context),
                ExpireAt = now.Add(_retention),
                SuppressLog = suppressLog,
            };

            // Lossy by design: a full channel drops the record (counted, never blocking or failing the
            // request), mirroring the outbound adapter recorder.
            if (!_recorder.Record(record))
            {
                WarpTelemetry.EndpointRecordsDropped.Add(1);
                DroppedRecordCounters.Track(DropPipeline.Endpoint, 1);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Recording is diagnostics, never a request failure — swallow anything the capture path throws.
        }
    }

    private static void EnrichSpan(
        Activity activity,
        WarpEndpointIdentity identity,
        int statusCode,
        AdapterCallOutcome outcome,
        string? group,
        string? remoteIp,
        string? userAgent,
        string? user,
        string? requestHeaders,
        string? responseHeaders,
        string? requestBody,
        string? responseBody)
    {
        activity.SetTag(WarpTelemetryAttributes.WarpEndpointRoute, $"{identity.Method} {identity.RouteTemplate}");
        activity.SetTag(WarpTelemetryAttributes.WarpEndpointStatusCode, statusCode);
        activity.SetTag(WarpTelemetryAttributes.WarpEndpointOutcome, Warp.Core.Metrics.WarpMetricCatalog.OutcomeToken(outcome));
        SetTagIfNotNull(activity, WarpTelemetryAttributes.WarpEndpointGroup, group);
        SetTagIfNotNull(activity, WarpTelemetryAttributes.WarpEndpointClientIp, remoteIp);
        SetTagIfNotNull(activity, WarpTelemetryAttributes.WarpEndpointUserAgent, userAgent);
        SetTagIfNotNull(activity, WarpTelemetryAttributes.WarpEndpointUser, user);
        SetTagIfNotNull(activity, WarpTelemetryAttributes.WarpEndpointRequestHeaders, requestHeaders);
        SetTagIfNotNull(activity, WarpTelemetryAttributes.WarpEndpointResponseHeaders, responseHeaders);
        SetTagIfNotNull(activity, WarpTelemetryAttributes.WarpEndpointRequestBody, requestBody);
        SetTagIfNotNull(activity, WarpTelemetryAttributes.WarpEndpointResponseBody, responseBody);
    }

    private static void SetTagIfNotNull(Activity activity, string key, string? value)
    {
        if (value is not null)
        {
            activity.SetTag(key, value);
        }
    }

    private string? ResolveGroup(HttpContext context)
    {
        var group = _options.GroupSelector?.Invoke(context);

        return NullIfEmpty(group);
    }

    private string? ResolveRemoteIp(HttpContext context)
    {
        if (_options.UseForwardedForIp
            && context.Request.Headers.TryGetValue("X-Forwarded-For", out var forwarded)
            && forwarded.Count > 0)
        {
            var first = forwarded.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (first.Length > 0)
            {
                return first[0];
            }
        }

        return context.Connection.RemoteIpAddress?.ToString();
    }

    private async Task<string?> ReadRequestBodyAsync(HttpContext context)
    {
        if (!context.Request.Body.CanSeek)
        {
            return null;
        }

        context.Request.Body.Position = 0;

        var max = _options.MaxCapturedBodySize;

        // Read one byte past the cap so an over-cap body is detectable — then decode only the first `max`
        // bytes on a UTF-8 boundary (a raw GetString over a mid-character cut surfaces U+FFFD).
        var buffer = new byte[max + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await context.Request.Body.ReadAsync(buffer.AsMemory(read, buffer.Length - read), CancellationToken.None);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        context.Request.Body.Position = 0;

        if (read == 0)
        {
            return null;
        }

        var truncated = read > max;

        return HttpCaptureHelpers.DecodePrefix(buffer, truncated ? max : read, truncated);
    }

    private static string? DecodeCaptured(CaptureBodyStream? capture)
    {
        if (capture is null)
        {
            return null;
        }

        var bytes = capture.CapturedBytes;

        return bytes.Length == 0 ? null : HttpCaptureHelpers.DecodePrefix(bytes, bytes.Length, capture.Truncated);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    // Store the trace id as a Guid built the SAME way jobs build theirs (new Guid over the 32-hex trace id),
    // so jobs spawned during this request join directly on Job.TraceId. Null when no Activity is flowing.
    private static Guid? ResolveTraceId()
    {
        var activity = Activity.Current;

        return activity is null ? null : new Guid(activity.TraceId.ToHexString());
    }

    private string? ResolveTags(HttpContext context)
    {
        if (_options.Enrich is null)
        {
            return null;
        }

        try
        {
            var tags = new Dictionary<string, string>(StringComparer.Ordinal);
            _options.Enrich(context, tags);

            return tags.Count == 0 ? null : JsonSerializer.Serialize(tags);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A throwing enricher costs its tags, not the whole record (recording never fails a request).
            return null;
        }
    }
}
