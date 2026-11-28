using System.Text;

namespace Warp.Adapters.Http;

// Bounded, non-destructive capture of an outgoing request body. Keeps at most limit bytes (the capture cap
// plus one, so an over-cap body still trips the truncation marker) and never reads the body on its own.
// An in-memory content (a ByteArrayContent or one of its subclasses, or a ReadOnlyMemoryContent) stays on
// the request as it is, and its prefix is copied from memory only when the call is recorded. Any other
// content is swapped for a CapturingRequestContent for the duration of the send, which copies the prefix
// as the transport writes the body. A body the transport never wrote records nothing.
internal sealed class RequestBodyCapture
{
    private readonly int _limit;
    private readonly HttpContent? _buffered;
    private readonly HttpContent? _original;
    private readonly Lock _gate = new();
    private byte[]? _buffer;
    private int _count;
    private bool _started;

    private RequestBodyCapture(int limit, HttpContent? buffered, HttpContent? original)
    {
        _limit = limit;
        _buffered = buffered;
        _original = original;
    }

    public static RequestBodyCapture Begin(HttpRequestMessage request, int maxBytes)
    {
        var content = request.Content!;
        var limit = maxBytes + 1;
        if (content is ByteArrayContent or ReadOnlyMemoryContent)
        {
            return new RequestBodyCapture(limit, buffered: content, original: null);
        }

        var capture = new RequestBodyCapture(limit, buffered: null, original: content);
        request.Content = new CapturingRequestContent(content, capture);

        return capture;
    }

    // Puts the caller's own content back once the send has returned, so code that inspects the request
    // afterwards (response.RequestMessage.Content) sees what it sent.
    public void Restore(HttpRequestMessage request)
    {
        if (_original is null)
        {
            return;
        }

        try
        {
            request.Content = _original;
        }
        catch (ObjectDisposedException)
        {
            // The request was disposed mid-send; disposing the wrapper already disposed the original.
        }
    }

    public async Task<string?> ReadAsync()
    {
        if (_buffered is not null)
        {
            // An in-memory copy that writes once into a sink keeping only the prefix. No token: it cannot block,
            // and a cancelled caller token must not abort the recording of the call's outcome.
            await _buffered.CopyToAsync(new CaptureWriteStream(Stream.Null, this), CancellationToken.None);
        }

        lock (_gate)
        {
            if (!_started)
            {
                return null;
            }

            return _count == 0 ? string.Empty : Encoding.UTF8.GetString(_buffer!, 0, _count);
        }
    }

    // Called at the start of every serialization of the wrapped body. A resilience handler that retries
    // re-sends the same content, so the capture restarts and reflects the last attempt.
    public void Reset()
    {
        lock (_gate)
        {
            _started = true;
            _count = 0;
        }
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            _started = true;
            var count = Math.Min(data.Length, _limit - _count);
            if (count <= 0)
            {
                return;
            }

            _buffer ??= new byte[_limit];
            data[..count].CopyTo(_buffer.AsSpan(_count));
            _count += count;
        }
    }
}
