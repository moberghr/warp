using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Logging;
using Warp.Core.Notifiers;

namespace Warp.Core.Services;

/// <summary>
/// Per-process reporter that makes dropped records visible in-box. The lossy pipelines (§8.19/§8.21/§8.27) already
/// increment an always-on OTel meter on a drop, but that is only observable through an OTel backend. This drains
/// the in-process <see cref="DroppedRecordCounters"/> every 30s and folds the delta to the durable
/// <c>warpsys:records-dropped:{pipeline}</c> tiered stat (so Warp's own dashboard can show "dropped in the last
/// N hours" without OTel), then raises a throttled <see cref="RecordsDroppedEvent"/> so a saturated pipeline is
/// alertable via the notifier seam (§8.25). A plain <see cref="BackgroundService"/> (not a locked
/// <c>IServerTask</c>) because the counters are per-process — each process must report the records it itself
/// dropped. No DB work when there's nothing to report, so it's a good neighbour on the shared connection.
/// </summary>
internal sealed class DroppedRecordReporter<TContext> : BackgroundService
    where TContext : DbContext
{
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _alertCooldown = TimeSpan.FromMinutes(5);
    private readonly DropPipeline[] _pipelines = [DropPipeline.Adapter, DropPipeline.Endpoint, DropPipeline.Client];

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly WarpConfiguration _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly WarpNotifierDispatcher _notifier;
    private readonly ILogger<DroppedRecordReporter<TContext>> _logger;
    private readonly Dictionary<DropPipeline, DateTime> _lastAlert = [];

    public DroppedRecordReporter(
        IServiceScopeFactory scopeFactory,
        IOptions<WarpConfiguration> configuration,
        TimeProvider timeProvider,
        WarpNotifierDispatcher notifier,
        ILogger<DroppedRecordReporter<TContext>> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration.Value;
        _timeProvider = timeProvider;
        _notifier = notifier;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, stoppingToken);
                await ReportOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dropped-record reporter iteration failed");
            }
        }
    }

    // Drains the in-process drop counters, folds each non-zero delta to warpsys:records-dropped:{pipeline} (tiered,
    // §8.30), then raises a throttled RecordsDropped event per pipeline. Internal so a test can drive one pass.
    internal async Task ReportOnceAsync(CancellationToken ct)
    {
        List<(DropPipeline Pipeline, long Count)>? drained = null;
        foreach (var pipeline in _pipelines)
        {
            var count = DroppedRecordCounters.Drain(pipeline);
            if (count > 0)
            {
                (drained ??= []).Add((pipeline, count));
            }
        }

        if (drained is null)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var tierSuffix = MetricTiers.Suffix(MetricTier.Fine, now, _configuration.FineResolutionMinutes);

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        foreach (var (pipeline, count) in drained)
        {
            context.Set<Counter>().Add(new Counter { Key = DroppedRecordKeys.History(pipeline, tierSuffix), Value = (int)Math.Min(count, int.MaxValue) });
        }

        await context.SaveChangesAsync(ct);

        // Post-commit: a throttled Warning event per pipeline so a saturated pipeline is alertable in-box.
        foreach (var (pipeline, count) in drained)
        {
            if (_lastAlert.TryGetValue(pipeline, out var last) && now - last < _alertCooldown)
            {
                continue;
            }

            _lastAlert[pipeline] = now;
            var token = DroppedRecordKeys.Token(pipeline);
            _logger.LogWarning("Recording pipeline '{Pipeline}' dropped {Count} record(s) — the bounded channel is saturated.", token, count);
            await _notifier.DispatchAsync(
                new RecordsDroppedEvent
                {
                    Type = WarpEventType.RecordsDropped,
                    Severity = WarpEventSeverity.Warning,
                    TimestampUtc = now,
                    MachineName = Environment.MachineName,
                    Application = _configuration.ApplicationName,
                    Message = $"Recording pipeline '{token}' dropped {count} record(s) on {Environment.MachineName} — channel saturated.",
                    Pipeline = token,
                    Count = count,
                },
                CancellationToken.None);
        }
    }
}
