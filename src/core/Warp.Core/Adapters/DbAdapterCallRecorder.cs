using System.Threading.Channels;

namespace Warp.Core.Adapters;

/// <summary>
/// Persistence-backed <see cref="IAdapterCallRecorder"/> registered by <c>AddAdapters()</c>. Owns a
/// bounded, single-reader <see cref="Channel{T}"/>; <see cref="Record"/> is a non-blocking
/// <c>TryWrite</c> that returns <c>false</c> when the channel is full (the scope then increments
/// <c>warp.adapter.records_dropped</c> and moves on — recording is lossy by design and never blocks or
/// fails a user call). The <see cref="AdapterCallFlusher{TContext}"/> drains the reader in batches on a
/// DI scope. Singleton so the channel outlives individual scopes.
/// </summary>
internal sealed class DbAdapterCallRecorder : IAdapterCallRecorder
{
    /// <summary>Default channel capacity — generous headroom before back-pressure drops kick in.</summary>
    internal const int DefaultCapacity = 10_000;

    private readonly Channel<AdapterCallRecord> _channel;

    public DbAdapterCallRecorder()
        : this(DefaultCapacity)
    {
    }

    internal DbAdapterCallRecorder(int capacity)
    {
        // SingleReader: only the flusher drains. FullMode.DropWrite would silently discard and report
        // success; we want TryWrite to return false on a full channel so the scope can count the drop.
        _channel = Channel.CreateBounded<AdapterCallRecord>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    internal ChannelReader<AdapterCallRecord> Reader => _channel.Reader;

    public bool Record(AdapterCallRecord record) => _channel.Writer.TryWrite(record);

    internal void Complete() => _channel.Writer.TryComplete();
}
