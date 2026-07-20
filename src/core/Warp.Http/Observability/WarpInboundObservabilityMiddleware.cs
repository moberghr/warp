using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Endpoints;
using Warp.Core.Enums;

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
    private readonly IEndpointCallRecorder _recorder;
    private readonly WarpEndpointObservabilityOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _retention;

    public WarpInboundObservabilityMiddleware(
        RequestDelegate next,
        IEndpointCallRecorder recorder,
        IOptions<WarpEndpointObservabilityOptions> options,
        IOptions<WarpConfiguration> configuration,
        TimeProvider timeProvider)
    {
        _next = next;
        _recorder = recorder;
        _options = options.Value;
        _timeProvider = timeProvider;
        _retention = configuration.Value.EndpointCallLogRetention;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var identity = context.GetEndpoint()?.Metadata.GetMetadata<WarpEndpointIdentity>();
        if (identity is null)
        {
            await _next(context);

            return;
        }

        if (_options.CaptureRequestBodies != CaptureMode.None)
        {
            // Buffer the body before model binding consumes it, so it can be re-read after the handler runs.
            context.Request.EnableBuffering();
        }

        var originalBody = context.Response.Body;
        CaptureBodyStream? capture = null;
        if (_options.CaptureResponseBodies != CaptureMode.None)
        {
            capture = new CaptureBodyStream(originalBody, _options.MaxCapturedBodySize);
            context.Response.Body = capture;
        }

        var start = _timeProvider.GetTimestamp();
        string? exceptionType = null;
        string? exceptionMessage = null;

        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
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

            await RecordAsync(context, identity, start, capture, exceptionType, exceptionMessage);
        }
    }

    private async Task RecordAsync(
        HttpContext context,
        WarpEndpointIdentity identity,
        long start,
        CaptureBodyStream? capture,
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

            var captureBodies = _options.CaptureResponseBodies == CaptureMode.Always || (failed && _options.CaptureResponseBodies == CaptureMode.OnFailure);
            var captureReq = _options.CaptureRequestBodies == CaptureMode.Always || (failed && _options.CaptureRequestBodies == CaptureMode.OnFailure);
            var captureHeaders = _options.CaptureHeaders == CaptureMode.Always || (failed && _options.CaptureHeaders == CaptureMode.OnFailure);

            var record = new EndpointCallRecord
            {
                Method = identity.Method,
                RouteTemplate = identity.RouteTemplate,
                Operation = identity.Operation,
                GroupName = ResolveGroup(context),
                Timestamp = now,
                DurationMs = durationMs,
                Outcome = outcome,
                StatusCode = statusCode,
                RemoteIp = ResolveRemoteIp(context),
                UserAgent = NullIfEmpty(context.Request.Headers.UserAgent.ToString()),
                User = context.User.Identity?.Name,
                ExceptionType = exceptionType,
                ExceptionMessage = exceptionMessage,
                RequestHeaders = captureHeaders ? HttpCaptureHelpers.RedactHeaders(context.Request.Headers, _options.RedactedHeaders, _options.MaxCapturedHeaderSize) : null,
                ResponseHeaders = captureHeaders ? HttpCaptureHelpers.RedactHeaders(context.Response.Headers, _options.RedactedHeaders, _options.MaxCapturedHeaderSize) : null,
                RequestBody = captureReq ? await ReadRequestBodyAsync(context) : null,
                ResponseBody = captureBodies ? DecodeCaptured(capture) : null,
                MachineName = Environment.MachineName,
                TraceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
                ExpireAt = now.Add(_retention),
                SuppressLog = _options.RecordCalls == CallRecording.FailuresOnly && !failed,
            };

            _recorder.Record(record);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Recording is diagnostics, never a request failure — swallow anything the capture path throws.
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
        var buffer = new byte[max];
        var read = 0;
        while (read < max)
        {
            var n = await context.Request.Body.ReadAsync(buffer.AsMemory(read, max - read), CancellationToken.None);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        context.Request.Body.Position = 0;

        return read == 0 ? null : Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static string? DecodeCaptured(CaptureBodyStream? capture)
    {
        if (capture is null)
        {
            return null;
        }

        var bytes = capture.CapturedBytes;

        return bytes.Length == 0 ? null : Encoding.UTF8.GetString(bytes);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
