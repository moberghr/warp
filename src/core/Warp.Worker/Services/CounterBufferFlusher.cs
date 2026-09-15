using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Services;

namespace Warp.Worker.Services;

/// <summary>
/// Writes the process's accumulated <see cref="WarpCounterBuffer"/> increments out as <c>Counter</c>
/// rows for <see cref="CounterAggregator{TContext}"/> to fold.
/// <para>
/// A plain <see cref="BackgroundService"/> rather than an <c>IServerTask</c>, for the same reason as
/// <c>DroppedRecordReporter</c> (§8.32): the buffer is per-process, so every process must write out
/// its own and a cluster-wide lock would be actively wrong. No DB work when nothing is buffered.
/// </para>
/// <para>
/// This collapses the per-increment row write into one row per distinct key per interval. It does
/// not change what <c>CounterAggregator</c> sees — the rows are identical in shape, there are just
/// far fewer of them carrying larger values.
/// </para>
/// </summary>
internal sealed class CounterBufferFlusher<TContext> : BackgroundService
    where TContext : DbContext
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly WarpCounterBuffer _buffer;
    private readonly WarpServerConfiguration _configuration;
    private readonly ILogger<CounterBufferFlusher<TContext>> _logger;

    public CounterBufferFlusher(
        IServiceScopeFactory scopeFactory,
        WarpCounterBuffer buffer,
        IOptions<WarpServerConfiguration> configuration,
        ILogger<CounterBufferFlusher<TContext>> logger)
    {
        _scopeFactory = scopeFactory;
        _buffer = buffer;
        _configuration = configuration.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = _configuration.CounterBufferFlushInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
                await FlushOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never propagate: losing a metrics flush must not fault the host.
                _logger.LogError(ex, "Counter buffer flush failed");
            }
        }
    }

    /// <summary>
    /// Final flush on graceful shutdown, so an orderly stop loses nothing. An ungraceful exit still
    /// loses whatever accumulated since the last interval — the documented trade.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        try
        {
            await FlushOnceAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Final counter buffer flush failed during shutdown");
        }
    }

    internal async Task<int> FlushOnceAsync(CancellationToken ct)
    {
        var pending = _buffer.Drain();
        if (pending.Count == 0)
        {
            return 0;
        }

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IWarpServerContext>().Context;

        foreach (var (key, value) in pending)
        {
            foreach (var chunk in Split(value))
            {
                context.Set<Counter>().Add(new Counter { Key = key, Value = chunk });
            }
        }

        await context.SaveChangesAsync(ct);

        return pending.Count;
    }

    /// <summary>
    /// Counter.Value is an int while the buffer accumulates a long, so a key that summed past
    /// int.MaxValue within one interval is emitted as several rows. Counters are additive, so the
    /// fold produces the same total either way. Realistically unreachable — a duration-sum key would
    /// need billions of milliseconds in one interval — but silently truncating a metric is worse
    /// than a second row.
    /// </summary>
    private static IEnumerable<int> Split(long value)
    {
        while (value > int.MaxValue)
        {
            yield return int.MaxValue;
            value -= int.MaxValue;
        }

        while (value < int.MinValue)
        {
            yield return int.MinValue;
            value -= int.MinValue;
        }

        if (value != 0)
        {
            yield return (int)value;
        }
    }
}
