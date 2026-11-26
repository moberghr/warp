using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Events;
using Warp.Core.Notifications;
using Warp.Core.Notifiers;
using Warp.Core.Services;
using Warp.Core.Webhooks;

namespace Warp.Worker.Services;

/// <summary>
/// Webhook endpoint health (8.1, §8.20): releases the parked deliveries of endpoints that recovered, probes failing
/// endpoints that are due, and raises the "still failing" alert. A cluster-wide <see cref="IServerTask"/> on the
/// server context (§2.14): it stages executor jobs directly (<see cref="WebhookExecutorJobs"/>), never through the
/// <c>TContext</c>-bound <c>IPublisher</c>, and its job wakes and health signals are buffered and raised from
/// <see cref="OnCommittedAsync"/> (§8.25).
/// <para>
/// Recovery itself happens where a delivery succeeds (the executor flips the endpoint to <c>Healthy</c> in its outcome
/// commit); this task only drains what recovery left behind, at most <see cref="ReleaseBatchSize"/> deliveries per
/// run, oldest first, re-running at once while a batch comes back full. Released deliveries get a fresh attempt budget
/// and go through the normal queue, so recovery never sends more at once than the workers already would.
/// </para>
/// </summary>
public sealed class WebhookEndpointMonitor<TContext> : IServerTask
    where TContext : DbContext
{
    internal const int ReleaseBatchSize = 200;
    private const int ProbeBatchSize = 50;
    private const int StillFailingBatchSize = 100;

    private readonly DbContext _context;
    private readonly WarpServerConfiguration _configuration;
    private readonly TimeProvider _time;
    private readonly WarpNotifierDispatcher _notifier;
    private readonly IEnumerable<IWebhookEndpointHealthHandler> _healthHandlers;
    private readonly IWarpNotificationTransport _notificationTransport;
    private readonly ServerTaskSignals<TContext> _signals;
    private readonly ILogger<WebhookEndpointMonitor<TContext>> _logger;
    private readonly HashSet<Notification> _pendingNotifications = [];
    private readonly List<PendingEndpointHealthSignal> _pendingSignals = [];

    public WebhookEndpointMonitor(
        IWarpServerContext serverContext,
        IOptions<WarpServerConfiguration> configuration,
        [FromKeyedServices(DatabaseClock.Key)] TimeProvider time,
        WarpNotifierDispatcher notifier,
        IEnumerable<IWebhookEndpointHealthHandler> healthHandlers,
        IWarpNotificationTransport notificationTransport,
        ServerTaskSignals<TContext> signals,
        ILogger<WebhookEndpointMonitor<TContext>> logger)
    {
        _context = serverContext.Context;
        _configuration = configuration.Value;
        _time = time;
        _notifier = notifier;
        _healthHandlers = healthHandlers;
        _notificationTransport = notificationTransport;
        _signals = signals;
        _logger = logger;
    }

    public string Name => "WebhookEndpointMonitor";

    public string? LockKey => "warp:webhook-endpoints";

    public TimeSpan? DefaultInterval => _configuration.WebhookEndpointMonitorInterval;

    public bool RerunImmediately { get; private set; }

    public bool LogOnSuccess => false;

    public async Task<string?> ExecuteAsync(CancellationToken ct)
    {
        RerunImmediately = false;
        _pendingNotifications.Clear();
        _pendingSignals.Clear();

        // Direct callers (tests) get their own transaction; under the task host the lock transaction wraps this, the
        // guarded updates and the staged jobs commit together, and OnCommittedAsync drains the buffers.
        var hasOuterTx = _context.Database.CurrentTransaction != null;
        await using var ownedTx = hasOuterTx
            ? null
            : await _context.Database.BeginTransactionAsync(ct);

        var now = _time.GetUtcNow().UtcDateTime;
        var released = await ReleaseRecoveredAsync(now, ct);
        var probed = await ProbeDueAsync(now, ct);
        var stillFailing = await FlagStillFailingAsync(now, ct);

        if (released + probed > 0)
        {
            // Captured before the save: CapturePending reads the Added Job entries off the change tracker.
            _pendingNotifications.UnionWith(NotificationDispatch.CapturePending(_context));
            await _context.SaveChangesAsync(ct);
        }

        if (ownedTx != null)
        {
            await ownedTx.CommitAsync(ct);
            await OnCommittedAsync(ct);
        }

        if (released + probed + stillFailing == 0)
        {
            return null;
        }

        return $"Released {released} parked webhook deliveries, probed {probed} failing endpoints, {stillFailing} still failing";
    }

    public async Task OnCommittedAsync(CancellationToken ct)
    {
        if (_pendingNotifications.Count > 0)
        {
            var notifications = _pendingNotifications.ToList();
            _pendingNotifications.Clear();

            // CancellationToken.None: the jobs are committed; workers elsewhere should hear about them.
            await NotificationDispatch.DispatchAsync(notifications, _signals, _notificationTransport, CancellationToken.None);
        }

        var signals = _pendingSignals.ToList();
        _pendingSignals.Clear();

        foreach (var signal in signals)
        {
            await WebhookEndpointHealthSignals.RaiseAsync(signal, _healthHandlers, _notifier, _logger);
        }
    }

    // Parked deliveries of endpoints that are Healthy again, oldest first, back to Pending with a fresh attempt budget.
    // The staged jobs' schedule times keep that order (the claim takes the oldest ScheduleTime first) and lie just
    // before now, so the batch is claimable at once.
    internal async Task<int> ReleaseRecoveredAsync(DateTime now, CancellationToken ct)
    {
        var healthy = _context.Set<WebhookEndpoint>()
            .Where(x => x.State == WebhookEndpointState.Healthy)
            .Select(x => x.Name);

        var parked = await _context.Set<WebhookDelivery>()
            .Where(x => x.Status == WebhookDeliveryStatus.Parked)
            .Where(x => x.GroupName != null)
            .Where(x => healthy.Contains(x.GroupName!))
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Take(ReleaseBatchSize)
            .Select(x => x.Id)
            .ToListAsync(ct);

        RerunImmediately = parked.Count == ReleaseBatchSize;

        var expireAt = now + _configuration.WebhookDeliveryRetention;
        var released = 0;
        for (var i = 0; i < parked.Count; i++)
        {
            if (!await WebhookParkedRelease.ReleaseAsync(_context, parked[i], attemptCount: 0, now, expireAt, ct))
            {
                continue;
            }

            var scheduleTime = now.AddMilliseconds(i - parked.Count);
            WebhookExecutorJobs.Stage(_context, parked[i], scheduleTime, now, _configuration.ApplicationName, "endpoint recovery");
            released++;
        }

        return released;
    }

    // Each failing endpoint whose probe is due re-sends its oldest parked delivery once (one attempt left: a failure
    // parks it again). With nothing parked there is nothing to probe; the endpoint recovers on its next success.
    // Either way the next probe is one interval out.
    internal async Task<int> ProbeDueAsync(DateTime now, CancellationToken ct)
    {
        var due = await _context.Set<WebhookEndpoint>()
            .Where(x => x.State == WebhookEndpointState.Failing)
            .Where(x => x.NextProbeAt != null)
            .Where(x => x.NextProbeAt <= now)
            .OrderBy(x => x.NextProbeAt)
            .Take(ProbeBatchSize)
            .Select(x => x.Name)
            .ToListAsync(ct);

        var nextProbeAt = now + _configuration.WebhookEndpointProbeInterval;
        var expireAt = now + _configuration.WebhookDeliveryRetention;
        var probed = 0;

        foreach (var name in due)
        {
            if (await WebhookParkedRelease.ProbeOldestAsync(_context, name, now, expireAt, ct) is { } deliveryId)
            {
                WebhookExecutorJobs.Stage(_context, deliveryId, now, now, _configuration.ApplicationName, "endpoint probe");
                probed++;
            }

            await WebhookParkedRelease.DeferNextProbeAsync(_context, name, nextProbeAt, ct);
        }

        return probed;
    }

    // Once per failing period: an endpoint failing for WebhookEndpointStillFailingAfter raises the alert. The guarded
    // stamp makes it exactly one per period across servers; recovery clears the stamp.
    internal async Task<int> FlagStillFailingAsync(DateTime now, CancellationToken ct)
    {
        var cutoff = now - _configuration.WebhookEndpointStillFailingAfter;
        var candidates = await _context.Set<WebhookEndpoint>()
            .Where(x => x.State == WebhookEndpointState.Failing)
            .Where(x => x.StillFailingNotifiedAt == null)
            .Where(x => x.FailingSince != null)
            .Where(x => x.FailingSince <= cutoff)
            .Take(StillFailingBatchSize)
            .Select(x =>
                new
                {
                    x.Name,
                    x.FailingSince,
                })
            .ToListAsync(ct);

        var flagged = 0;
        foreach (var candidate in candidates)
        {
            var stamped = await _context.Set<WebhookEndpoint>()
                .Where(x => x.Name == candidate.Name)
                .Where(x => x.State == WebhookEndpointState.Failing)
                .Where(x => x.StillFailingNotifiedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.StillFailingNotifiedAt, now), ct);

            if (stamped == 1)
            {
                _pendingSignals.Add(new PendingEndpointHealthSignal(WarpEventType.WebhookEndpointStillFailing, candidate.Name, candidate.FailingSince, now));
                flagged++;
            }
        }

        return flagged;
    }
}
