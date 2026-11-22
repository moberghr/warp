using System.Threading.Channels;
using Warp.Core.Buffering;

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

    private readonly DrainQueue<EndpointCallRecord> _queue;

    public DbEndpointCallRecorder()
        : this(DefaultCapacity)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DbEndpointCallRecorder"/> class with an explicit
    /// bounded-channel capacity (from <c>WarpConfiguration.CallLogBufferCapacity</c>).
    /// </summary>
    public DbEndpointCallRecorder(int capacity)
    {
        _queue = new DrainQueue<EndpointCallRecord>(capacity);
    }

    internal ChannelReader<EndpointCallRecord> Reader => _queue.Reader;

    public bool Record(EndpointCallRecord record) => _queue.TryAdd(record);

    internal void Complete() => _queue.Complete();
}
