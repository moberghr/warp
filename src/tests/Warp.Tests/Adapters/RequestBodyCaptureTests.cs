using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Warp.Adapters.Http;
using Warp.Core.Adapters;
using Warp.Core.Enums;

namespace Warp.Tests.Adapters;

/// <summary>
/// Request-body capture is bounded and non-destructive (#365): the body is copied as the transport writes
/// it, never buffered ahead of the send, and only the first <c>MaxCapturedBodySize</c> bytes are kept.
/// The inner handlers stand in for the transport and read the body the way <c>SocketsHttpHandler</c> does
/// (<see cref="HttpContent.CopyToAsync(Stream, CancellationToken)"/>).
/// </summary>
[Trait("Category", "NoDb")]
public class RequestBodyCaptureTests
{
    [TimedFact]
    public async Task LargeStreamedBody_AllocationBounded_CapturesPrefixOfCap()
    {
        const int bodySize = 32 * 1024 * 1024;
        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters();
        var options = Options(CaptureMode.Always, cap: 1024);
        var content = new GeneratedContent(bodySize);
        var drain = new CountingStream();
        var handler = new WarpAdapterHandler("vendor", options, adapters, Resolver()) { InnerHandler = new DrainingHandler(drain) };
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.vendor.com/upload") { Content = content };

        // Every stage completes synchronously (in-memory body, in-memory sink), so the whole send runs on
        // this thread and the per-thread allocation counter sees all of it.
        var before = GC.GetAllocatedBytesForCurrentThread();
        var send = invoker.SendAsync(request, Xunit.TestContext.Current.CancellationToken);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        send.IsCompletedSuccessfully.ShouldBeTrue();
        using var response = await send;
        drain.Total.ShouldBe(bodySize);

        // Buffering the body would allocate at least its 32 MiB; the capture keeps cap + 1 bytes.
        allocated.ShouldBeLessThan(1024 * 1024);

        var body = recorder.Records.ShouldHaveSingleItem().RequestBody;
        body.ShouldNotBeNull();
        body.ShouldEndWith("…");
        Encoding.UTF8.GetByteCount(body).ShouldBeLessThanOrEqualTo(1024);
        body.ShouldStartWith(new string('x', 1000));
    }

    [TimedFact]
    public async Task NonSeekableStreamedBody_StillStreams_AndIsCaptured()
    {
        // The body yields its second chunk only after the transport has received the first. Buffering the
        // body before the send (the old LoadIntoBufferAsync) would wait for the second chunk forever.
        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters();
        var options = Options(CaptureMode.Always, cap: 1024);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = new GatedReadStream("first-", "second", gate.Task);
        var content = new StreamContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        var drain = new CountingStream(onFirstWrite: () => gate.TrySetResult());
        var transport = new DrainingHandler(drain);
        var handler = new WarpAdapterHandler("vendor", options, adapters, Resolver()) { InnerHandler = transport };
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.vendor.com/upload") { Content = content };

        using var response = await invoker.SendAsync(request, Xunit.TestContext.Current.CancellationToken);

        drain.Text.ShouldBe("first-second");
        recorder.Records.ShouldHaveSingleItem().RequestBody.ShouldBe("first-second");

        // The transport saw the caller's headers and an unknown length (chunked); the caller gets its own
        // content back once the send returns.
        transport.SeenContentType.ShouldBe("text/plain");
        transport.SeenContentLength.ShouldBeNull();
        request.Content.ShouldBeSameAs(content);
    }

    [TimedFact]
    public async Task StreamedBodyWithKnownLength_KeepsContentLength()
    {
        var (adapters, _, _) = AdapterTestHarness.CreateAdapters();
        var options = Options(CaptureMode.Always, cap: 1024);
        var transport = new DrainingHandler(new CountingStream());
        var handler = new WarpAdapterHandler("vendor", options, adapters, Resolver()) { InnerHandler = transport };
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.vendor.com/upload") { Content = new GeneratedContent(10) };

        using var response = await invoker.SendAsync(request, Xunit.TestContext.Current.CancellationToken);

        transport.SeenContentLength.ShouldBe(10);
    }

    [TimedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BodyOfExactlyCap_CapturedWhole(bool streamed)
    {
        var record = await SendBodyAsync("hello", cap: 5, streamed);

        record.RequestBody.ShouldBe("hello");
    }

    [TimedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BodyOfCapPlusOne_Truncated(bool streamed)
    {
        var record = await SendBodyAsync("hello!", cap: 5, streamed);

        var body = record.RequestBody;
        body.ShouldNotBeNull();
        body.ShouldEndWith("…");
        Encoding.UTF8.GetByteCount(body).ShouldBeLessThanOrEqualTo(5);
    }

    [TimedFact]
    public async Task StreamedBodyNeverRead_FailedCall_RecordsNoBody_AndDoesNotReadIt()
    {
        // The server answers without the transport ever writing the body: nothing was sent, so nothing is
        // recorded, and capture does not read the body itself.
        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters();
        var options = Options(CaptureMode.OnFailure, cap: 1024);
        var stream = new OneShotStream("never-sent");
        var handler = new WarpAdapterHandler("vendor", options, adapters, Resolver())
        {
            InnerHandler = new StubHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") }),
        };
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.vendor.com/upload") { Content = new StreamContent(stream) };

        using var response = await invoker.SendAsync(request, Xunit.TestContext.Current.CancellationToken);

        var record = recorder.Records.ShouldHaveSingleItem();
        record.Outcome.ShouldBe(AdapterCallOutcome.Failed);
        record.RequestBody.ShouldBeNull();
        stream.Position.ShouldBe(0);
    }

    [TimedFact]
    public async Task StreamedBodyPartlyWritten_TransportFails_RecordsWhatWasWritten()
    {
        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters();
        var options = Options(CaptureMode.OnFailure, cap: 1024);
        var handler = new WarpAdapterHandler("vendor", options, adapters, Resolver()) { InnerHandler = new DrainingHandler(new FailingWriteStream(failAfter: 4)) };
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.vendor.com/upload")
        {
            Content = new StreamContent(new ChunkedReadStream("abcd", "efgh")),
        };

        // HttpContent.CopyToAsync surfaces the sink's IOException as an HttpRequestException, as a real transport does.
        await Should.ThrowAsync<HttpRequestException>(async () => await invoker.SendAsync(request, Xunit.TestContext.Current.CancellationToken));

        var record = recorder.Records.ShouldHaveSingleItem();
        record.Outcome.ShouldBe(AdapterCallOutcome.Failed);
        record.RequestBody.ShouldBe("abcd");
    }

    [TimedFact]
    public async Task StreamedBodySentTwice_CapturesLastAttemptOnly()
    {
        // A resilience handler re-sends the same content on retry; the capture restarts with each attempt
        // instead of concatenating them.
        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters();
        var options = Options(CaptureMode.Always, cap: 1024);
        var handler = new WarpAdapterHandler("vendor", options, adapters, Resolver()) { InnerHandler = new DrainingHandler(new CountingStream(), attempts: 2) };
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.vendor.com/upload")
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes("payload"))),
        };

        using var response = await invoker.SendAsync(request, Xunit.TestContext.Current.CancellationToken);

        recorder.Records.ShouldHaveSingleItem().RequestBody.ShouldBe("payload");
    }

    [TimedFact]
    public async Task BufferedBody_LeftOnRequestUnwrapped()
    {
        var (adapters, _, _) = AdapterTestHarness.CreateAdapters();
        var options = Options(CaptureMode.Always, cap: 1024);
        var transport = new DrainingHandler(new CountingStream());
        var handler = new WarpAdapterHandler("vendor", options, adapters, Resolver()) { InnerHandler = transport };
        using var invoker = new HttpMessageInvoker(handler);
        var content = new StringContent("buffered");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.vendor.com/upload") { Content = content };

        using var response = await invoker.SendAsync(request, Xunit.TestContext.Current.CancellationToken);

        transport.SeenContent.ShouldBeSameAs(content);
    }

    [TimedFact]
    public async Task StreamedBody_TransportCancelledWhileReading_RecordsFailed_ThenRethrows()
    {
        // Cancellation while the transport reads the body must record the call Failed, never let the OCE
        // unwind past the scope so Dispose() defaults the call to Success.
        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters();
        var options = Options(CaptureMode.Always, cap: 1024);
        var handler = new WarpAdapterHandler("vendor", options, adapters, Resolver()) { InnerHandler = new DrainingHandler(new CountingStream()) };
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.vendor.com/orders") { Content = new CancelledDuringReadContent() };

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await invoker.SendAsync(request, Xunit.TestContext.Current.CancellationToken));

        var record = recorder.Records.ShouldHaveSingleItem();
        record.Outcome.ShouldBe(AdapterCallOutcome.Failed);
        record.ExceptionType.ShouldNotBeNull();
    }

    private static async Task<AdapterCallRecord> SendBodyAsync(string text, int cap, bool streamed)
    {
        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters();
        var options = Options(CaptureMode.Always, cap);
        var handler = new WarpAdapterHandler("vendor", options, adapters, Resolver()) { InnerHandler = new DrainingHandler(new CountingStream()) };
        using var invoker = new HttpMessageInvoker(handler);
        HttpContent content = streamed ? new StreamContent(new OneShotStream(text)) : new StringContent(text);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.vendor.com/upload") { Content = content };

        using var response = await invoker.SendAsync(request, Xunit.TestContext.Current.CancellationToken);

        return recorder.Records.ShouldHaveSingleItem();
    }

    private static WarpAdapterHttpOptions Options(CaptureMode requestBodies, int cap)
    {
        var options = new WarpAdapterHttpOptions();
        options.Recording.CaptureRequestBodies = requestBodies;
        options.Recording.MaxCapturedBodySize = cap;

        return options;
    }

    private static OperationNameResolver Resolver() => new(NullLogger<OperationNameResolver>.Instance);
}

/// <summary>Reads the request body into a sink the way a transport does, then answers 200.</summary>
internal sealed class DrainingHandler : HttpMessageHandler
{
    private readonly Stream _sink;
    private readonly int _attempts;

    public DrainingHandler(Stream sink, int attempts = 1)
    {
        _sink = sink;
        _attempts = attempts;
    }

    public HttpContent? SeenContent { get; private set; }

    public string? SeenContentType { get; private set; }

    public long? SeenContentLength { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        SeenContent = request.Content;
        SeenContentType = request.Content?.Headers.ContentType?.MediaType;
        SeenContentLength = request.Content?.Headers.ContentLength;

        for (var i = 0; i < _attempts && request.Content is not null; i++)
        {
            await request.Content.CopyToAsync(_sink, cancellationToken);
        }

        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}

/// <summary>Write-only sink that counts bytes, keeps small writes as text and signals on its first write.</summary>
internal sealed class CountingStream : Stream
{
    private readonly Action? _onFirstWrite;
    private readonly StringBuilder _text = new();

    public CountingStream(Action? onFirstWrite = null) => _onFirstWrite = onFirstWrite;

    public long Total { get; private set; }

    public string Text => _text.ToString();

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (Total == 0 && buffer.Length > 0)
        {
            _onFirstWrite?.Invoke();
        }

        if (Total < 4096)
        {
            _text.Append(Encoding.UTF8.GetString(buffer));
        }

        Total += buffer.Length;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);

        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>Write-only sink that accepts <c>failAfter</c> bytes and then fails, like a connection reset.</summary>
internal sealed class FailingWriteStream : Stream
{
    private readonly int _failAfter;
    private int _written;

    public FailingWriteStream(int failAfter) => _failAfter = failAfter;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (_written >= _failAfter)
        {
            throw new IOException("connection reset");
        }

        _written += buffer.Length;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);

        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>A body of <c>size</c> 'x' bytes with a known length, written in chunks from one reused buffer.</summary>
internal sealed class GeneratedContent : HttpContent
{
    private readonly long _size;

    public GeneratedContent(long size) => _size = size;

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        var chunk = new byte[16 * 1024];
        Array.Fill(chunk, (byte)'x');
        var remaining = _size;
        while (remaining > 0)
        {
            var count = (int)Math.Min(chunk.Length, remaining);
            await stream.WriteAsync(chunk.AsMemory(0, count));
            remaining -= count;
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _size;

        return true;
    }
}

/// <summary>A non-seekable body that returns each chunk on its own read, in order.</summary>
internal sealed class ChunkedReadStream : Stream
{
    private readonly Queue<byte[]> _chunks;

    public ChunkedReadStream(params string[] chunks) => _chunks = new Queue<byte[]>(chunks.Select(x => Encoding.UTF8.GetBytes(x)));

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (!_chunks.TryDequeue(out var chunk))
        {
            return 0;
        }

        chunk.CopyTo(buffer);

        return chunk.Length;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Read(buffer.Span));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>A non-seekable body that returns its second chunk only once <c>gate</c> completes.</summary>
internal sealed class GatedReadStream : Stream
{
    private readonly byte[] _first;
    private readonly byte[] _second;
    private readonly Task _gate;
    private int _step;

    public GatedReadStream(string first, string second, Task gate)
    {
        _first = Encoding.UTF8.GetBytes(first);
        _second = Encoding.UTF8.GetBytes(second);
        _gate = gate;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        switch (_step++)
        {
            case 0:
                _first.CopyTo(buffer);

                return _first.Length;
            case 1:
                await _gate.WaitAsync(cancellationToken);
                _second.CopyTo(buffer);

                return _second.Length;
            default:
                return 0;
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
