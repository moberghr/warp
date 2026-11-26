namespace Warp.Adapters.Http;

// Read-only stream that replays a prefix already read from `inner` and then continues with the rest of
// `inner`. Lets response capture peek at the head of a forward-only body without buffering the whole
// of it and without the caller losing those bytes.
internal sealed class PrefixedReadStream : Stream
{
    private readonly byte[] _prefix;
    private readonly int _prefixLength;
    private readonly Stream _inner;
    private readonly IDisposable _owner;
    private int _prefixPosition;

    public PrefixedReadStream(byte[] prefix, int prefixLength, Stream inner, IDisposable owner)
    {
        _prefix = prefix;
        _prefixLength = prefixLength;
        _inner = inner;
        _owner = owner;
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

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var fromPrefix = ReadPrefix(buffer);

        return fromPrefix > 0 ? fromPrefix : _inner.Read(buffer);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var fromPrefix = ReadPrefix(buffer.Span);

        return fromPrefix > 0 ? ValueTask.FromResult(fromPrefix) : _inner.ReadAsync(buffer, cancellationToken);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            _owner.Dispose();
        }

        base.Dispose(disposing);
    }

    private int ReadPrefix(Span<byte> buffer)
    {
        var remaining = _prefixLength - _prefixPosition;
        if (remaining <= 0 || buffer.IsEmpty)
        {
            return 0;
        }

        var count = Math.Min(remaining, buffer.Length);
        _prefix.AsSpan(_prefixPosition, count).CopyTo(buffer);
        _prefixPosition += count;

        return count;
    }
}
