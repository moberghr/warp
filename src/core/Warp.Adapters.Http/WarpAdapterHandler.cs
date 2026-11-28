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
        RequestBodyCapture? requestCapture = null;
        try
        {
            // Request-body capture (a request-body tier or forced capture) copies a bounded prefix as the
            // transport writes the body; nothing is read ahead of the send. Set up INSIDE the outcome-owning
            // try so nothing here can unwind past the scope and let Dispose() default the call to Success.
            requestCapture = BeginRequestBodyCapture(request, forceCapture);

            try
            {
                response = await base.SendAsync(request, cancellationToken);
            }
            finally
            {
                requestCapture?.Restore(request);
            }
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
            await CaptureAsync(scope, request, throttled, requestCapture, isFailure: true, forceCapture, cancellationToken);
            CompleteOutcome(scope, throttled, isFailure: true);

            return throttled;
        }
        catch (Exception ex)
        {
            await CaptureAsync(scope, request, response: null, requestCapture, isFailure: true, forceCapture, cancellationToken);
            scope.Fail(ex);

            throw;
        }

        var isFailure = !response.IsSuccessStatusCode;
        await CaptureAsync(scope, request, response, requestCapture, isFailure, forceCapture, cancellationToken);
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
        RequestBodyCapture? requestCapture,
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

        if (requestCapture is not null && ShouldCapture(_recording.CaptureRequestBodies, isFailure, forceCapture))
        {
            var requestBody = await ReadRequestCaptureAsync(requestCapture);
            if (requestBody is not null)
            {
                scope.SetRequestBody(TruncateToBytes(requestBody, _recording.MaxCapturedBodySize));
            }
        }

        if (response?.Content is not null && ShouldCapture(_recording.CaptureResponseBodies, isFailure, forceCapture))
        {
            // Capture must be NON-DESTRUCTIVE and BOUNDED. A live response body is a forward-only, single-pass
            // stream, so raw-reading it here would consume it and the caller would then fail with "stream
            // already consumed". Buffering the whole content (LoadIntoBufferAsync) kept it readable but had no
            // limit: a huge or endless response (a vendor, or anyone the adapter can be pointed at) was read
            // into memory in full, and a streaming caller (ResponseHeadersRead) never got its response at all.
            // So read only cap + 1 bytes and hand the caller a content that replays them before the rest of
            // the stream (PeekResponseBodyAsync). Read with the CALLER's token; capture is best-effort and must
            // never rewrite the outcome, so on cancellation we complete the scope with the call's TRUE outcome
            // before the OCE propagates.
            try
            {
                var responseBody = await PeekResponseBodyAsync(response, _recording.MaxCapturedBodySize, cancellationToken);
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

    private static readonly HashSet<string> StreamingMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/event-stream",
        "application/x-ndjson",
        "application/ndjson",
        "application/jsonl",
        "application/x-jsonlines",
        "application/stream+json",
        "multipart/x-mixed-replace",
    };

    private bool AnyCaptureEnabled
        => _recording.CaptureHeaders != CaptureMode.None
        || _recording.CaptureRequestBodies != CaptureMode.None
        || _recording.CaptureResponseBodies != CaptureMode.None;

    private RequestBodyCapture? BeginRequestBodyCapture(HttpRequestMessage request, bool forceCapture)
    {
        if ((_recording.CaptureRequestBodies == CaptureMode.None && !forceCapture) || request.Content is null)
        {
            return null;
        }

        return RequestBodyCapture.Begin(request, Math.Max(0, _recording.MaxCapturedBodySize));
    }

    private static async Task<string?> ReadRequestCaptureAsync(RequestBodyCapture capture)
    {
        try
        {
            return await capture.ReadAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Capture is best-effort and must never break the call (e.g. a content the caller already disposed).
            return null;
        }
    }

    // Reads at most maxBytes + 1 bytes of the response body. A seekable body is rewound; a forward-only one is
    // replaced by a content that replays the bytes read and then streams the rest, with the original headers, so
    // the caller still receives the whole body and nothing beyond the prefix is ever buffered here. The swap
    // happens even when the read fails part-way, so the caller sees the transport's own error on its next read
    // rather than "stream already consumed". A streaming media type is not captured at all: the peek waits
    // for maxBytes + 1 bytes or the end, and a server-sent-events or line-delimited feed may send a few bytes a
    // minute for ever. Other long-poll endpoints need capture off (documented).
    private static async Task<string?> PeekResponseBodyAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        if (maxBytes <= 0)
        {
            return string.Empty;
        }

        var original = response.Content;
        if (IsStreamingMediaType(original.Headers.ContentType?.MediaType))
        {
            return null;
        }

        Stream stream;
        try
        {
            stream = await original.ReadAsStreamAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        var start = stream.CanSeek ? stream.Position : 0;
        var buffer = new byte[maxBytes + 1];
        var read = 0;
        try
        {
            read = await ReadPrefixAsync(stream, buffer, x => read = x, cancellationToken);

            return read == 0 ? string.Empty : Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Capture is best-effort and must never break the call.
            return null;
        }
        finally
        {
            if (stream.CanSeek)
            {
                stream.Position = start;
            }
            else
            {
                var replay = new StreamContent(new PrefixedReadStream(buffer, read, stream, original));
                foreach (var header in original.Headers)
                {
                    replay.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                response.Content = replay;
            }
        }
    }

    // Media types that are, by their nature, open-ended feeds rather than one document.
    private static bool IsStreamingMediaType(string? mediaType)
    {
        if (mediaType == null)
        {
            return false;
        }

        return StreamingMediaTypes.Contains(mediaType)
            || mediaType.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase);
    }

    // progress reports the count after every read, so a caller whose read throws part-way still knows how many
    // bytes of the buffer are real.
    private static async Task<int> ReadPrefixAsync(Stream stream, byte[] buffer, Action<int>? progress, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellationToken);
            if (n == 0)
            {
                break;
            }

            read += n;
            progress?.Invoke(read);
        }

        return read;
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
