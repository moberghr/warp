using System.Threading.Channels;

namespace Warp.Core.Endpoints;

/// <summary>
/// Persistence-backed <see cref="IEndpointCallRecorder"/> — the inbound mirror of
/// <c>DbAdapterCallRecorder</c>. Owns a bounded, single-reader <see cref="Channel{T}"/>;
/// <see cref="Record"/> is a non-blocking <c>TryWrite</c> that returns <c>false</c> when the channel is
/// full (the caller counts the drop and moves on — recording is lossy by design and never blocks or
/// fails an inbound request). The <see cref="EndpointCallFlusher{TContext}"/> drains the reader in
/// batches on a DI scope. Singleton so the channel outlives individual scopes.
/// </summary>
public sealed class DbEndpointCallRecorder : IEndpointCallRecorder
{
    /// <summary>Default channel capacity — generous headroom before back-pressure drops kick in.</summary>
    internal const int DefaultCapacity = 10_000;

    private readonly Channel<EndpointCallRecord> _channel;

    public DbEndpointCallRecorder()
        : this(DefaultCapacity)
    {
    }

    internal DbEndpointCallRecorder(int capacity)
    {
        // SingleReader: only the flusher drains. FullMode.DropWrite would silently discard and report
        // success; we want TryWrite to return false on a full channel so the caller can count the drop.
        _channel = Channel.CreateBounded<EndpointCallRecord>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    internal ChannelReader<EndpointCallRecord> Reader => _channel.Reader;

    public bool Record(EndpointCallRecord record) => _channel.Writer.TryWrite(record);

    internal void Complete() => _channel.Writer.TryComplete();
}
