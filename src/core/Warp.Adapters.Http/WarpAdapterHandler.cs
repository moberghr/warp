using System.Net.Http.Headers;
using System.Text;
using Warp.Core.Adapters;
using Warp.Core.Enums;

namespace Warp.Adapters.Http;

/// <summary>
/// Outermost <see cref="DelegatingHandler"/> for an adapter's <see cref="HttpClient"/>. Times the
/// logical call, resolves the operation/group/correlation, drives a single
/// <see cref="AdapterCallScope"/> (telemetry + one recorded row with the final outcome), and applies the
/// configured capture tiers with header redaction and byte truncation before storing anything (§1.2).
/// <para>
/// Sits <b>outside</b> the resilience/rate-limit handlers, so it observes one logical call regardless of
/// how many physical attempts the resilience pipeline makes. Captured payloads are redacted and
/// truncated here, then attached to the scope's dedicated capture columns via
/// <see cref="AdapterCallScope.SetRequestSummary"/> / <see cref="AdapterCallScope.SetStatusCode"/> /
/// <see cref="AdapterCallScope.SetRequestHeaders"/> / <see cref="AdapterCallScope.SetResponseHeaders"/> /
/// <see cref="AdapterCallScope.SetRequestBody"/> / <see cref="AdapterCallScope.SetResponseBody"/>; scope
/// tags stay reserved for user enrichment. The recorded attempt count is the logical call (1); per-attempt
/// latency remains visible via the resilience package's own OTel telemetry.
/// </para>
/// <para>
/// <b>Outcome mapping.</b> A thrown exception (connection failure, cancellation) records
/// <c>Failed</c> and rethrows. A response with a non-success status records <c>Failed</c> via the
/// idiomatic <see cref="HttpRequestException"/> (the same exception <c>EnsureSuccessStatusCode</c>
/// produces) but is <b>not</b> thrown — the caller receives the response unchanged, matching default
/// <see cref="HttpClient"/> behaviour. Both paths trigger <c>OnFailure</c> capture.
/// </para>
/// <para>
/// <b>Respond429.</b> A shared-limit refusal is thrown by the innermost rate-limit handler in every
/// overflow mode; under <c>AdapterRateLimitOverflow.Respond429</c> it is converted <b>here</b> — after
/// every user handler — into the synthetic 429 the caller receives, recorded <c>Throttled</c> like the
/// throwing modes. Converting it any deeper would expose Warp's self-throttle to the user's resilience
/// handler as a retryable status.
/// </para>
/// </summary>
internal sealed class WarpAdapterHandler : DelegatingHandler
{
    private const string TruncationMarker = "…";

    private readonly string _adapterName;
    private readonly WarpAdapterOptions _recording;
    private readonly int _maxDistinctOperations;
    private readonly bool _respondWith429;
    private readonly IWarpAdapters _adapters;
    private readonly OperationNameResolver _resolver;

    public WarpAdapterHandler(
        string adapterName,
        WarpAdapterHttpOptions options,
        IWarpAdapters adapters,
        OperationNameResolver resolver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterName);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(resolver);

        _adapterName = adapterName;
        _recording = options.Recording;
        _maxDistinctOperations = options.Recording.MaxDistinctOperations;
        _respondWith429 = options.SharedRateLimit?.Overflow == AdapterRateLimitOverflow.Respond429;
        _adapters = adapters;
        _resolver = resolver;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var operation = _resolver.Resolve(_adapterName, request, _maxDistinctOperations);
        var group = OperationNameResolver.ResolveGroup(request);

        using var scope = _adapters.BeginCall(_adapterName, operation, group);

        // Force-capture (request option OR ambient scope): writes the log row regardless of SampleRate /
        // RecordCalls and captures full-fidelity payloads even on success / with a None tier. Set on the
        // scope before completion so its SuppressLog decision honours it.
        var forceCapture = request.GetWarpForceCapture() || WarpAdapterCall.CurrentForceCapture;
        scope.SetForceCapture(forceCapture);

        var correlation = request.GetWarpCorrelation();
        if (correlation is not null)
        {
            scope.SetCorrelation(correlation);
        }

        HttpResponseMessage response;
        string? requestBody = null;
        try
        {
            // Buffer the request body up front (only when a request-body tier is active or capture is
            // forced): the transport consumes the content, so it cannot be read after the send. Buffering
            // lives INSIDE the outcome-owning try — a cancellation here must record Failed, not unwind past
            // the scope and let Dispose() default the never-sent call to Success.
            requestBody = await BufferRequestBodyAsync(request, forceCapture, cancellationToken);

            response = await base.SendAsync(request, cancellationToken);
        }
        catch (AdapterRateLimitedException ex) when (_respondWith429)
        {
            // Answer the call instead of rethrowing: a client that classifies by status (Refit, which wraps
            // pipeline exceptions in ApiRequestException, and does not throw at all for ApiResponse<T>) then
            // sees the refusal on its normal path. No request was sent.
            //
            // The conversion belongs HERE, outside every user handler, not at the rate-limit handler that
            // raised it: a 429 born innermost would be an ordinary retryable status to the resilience
            // handler nested between us, which would retry Warp's own self-throttle straight back into the
            // limiter and sample it into its circuit breaker. Thrown, it passes those handlers untouched
            // (it is not an HttpRequestException), so Respond429 behaves exactly like Wait in the pipeline.
            var throttled = WarpThrottledResponse.Create(request, ex);
            await CaptureAsync(scope, request, throttled, requestBody, isFailure: true, forceCapture, cancellationToken);
            CompleteOutcome(scope, throttled, isFailure: true);

            return throttled;
        }
        catch (Exception ex)
        {
            await CaptureAsync(scope, request, response: null, requestBody, isFailure: true, forceCapture, cancellationToken);
            scope.Fail(ex);

            throw;
        }

        var isFailure = !response.IsSuccessStatusCode;
        await CaptureAsync(scope, request, response, requestBody, isFailure, forceCapture, cancellationToken);
        CompleteOutcome(scope, response, isFailure);

        return response;
    }

    // Completion is idempotent (first of Succeed/Fail/Dispose wins), so calling this from the OCE-desync
    // guard in CaptureAsync and again here is safe — the second call is a no-op.
    private static void CompleteOutcome(AdapterCallScope scope, HttpResponseMessage response, bool isFailure)
    {
        // Warp's own shared-limit refusal (AdapterRateLimitOverflow.Respond429) arrives as a synthetic 429.
        // It is a non-success status, so the generic branch below would record it Failed — complete it with
        // the refusal itself instead, which the scope maps to Throttled exactly as the throwing modes do.
        // A REAL vendor 429 is deliberately left on the Failed branch: its classification is unchanged.
        if (response is WarpThrottledResponse throttled)
        {
            scope.Fail(throttled.Refusal);

            return;
        }

        if (isFailure)
        {
            scope.Fail(new HttpRequestException(
                $"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}).",
                inner: null,
                statusCode: response.StatusCode));

            return;
        }

        scope.Succeed();
    }

    internal static bool ShouldCapture(CaptureMode mode, bool isFailure, bool forceCapture) => forceCapture || mode switch
    {
        CaptureMode.Always => true,
        CaptureMode.OnFailure => isFailure,
        _ => false,
    };

    internal static string RedactHeaders(
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers,
        ISet<string> redacted,
        int maxBytes)
    {
        var builder = new StringBuilder();
        foreach (var header in headers)
        {
            var value = redacted.Contains(header.Key) ? "***" : string.Join(", ", header.Value);
            builder.Append(header.Key).Append(": ").Append(value).Append('\n');
        }

        return TruncateToBytes(builder.ToString().TrimEnd('\n'), maxBytes);
    }

    internal static string TruncateToBytes(string value, int maxBytes)
    {
        if (maxBytes <= 0 || string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount <= maxBytes)
        {
            return value;
        }

        var markerBytes = Encoding.UTF8.GetByteCount(TruncationMarker);
        var budget = Math.Max(0, maxBytes - markerBytes);
        var buffer = Encoding.UTF8.GetBytes(value);
        var boundary = SafeBoundary(buffer, Math.Min(budget, buffer.Length));

        return Encoding.UTF8.GetString(buffer, 0, boundary) + TruncationMarker;
    }

    private static int SafeBoundary(byte[] buffer, int limit)
    {
        var boundary = limit;

        // Walk back off any UTF-8 continuation byte (0b10xxxxxx) so we never split a multi-byte char.
        while (boundary > 0 && (buffer[boundary] & 0xC0) == 0x80)
        {
            boundary--;
        }

        return boundary;
    }

    private async Task CaptureAsync(
        AdapterCallScope scope,
        HttpRequestMessage request,
        HttpResponseMessage? response,
        string? requestBody,
        bool isFailure,
        bool forceCapture,
        CancellationToken cancellationToken)
    {
        // Status code is always-recorded metadata (§8.19), not a capture column — set it regardless of the
        // capture mode so an observe-first adapter (no payload capture) can still tell a 404 from a 500.
        if (response is not null)
        {
            scope.SetStatusCode((int)response.StatusCode);
        }

        if (!AnyCaptureEnabled && !forceCapture)
        {
            return;
        }

        scope.SetRequestSummary($"{request.Method.Method} {request.RequestUri?.GetLeftPart(UriPartial.Path)}");

        if (ShouldCapture(_recording.CaptureHeaders, isFailure, forceCapture))
        {
            scope.SetRequestHeaders(RedactHeaders(AllHeaders(request.Headers, request.Content?.Headers), _recording.RedactedHeaders, _recording.MaxCapturedHeaderSize));

            if (response is not null)
            {
                scope.SetResponseHeaders(RedactHeaders(AllHeaders(response.Headers, response.Content?.Headers), _recording.RedactedHeaders, _recording.MaxCapturedHeaderSize));
            }
        }

        if (requestBody is not null && ShouldCapture(_recording.CaptureRequestBodies, isFailure, forceCapture))
        {
            scope.SetRequestBody(TruncateToBytes(requestBody, _recording.MaxCapturedBodySize));
        }

        if (response?.Content is not null && ShouldCapture(_recording.CaptureResponseBodies, isFailure, forceCapture))
        {
            // Capture must be NON-DESTRUCTIVE: a live response body is a forward-only, single-pass stream, so
            // raw-reading it here would consume it and the caller (and HttpClient's default content buffering)
            // would then fail with "stream already consumed" — turning a successful call into a failure. So we
            // buffer the content first (LoadIntoBufferAsync, via ReadBufferedAsync) — leaving it fully readable
            // by the caller — and store only a truncated prefix. Buffering matches HttpClient's default
            // ResponseContentRead completion, so it adds no cost on the common path. Read with the CALLER's
            // token; capture is best-effort and must never rewrite the outcome, so on cancellation we complete
            // the scope with the call's TRUE outcome before the OCE propagates.
            try
            {
                var responseBody = await ReadBufferedAsync(response.Content, _recording.MaxCapturedBodySize, cancellationToken);
                if (responseBody is not null)
                {
                    scope.SetResponseBody(TruncateToBytes(responseBody, _recording.MaxCapturedBodySize));
                }
            }
            catch (OperationCanceledException)
            {
                CompleteOutcome(scope, response, isFailure);

                throw;
            }
        }
    }

    private bool AnyCaptureEnabled
        => _recording.CaptureHeaders != CaptureMode.None
        || _recording.CaptureRequestBodies != CaptureMode.None
        || _recording.CaptureResponseBodies != CaptureMode.None;

    private async Task<string?> BufferRequestBodyAsync(HttpRequestMessage request, bool forceCapture, CancellationToken cancellationToken)
    {
        if ((_recording.CaptureRequestBodies == CaptureMode.None && !forceCapture) || request.Content is null)
        {
            return null;
        }

        return await ReadBufferedAsync(request.Content, _recording.MaxCapturedBodySize, cancellationToken);
    }

    private static async Task<string?> ReadBufferedAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        try
        {
            // Buffer so the content stays readable for the transport (request) or the caller (response).
            await content.LoadIntoBufferAsync(cancellationToken);

            if (maxBytes <= 0)
            {
                return string.Empty;
            }

            await using var stream = await content.ReadAsStreamAsync(cancellationToken);
            if (stream.CanSeek)
            {
                stream.Seek(0, SeekOrigin.Begin);
            }

            // Read only a bounded prefix rather than materialising the whole body into a string just to
            // truncate it to maxBytes elsewhere — a hot adapter returning a large payload would otherwise
            // allocate the full body (on top of the already-buffered bytes) on EVERY captured call. Read one
            // extra byte so an over-cap body still trips the caller's TruncateToBytes marker; TruncateToBytes
            // does the boundary-safe cut, so a raw decode here is fine (it re-encodes and trims).
            var buffer = new byte[maxBytes + 1];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellationToken);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            return read == 0 ? string.Empty : Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Capture is best-effort and must never break the call (e.g. a non-buffering stream content).
            return null;
        }
    }

    private static IEnumerable<KeyValuePair<string, IEnumerable<string>>> AllHeaders(HttpHeaders primary, HttpHeaders? content)
    {
        foreach (var header in primary)
        {
            yield return header;
        }

        if (content is null)
        {
            yield break;
        }

        foreach (var header in content)
        {
            yield return header;
        }
    }
}
