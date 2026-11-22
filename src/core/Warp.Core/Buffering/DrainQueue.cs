using System.Threading.Channels;

namespace Warp.Core.Buffering;

/// <summary>
/// The writer side of a lossy in-process pipeline: a bounded, single-reader channel that producers add
/// to without ever waiting, drained by one <see cref="DrainService{TItem}"/>. Shared by the adapter,
/// endpoint and client-event recorders and the enqueue-notification relay.
/// <para>
/// <see cref="TryAdd"/> returns <c>false</c> when the queue is full (or completed) rather than waiting or
/// silently discarding, so the producer can count the drop. Producers sit on request/save paths and
/// must never be slowed by a slow consumer.
/// </para>
/// </summary>
internal sealed class DrainQueue<TItem>
{
    private readonly Channel<TItem> _channel;

    public DrainQueue(int capacity)
    {
        // FullMode.Wait with TryWrite: a full channel makes TryWrite return false (countable), where
        // DropWrite/DropOldest would report success and lose the item silently.
        _channel = Channel.CreateBounded<TItem>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public ChannelReader<TItem> Reader => _channel.Reader;

    public bool TryAdd(TItem item) => _channel.Writer.TryWrite(item);

    /// <summary>Stops accepting items; <see cref="TryAdd"/> returns false from here on. Called at shutdown,
    /// before the final drain.</summary>
    public void Complete() => _channel.Writer.TryComplete();
}
