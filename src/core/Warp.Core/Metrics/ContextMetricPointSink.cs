using Microsoft.EntityFrameworkCore;
using Warp.Core.Data.Entities;

namespace Warp.Core.Metrics;

/// <summary>
/// Writes metric points on the host's own <typeparamref name="TContext"/> — the sink of an <c>AddWarp</c>-only
/// process, like the adapter, endpoint and client flushers (§2.15). A Warp server replaces it with the server
/// context sink (§2.14).
/// </summary>
internal sealed class ContextMetricPointSink<TContext> : IMetricPointSink
    where TContext : DbContext
{
    private readonly TContext _context;

    public ContextMetricPointSink(TContext context) => _context = context;

    public Task WritePointsAsync(IReadOnlyList<MetricPoint> points, CancellationToken ct)
        => MetricSinkWriter.WritePointsAsync(_context, points, ct);

    public Task UpsertInstrumentsAsync(IReadOnlyList<MetricInstrument> rows, CancellationToken ct)
        => MetricSinkWriter.UpsertInstrumentsAsync(_context, rows, ct);
}
