using Microsoft.EntityFrameworkCore;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Metrics;

namespace Warp.Worker.Services;

/// <summary>
/// Writes metric points on the Warp server context (§2.14), the sink of every Warp server process. Registered by
/// <c>AddServerHostCore</c> with a replace, so it wins over the <c>AddWarp</c>-side context sink whichever of
/// <c>AddMetrics</c> and <c>AddWarpServer</c> ran first.
/// </summary>
internal sealed class ServerMetricPointSink : IMetricPointSink
{
    private readonly DbContext _context;

    public ServerMetricPointSink(IWarpServerContext serverContext) => _context = serverContext.Context;

    public Task WritePointsAsync(IReadOnlyList<MetricPoint> points, CancellationToken ct)
        => MetricSinkWriter.WritePointsAsync(_context, points, ct);

    public Task UpsertInstrumentsAsync(IReadOnlyList<MetricInstrument> rows, CancellationToken ct)
        => MetricSinkWriter.UpsertInstrumentsAsync(_context, rows, ct);
}
