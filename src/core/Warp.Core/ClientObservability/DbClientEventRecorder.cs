using System.Threading.Channels;
using Warp.Core.Buffering;

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

    private readonly DrainQueue<ClientEventRecord> _queue;

    public DbClientEventRecorder(int capacity)
    {
        _queue = new DrainQueue<ClientEventRecord>(capacity <= 0 ? DefaultCapacity : capacity);
    }

    internal ChannelReader<ClientEventRecord> Reader => _queue.Reader;

    // Never waits: a full buffer drops immediately and the caller counts it — the browser ingest path must
    // never await a persist.
    public bool Record(ClientEventRecord record) => _queue.TryAdd(record);

    internal void Complete() => _queue.Complete();
}
