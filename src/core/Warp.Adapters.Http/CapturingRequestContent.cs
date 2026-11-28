using System.Net;

namespace Warp.Adapters.Http;

// Pass-through request content: serializes the wrapped content straight into the transport's stream and
// hands the bytes written to a RequestBodyCapture on the way. Nothing is buffered ahead of the send and
// the body is read once, by the transport. Headers are copied from the wrapped content, and the length is
// the wrapped content's own (known length stays Content-Length, unknown stays chunked).
internal sealed class CapturingRequestContent : HttpContent
{
    private readonly HttpContent _inner;
    private readonly RequestBodyCapture _capture;

    public CapturingRequestContent(HttpContent inner, RequestBodyCapture capture)
    {
        _inner = inner;
        _capture = capture;

        foreach (var header in inner.Headers)
        {
            Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        _capture.Reset();

        return _inner.CopyToAsync(new CaptureWriteStream(stream, _capture), context, cancellationToken);
    }

    protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        _capture.Reset();
        _inner.CopyTo(new CaptureWriteStream(stream, _capture), context, cancellationToken);
    }

    protected override bool TryComputeLength(out long length)
    {
        var innerLength = _inner.Headers.ContentLength;
        length = innerLength ?? 0;

        return innerLength.HasValue;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
