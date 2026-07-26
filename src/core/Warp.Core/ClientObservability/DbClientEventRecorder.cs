using System.Threading.Channels;

namespace Warp.Core.ClientObservability;

/// <summary>
/// Default <see cref="IClientEventRecorder"/>: a bounded, single-reader channel drained by
/// <see cref="ClientEventFlusher{TContext}"/> (§8.27) — the client-side mirror of
/// <c>DbEndpointCallRecorder</c>. <see cref="Record"/> uses <c>TryWrite</c> and returns false when the buffer
/// is full (drop-by-design; the ingest endpoint increments <c>warp.client.events.dropped</c>), so a browser
/// beacon is never blocked or failed by a slow database.
/// </summary>
public sealed class DbClientEventRecorder : IClientEventRecorder
{
    internal const int DefaultCapacity = 10_000;

    private readonly Channel<ClientEventRecord> _channel;

    public DbClientEventRecorder(int capacity)
    {
        _channel = Channel.CreateBounded<ClientEventRecord>(new BoundedChannelOptions(capacity <= 0 ? DefaultCapacity : capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    internal ChannelReader<ClientEventRecord> Reader => _channel.Reader;

    // TryWrite (not a Wait-mode write) so a full buffer drops immediately and the caller can count it — the
    // browser ingest path must never await a persist.
    public bool Record(ClientEventRecord record) => _channel.Writer.TryWrite(record);

    internal void Complete() => _channel.Writer.TryComplete();
}
