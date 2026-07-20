using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Warp.Adapters.Http;
using Warp.Core.Adapters;
using Warp.Core.Enums;

namespace Warp.Tests.Adapters;

/// <summary>
/// Handler-level capture coverage (SC3): capture-tier gating (None / OnFailure / Always) against the
/// call outcome, header redaction (including a user-removed / user-cleared denylist), byte truncation,
/// and absolute-URI (no <c>BaseUrl</c>) recording (SC14). Drives the real <c>WarpAdapterHandler</c>
/// through a stub <see cref="HttpMessageHandler"/> — no live network — and inspects the recorded call.
/// </summary>
[Trait("Category", "NoDb")]
public class CaptureRedactionTests
{
    [TimedFact]
    public async Task CaptureNone_Success_WritesNoCaptureColumns()
    {
        var (adapters, recorder) = Harness();
        var options = new WarpAdapterHttpOptions();

        await SendAsync(adapters, options, Get("https://api.vendor.com/orders"), Ok("pong"));

        var record = recorder.Records.ShouldHaveSingleItem();
        record.RequestSummary.ShouldBeNull();
        record.StatusCode.ShouldBeNull();
        record.RequestHeaders.ShouldBeNull();
        record.ResponseHeaders.ShouldBeNull();
        record.RequestBody.ShouldBeNull();
        record.ResponseBody.ShouldBeNull();
        record.Tags.ShouldBeNull();
    }

    [TimedFact]
    public async Task CaptureAlways_Success_CapturesResponseBodyAndStatus()
    {
        var (adapters, recorder) = Harness();
        var options = new WarpAdapterHttpOptions();
        options.Recording.CaptureResponseBodies = CaptureMode.Always;

        await SendAsync(adapters, options, Get("https://api.vendor.com/ping"), Ok("pong"));

        var record = recorder.Records.ShouldHaveSingleItem();
        record.ResponseBody.ShouldBe("pong");
        record.StatusCode.ShouldBe(200);
        record.RequestSummary.ShouldBe("GET https://api.vendor.com/ping");
    }

    [TimedFact]
    public async Task CaptureOnFailure_Success_DoesNotCaptureBody()
    {
        var (adapters, recorder) = Harness();
        var options = new WarpAdapterHttpOptions();
        options.Recording.CaptureResponseBodies = CaptureMode.OnFailure;

        await SendAsync(adapters, options, Get("https://api.vendor.com/ping"), Ok("pong"));

        recorder.Records.ShouldHaveSingleItem().ResponseBody.ShouldBeNull();
    }

    [TimedFact]
    public async Task CaptureOnFailure_ErrorStatus_CapturesBodyAndRecordsFailed()
    {
        var (adapters, recorder) = Harness();
        var options = new WarpAdapterHttpOptions();
        options.Recording.CaptureResponseBodies = CaptureMode.OnFailure;

        await SendAsync(adapters, options, Get("https://api.vendor.com/ping"), Status(System.Net.HttpStatusCode.InternalServerError, "boom"));

        var record = recorder.Records.ShouldHaveSingleItem();
        record.Outcome.ShouldBe(AdapterCallOutcome.Failed);
        record.ResponseBody.ShouldBe("boom");
        record.StatusCode.ShouldBe(500);
    }

    [TimedFact]
    public async Task CaptureHeaders_Always_RedactsDenylistedHeader()
    {
        var (adapters, recorder) = Harness();
        var options = new WarpAdapterHttpOptions();
        options.Recording.CaptureHeaders = CaptureMode.Always;

        var request = Get("https://api.vendor.com/ping");
        request.Headers.Add("Authorization", "Bearer super-secret");

        await SendAsync(adapters, options, request, Ok("pong"));

        var headers = recorder.Records.ShouldHaveSingleItem().RequestHeaders;
        headers.ShouldNotBeNull();
        headers.ShouldContain("Authorization: ***");
        headers.ShouldNotContain("super-secret");
    }

    [TimedFact]
    public async Task CaptureHeaders_HeaderRemovedFromDenylist_NotRedacted()
    {
        var (adapters, recorder) = Harness();
        var options = new WarpAdapterHttpOptions();
        options.Recording.CaptureHeaders = CaptureMode.Always;
        options.Recording.RedactedHeaders.Remove("Authorization");

        var request = Get("https://api.vendor.com/ping");
        request.Headers.Add("Authorization", "Bearer visible-token");

        await SendAsync(adapters, options, request, Ok("pong"));

        var headers = recorder.Records.ShouldHaveSingleItem().RequestHeaders;
        headers.ShouldNotBeNull();
        headers.ShouldContain("visible-token");
    }

    [TimedFact]
    public async Task CaptureHeaders_CustomHeaderAddedToDenylist_Redacted()
    {
        // A caller-ADDED header name (not one of the built-in defaults) must be redacted once it is on the
        // denylist — proves the denylist is fully user-owned on the add side, not just remove/clear (§1.2).
        var (adapters, recorder) = Harness();
        var options = new WarpAdapterHttpOptions();
        options.Recording.CaptureHeaders = CaptureMode.Always;
        options.Recording.RedactedHeaders.Add("X-Custom-Secret");

        var request = Get("https://api.vendor.com/ping");
        request.Headers.Add("X-Custom-Secret", "hunter2-token");

        await SendAsync(adapters, options, request, Ok("pong"));

        var headers = recorder.Records.ShouldHaveSingleItem().RequestHeaders;
        headers.ShouldNotBeNull();
        headers.ShouldContain("X-Custom-Secret: ***");
        headers.ShouldNotContain("hunter2-token");
    }

    [TimedFact]
    public async Task CaptureHeaders_CombinedExceedsHeaderCap_Truncated()
    {
        // Combined redacted header text longer than MaxCapturedHeaderSize is truncated to the byte cap with
        // the marker — proves the header-size cap (distinct from the body cap) is honoured end-to-end.
        var (adapters, recorder) = Harness();
        var options = new WarpAdapterHttpOptions();
        options.Recording.CaptureHeaders = CaptureMode.Always;
        options.Recording.MaxCapturedHeaderSize = 16;

        var request = Get("https://api.vendor.com/ping");
        request.Headers.Add("Authorization", "Bearer super-secret-value");
        request.Headers.Add("X-Trace", "abcdefghijklmnop");

        await SendAsync(adapters, options, request, Ok("pong"));

        var headers = recorder.Records.ShouldHaveSingleItem().RequestHeaders;
        headers.ShouldNotBeNull();
        Encoding.UTF8.GetByteCount(headers).ShouldBeLessThanOrEqualTo(16);
        headers.ShouldEndWith("…");
    }

    [TimedFact]
    public async Task CaptureHeaders_DenylistCleared_NothingRedacted()
    {
        var (adapters, recorder) = Harness();
        var options = new WarpAdapterHttpOptions();
        options.Recording.CaptureHeaders = CaptureMode.Always;
        options.Recording.RedactedHeaders.Clear();

        var request = Get("https://api.vendor.com/ping");
        request.Headers.Add("Authorization", "Bearer plain");

        await SendAsync(adapters, options, request, Ok("pong"));

        var headers = recorder.Records.ShouldHaveSingleItem().RequestHeaders;
        headers.ShouldNotBeNull();
        headers.ShouldContain("plain");
        headers.ShouldNotContain("***");
    }

    [TimedFact]
    public async Task Capture_ResponseBodyExceedsCap_Truncated()
    {
        var (adapters, recorder) = Harness();
        var options = new WarpAdapterHttpOptions();
        options.Recording.CaptureResponseBodies = CaptureMode.Always;
        options.Recording.MaxCapturedBodySize = 5;

        await SendAsync(adapters, options, Get("https://api.vendor.com/ping"), Ok("hello world, this is long"));

        var body = recorder.Records.ShouldHaveSingleItem().ResponseBody;
        body.ShouldNotBeNull();
        Encoding.UTF8.GetByteCount(body).ShouldBeLessThanOrEqualTo(5);
        body.ShouldEndWith("…");
    }

    [TimedFact]
    public async Task Capture_MultibyteBodyExceedsCap_TruncatesOnCharBoundary_ValidUtf8()
    {
        var (adapters, recorder) = Harness();
        var options = new WarpAdapterHttpOptions();
        options.Recording.CaptureResponseBodies = CaptureMode.Always;
        options.Recording.MaxCapturedBodySize = 8;

        // Ten 'é' (2 UTF-8 bytes each). The 5-byte content budget (8 cap − 3-byte '…' marker) lands mid
        // character, so SafeBoundary must walk back off the continuation byte instead of splitting it.
        await SendAsync(adapters, options, Get("https://api.vendor.com/ping"), Ok(new string('é', 10)));

        var body = recorder.Records.ShouldHaveSingleItem().ResponseBody;
        body.ShouldNotBeNull();
        body.ShouldEndWith("…");

        // Walk-back may leave the captured value shorter than the cap; it must never exceed it.
        Encoding.UTF8.GetByteCount(body).ShouldBeLessThanOrEqualTo(8);

        // A mid-character cut would surface U+FFFD when the bytes are decoded — the boundary walk prevents it.
        body.ShouldNotContain("�");
        body.ShouldBe("éé…");
    }

    [TimedFact]
    public async Task ExceptionThrown_RecordsFailed_AndCapturesRequestBody()
    {
        var (adapters, recorder) = Harness();
        var options = new WarpAdapterHttpOptions();
        options.Recording.CaptureRequestBodies = CaptureMode.OnFailure;

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.vendor.com/submit")
        {
            Content = new StringContent("payload-body"),
        };

        await Should.ThrowAsync<HttpRequestException>(async () =>
        {
            var handler = new WarpAdapterHandler("vendor", options, adapters, Resolver()) { InnerHandler = new ThrowingHandler() };
            using var invoker = new HttpMessageInvoker(handler);
            await invoker.SendAsync(request, Xunit.TestContext.Current.CancellationToken);
        });

        var record = recorder.Records.ShouldHaveSingleItem();
        record.Outcome.ShouldBe(AdapterCallOutcome.Failed);
        record.RequestBody.ShouldBe("payload-body");
    }

    [TimedFact]
    public async Task NoBaseUrl_AbsoluteUri_RecordsCallWithHeuristicOperation()
    {
        var (adapters, recorder) = Harness();
        var options = new WarpAdapterHttpOptions();

        await SendAsync(adapters, options, Get("https://per-tenant.example.com/orders/42"), Ok("ok"));

        var record = recorder.Records.ShouldHaveSingleItem();
        record.AdapterName.ShouldBe("vendor");
        record.Operation.ShouldBe("GET /orders/{id}");
        record.Outcome.ShouldBe(AdapterCallOutcome.Success);
    }

    [TimedFact]
    public async Task ResponseCapture_CallerTokenCancelled_CompletesScopeWithTrueOutcome_ThenRethrows()
    {
        // Transport succeeds, then the caller's already-cancelled token is honoured while reading the
        // response body (so a slow/streaming body cannot hang past the caller's cancellation). Capture is
        // best-effort and must not desync the outcome: the scope completes with the call's TRUE outcome
        // (Success) before the OCE propagates to the caller (F5 intent, without the hang).
        var (adapters, recorder) = Harness();
        var options = new WarpAdapterHttpOptions();
        options.Recording.CaptureResponseBodies = CaptureMode.Always;

        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(new CancelObservingStream("ok")) };
        var handler = new WarpAdapterHandler("vendor", options, adapters, Resolver()) { InnerHandler = new StubHandler(response) };
        using var invoker = new HttpMessageInvoker(handler);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await invoker.SendAsync(Get("https://api.vendor.com/ping"), cts.Token));

        recorder.Records.ShouldHaveSingleItem().Outcome.ShouldBe(AdapterCallOutcome.Success);
    }

    [TimedFact]
    public async Task ResponseCapture_CallerTokenCancelled_FailedResponse_CompletesScopeWithFailedOutcome_ThenRethrows()
    {
        // Companion to the 200→Success case: on a 500 the cancelled-token capture must still record the call's
        // TRUE outcome (Failed) before the OCE propagates. A hardcoded isFailure:false in the OCE-desync guard
        // would record Success here and fail this test.
        var (adapters, recorder) = Harness();
        var options = new WarpAdapterHttpOptions();
        options.Recording.CaptureResponseBodies = CaptureMode.Always;

        var response = new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError) { Content = new StreamContent(new CancelObservingStream("boom")) };
        var handler = new WarpAdapterHandler("vendor", options, adapters, Resolver()) { InnerHandler = new StubHandler(response) };
        using var invoker = new HttpMessageInvoker(handler);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await invoker.SendAsync(Get("https://api.vendor.com/ping"), cts.Token));

        recorder.Records.ShouldHaveSingleItem().Outcome.ShouldBe(AdapterCallOutcome.Failed);
    }

    [TimedFact]
    public async Task RequestBodyBuffering_CancelledDuringBuffering_RecordsFailedOutcome_ThenRethrows()
    {
        // Cancellation while buffering the request body (before the transport is ever reached) must record
        // the call as Failed — not let the OCE unwind past the scope so Dispose() defaults it to Success.
        // A call that never executed must never be dashboarded as a successful one.
        var (adapters, recorder) = Harness();
        var options = new WarpAdapterHttpOptions();
        options.Recording.CaptureRequestBodies = CaptureMode.Always;

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.vendor.com/orders")
        {
            Content = new CancelledDuringReadContent(),
        };
        var handler = new WarpAdapterHandler("vendor", options, adapters, Resolver()) { InnerHandler = new StubHandler(Ok("never-reached")) };
        using var invoker = new HttpMessageInvoker(handler);

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await invoker.SendAsync(request, Xunit.TestContext.Current.CancellationToken));

        var record = recorder.Records.ShouldHaveSingleItem();
        record.Outcome.ShouldBe(AdapterCallOutcome.Failed);
        record.ExceptionType.ShouldNotBeNull();
    }

    [TimedFact]
    public async Task Transport_TaskCanceledDuringSend_RecordsFailedOutcome_ThenRethrows()
    {
        // HttpClient.Timeout expiry and caller cancellation both surface as TaskCanceledException from the
        // send itself — a different code path from the tested response-body-read cancellation. The generic
        // catch must record Failed and rethrow, same as any transport failure.
        var (adapters, recorder) = Harness();
        var options = new WarpAdapterHttpOptions();

        var handler = new WarpAdapterHandler("vendor", options, adapters, Resolver()) { InnerHandler = new TaskCanceledHandler() };
        using var invoker = new HttpMessageInvoker(handler);

        await Should.ThrowAsync<TaskCanceledException>(async () =>
            await invoker.SendAsync(Get("https://api.vendor.com/ping"), Xunit.TestContext.Current.CancellationToken));

        var record = recorder.Records.ShouldHaveSingleItem();
        record.Outcome.ShouldBe(AdapterCallOutcome.Failed);
        record.ExceptionType.ShouldBe(typeof(TaskCanceledException).FullName);
    }

    [TimedFact]
    public async Task ResponseCapture_SingleConsumptionResponse_DoesNotConsumeCallersBody()
    {
        // A real network response (HttpConnectionResponseContent) is a forward-only, single-pass stream —
        // reading it destructively consumes it. Response capture MUST buffer, not raw-read: if it consumed
        // the stream the caller (and HttpClient's default content buffering) would fail with "stream already
        // consumed" and a successful call would be seen as a failure. This is the exact bug the webhook demo
        // surfaced (every 204 delivery exhausting). Model it with a non-seekable one-shot stream.
        var (adapters, recorder) = Harness();
        var options = new WarpAdapterHttpOptions();
        options.Recording.CaptureResponseBodies = CaptureMode.Always;

        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StreamContent(new OneShotStream("hello-from-the-wire")),
        };
        var handler = new WarpAdapterHandler("vendor", options, adapters, Resolver()) { InnerHandler = new StubHandler(response) };
        using var invoker = new HttpMessageInvoker(handler);

        using var result = await invoker.SendAsync(Get("https://api.vendor.com/ping"), Xunit.TestContext.Current.CancellationToken);

        // The caller can still read the full body — capture buffered it rather than consuming the stream.
        var callerBody = await result.Content.ReadAsStringAsync(Xunit.TestContext.Current.CancellationToken);
        callerBody.ShouldBe("hello-from-the-wire");

        // And it was captured.
        recorder.Records.ShouldHaveSingleItem().ResponseBody.ShouldBe("hello-from-the-wire");
    }

    [TimedFact]
    public async Task ResponseCapture_LargeBody_TruncatesStoredPrefixToCap_CallerReadsFull()
    {
        // Capture stores only up to the cap (truncated with the marker) while the caller still reads the
        // whole body — the cap bounds what is PERSISTED, not what the caller receives.
        var (adapters, recorder) = Harness();
        var options = new WarpAdapterHttpOptions();
        options.Recording.CaptureResponseBodies = CaptureMode.Always;
        options.Recording.MaxCapturedBodySize = 16;

        var full = new string('x', 100_000);
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(full) };
        var handler = new WarpAdapterHandler("vendor", options, adapters, Resolver()) { InnerHandler = new StubHandler(response) };
        using var invoker = new HttpMessageInvoker(handler);

        using var result = await invoker.SendAsync(Get("https://api.vendor.com/ping"), Xunit.TestContext.Current.CancellationToken);

        var stored = recorder.Records.ShouldHaveSingleItem().ResponseBody;
        stored.ShouldNotBeNull();
        Encoding.UTF8.GetByteCount(stored).ShouldBeLessThanOrEqualTo(16);
        stored.ShouldEndWith("…");

        var callerBody = await result.Content.ReadAsStringAsync(Xunit.TestContext.Current.CancellationToken);
        callerBody.Length.ShouldBe(100_000);
    }

    [TimedFact]
    public void RedactHeaders_MasksDenylistedValue_KeepsOthers()
    {
        var redacted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Authorization" };
        var headers = new[]
        {
            new KeyValuePair<string, IEnumerable<string>>("Authorization", ["Bearer secret"]),
            new KeyValuePair<string, IEnumerable<string>>("Accept", ["application/json"]),
        };

        var result = WarpAdapterHandler.RedactHeaders(headers, redacted, 4096);

        result.ShouldContain("Authorization: ***");
        result.ShouldContain("Accept: application/json");
    }

    [TimedFact]
    public void TruncateToBytes_UnderCap_ReturnsInputUnchanged()
        => WarpAdapterHandler.TruncateToBytes("short", 4096).ShouldBe("short");

    // Off-by-one guard on the boundary: "short" is exactly 5 UTF-8 bytes, so byteCount == maxBytes must
    // return the value verbatim (the truncation branch is byteCount > cap, never ==).
    [TimedFact]
    public void TruncateToBytes_ByteCountEqualsCap_ReturnsInputUnchanged()
        => WarpAdapterHandler.TruncateToBytes("short", 5).ShouldBe("short");

    [TimedFact]
    public async Task Correlation_RequestOption_FlowsToRecordedRow()
    {
        // WithWarpCorrelation's own-package proof (previously only covered indirectly through the Webhooks
        // DB tests): the option must reach the recorded row through the plain handler pipeline.
        var (adapters, recorder) = Harness();
        var options = new WarpAdapterHttpOptions();

        var request = Get("https://api.vendor.com/ping");
        request.WithWarpCorrelation("delivery-42");

        await SendAsync(adapters, options, request, Ok("pong"));

        recorder.Records.ShouldHaveSingleItem().CorrelationId.ShouldBe("delivery-42");
    }

    [TimedFact]
    public async Task Group_RequestOption_FlowsToRecordedRow()
    {
        var (adapters, recorder) = Harness();
        var options = new WarpAdapterHttpOptions();

        var request = Get("https://api.vendor.com/ping");
        request.WithWarpGroup("opt-group");

        await SendAsync(adapters, options, request, Ok("pong"));

        recorder.Records.ShouldHaveSingleItem().GroupName.ShouldBe("opt-group");
    }

    [TimedFact]
    public async Task Group_AmbientScope_FlowsToRecordedRow()
    {
        var (adapters, recorder) = Harness();
        var options = new WarpAdapterHttpOptions();

        using (WarpAdapterCall.Group("ambient-group"))
        {
            await SendAsync(adapters, options, Get("https://api.vendor.com/ping"), Ok("pong"));
        }

        recorder.Records.ShouldHaveSingleItem().GroupName.ShouldBe("ambient-group");
    }

    [TimedFact]
    public async Task Group_RequestOption_BeatsAmbientScope()
    {
        // Precedence: an explicit WithWarpGroup on the request wins over the ambient WarpAdapterCall.Group
        // scope that is open around the same send.
        var (adapters, recorder) = Harness();
        var options = new WarpAdapterHttpOptions();

        var request = Get("https://api.vendor.com/ping");
        request.WithWarpGroup("opt-group");

        using (WarpAdapterCall.Group("ambient-group"))
        {
            await SendAsync(adapters, options, request, Ok("pong"));
        }

        recorder.Records.ShouldHaveSingleItem().GroupName.ShouldBe("opt-group");
    }

    private static (WarpAdapters Adapters, CapturingRecorder Recorder) Harness()
    {
        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters();

        return (adapters, recorder);
    }

    private static async Task SendAsync(WarpAdapters adapters, WarpAdapterHttpOptions options, HttpRequestMessage request, HttpResponseMessage response)
    {
        var handler = new WarpAdapterHandler("vendor", options, adapters, Resolver()) { InnerHandler = new StubHandler(response) };
        using var invoker = new HttpMessageInvoker(handler);
        var result = await invoker.SendAsync(request, Xunit.TestContext.Current.CancellationToken);
        result.Dispose();
    }

    private static OperationNameResolver Resolver() => new(NullLogger<OperationNameResolver>.Instance);

    private static HttpRequestMessage Get(string url) => new(HttpMethod.Get, url);

    private static HttpResponseMessage Ok(string body) => Status(System.Net.HttpStatusCode.OK, body);

    private static HttpResponseMessage Status(System.Net.HttpStatusCode code, string body)
        => new(code) { Content = new StringContent(body) };
}

/// <summary>Returns a fixed response for any request (no network).</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly HttpResponseMessage _response;

    public StubHandler(HttpResponseMessage response) => _response = response;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(_response);
}

/// <summary>Simulates a transport failure (connection error) for the failure-path tests.</summary>
internal sealed class ThrowingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => throw new HttpRequestException("connection refused");
}

/// <summary>Simulates an HttpClient timeout / caller cancellation surfacing from the send itself.</summary>
internal sealed class TaskCanceledHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => throw new TaskCanceledException("the request was canceled due to the configured HttpClient.Timeout");
}

/// <summary>
/// Request content that simulates cancellation firing while the body is being buffered for capture —
/// the OCE must be mapped to a Failed outcome by the handler, never left to Dispose-as-Success.
/// </summary>
internal sealed class CancelledDuringReadContent : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        => throw new OperationCanceledException("cancelled while buffering the request body");

    protected override bool TryComputeLength(out long length)
    {
        length = 0;

        return false;
    }
}

/// <summary>
/// A non-seekable, forward-only, single-pass stream — models a live network response body
/// (<c>HttpConnectionResponseContent</c>), which can be read exactly once. Reading it destructively
/// consumes it: proves the handler's response capture buffers rather than raw-reads the caller's stream.
/// </summary>
internal sealed class OneShotStream : Stream
{
    private readonly byte[] _bytes;
    private int _position;

    public OneShotStream(string content) => _bytes = Encoding.UTF8.GetBytes(content);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var toRead = Math.Min(count, _bytes.Length - _position);
        if (toRead <= 0)
        {
            return 0;
        }

        Array.Copy(_bytes, _position, buffer, offset, toRead);
        _position += toRead;

        return toRead;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// A forward-only stream whose reads honour the cancellation token — a cancelled token makes buffering
/// throw <see cref="OperationCanceledException"/>, modelling a slow network body cancelled mid-read. Used
/// to prove the capture path completes the scope with the call's TRUE outcome before the OCE propagates.
/// </summary>
internal sealed class CancelObservingStream : Stream
{
    private readonly byte[] _bytes;
    private int _position;

    public CancelObservingStream(string content) => _bytes = Encoding.UTF8.GetBytes(content);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var toRead = Math.Min(buffer.Length, _bytes.Length - _position);
        if (toRead <= 0)
        {
            return ValueTask.FromResult(0);
        }

        _bytes.AsSpan(_position, toRead).CopyTo(buffer.Span);
        _position += toRead;

        return ValueTask.FromResult(toRead);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
