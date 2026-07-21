namespace Warp.Http.Observability;

/// <summary>
/// A write-through stream that forwards every write to the real response body (so the client receives the
/// full, unmodified response) while capturing at most <c>maxBytes</c> into an in-memory buffer for the
/// call log. Bounded so a large / streaming response (e.g. a file download via <c>IResult</c>) never
/// balloons memory — the capture stops at the cap, the passthrough continues.
/// </summary>
internal sealed class CaptureBodyStream : Stream
{
    private readonly Stream _inner;
    private readonly int _maxBytes;
    private readonly MemoryStream _captured = new();

    public CaptureBodyStream(Stream inner, int maxBytes)
    {
        _inner = inner;
        _maxBytes = maxBytes;
    }

    public byte[] CapturedBytes => _captured.ToArray();

    // True once a write was dropped because the cap was hit — the captured buffer is a prefix, so the
    // decoder must cut on a UTF-8 boundary and append the truncation marker.
    public bool Truncated { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _inner.CanSeek ? _inner.Position : _captured.Length;
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override void Write(byte[] buffer, int offset, int count)
    {
        CaptureInto(buffer.AsSpan(offset, count));
        _inner.Write(buffer, offset, count);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        CaptureInto(buffer.AsSpan(offset, count));
        await _inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        CaptureInto(buffer.Span);
        await _inner.WriteAsync(buffer, cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    private void CaptureInto(ReadOnlySpan<byte> span)
    {
        var room = _maxBytes - (int)_captured.Length;
        if (room <= 0)
        {
            Truncated = true;

            return;
        }

        if (span.Length > room)
        {
            Truncated = true;
        }

        _captured.Write(span[..Math.Min(room, span.Length)]);
    }
}
