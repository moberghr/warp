using Microsoft.Extensions.Hosting;

namespace Warp.Core.Metrics;

/// <summary>
/// Ties the <see cref="WarpMeterCollector"/> listener to the host's lifetime: started in <see cref="StartAsync"/>,
/// disposed in <see cref="StopAsync"/>.
/// </summary>
internal sealed class MetricCollectorHost : IHostedService
{
    private readonly WarpMeterCollector _collector;

    public MetricCollectorHost(WarpMeterCollector collector)
    {
        _collector = collector;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _collector.Start();

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _collector.Stop();

        return Task.CompletedTask;
    }
}
