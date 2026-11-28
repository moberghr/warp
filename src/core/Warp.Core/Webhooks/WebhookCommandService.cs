using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Events;
using Warp.Core.Logging;
using Warp.Core.Notifications;

namespace Warp.Core.Webhooks;

/// <summary>
/// <see cref="IWebhookCommandService"/> over Warp's own context (registered by <c>AddWarp</c>; the
/// <typeparamref name="TContext"/> constructor runs it on the given application context). Applies the status
/// guard, then flips the settled delivery back to <c>Pending</c> (fresh attempt budget, immediate
/// <c>NextAttemptAt</c>, refreshed <c>ExpireAt</c>) and enqueues the executor job through the
/// <see cref="IWebhookRedeliveryEnqueuer"/> seam. The executor job type lives in the webhooks addon (which
/// owns the <c>IHttpClientFactory</c> dependency line Core cannot take), so Core cannot construct it
/// directly — the addon registers the enqueuer and Core drives it.
/// <para>
/// The settled→<c>Pending</c> flip is a single guarded <c>ExecuteUpdate</c> inside an explicit transaction
/// (§1.3), and the enqueue commits in the same transaction, so (a) two concurrent redelivers on one settled
/// delivery enqueue exactly one job — the second's guarded update matches zero rows — and (b) a redelivered
/// row is never left <c>Pending</c> without a live executor job.
/// </para>
/// <para>
/// In a process that never called <c>AddWebhooks()</c> (dashboard-only / publisher-only) the enqueuer is
/// absent. There is no worker there to run an executor job and nothing scans <c>NextAttemptAt</c>, so a
/// <c>Pending</c> reset would strand the delivery forever. The reset is therefore <b>not</b> applied and the
/// call returns <see cref="WebhookRedeliveryResult.Unavailable"/>.
/// </para>
/// </summary>
public class WebhookCommandService<TContext> : IWebhookCommandService
    where TContext : DbContext
{
    private readonly DbContext _context;
    private readonly TimeProvider _timeProvider;
    private readonly WarpConfiguration _configuration;
    private readonly IEnumerable<IWebhookRedeliveryEnqueuer> _redeliveryEnqueuers;
    private readonly ServerTaskSignals<TContext>? _signals;
    private readonly IWarpNotificationTransport? _notificationTransport;
    private readonly WebhookSendBehavior _behavior;

    public WebhookCommandService(
        TContext context,
        TimeProvider timeProvider,
        IOptions<WarpConfiguration> configuration,
        IEnumerable<IWebhookRedeliveryEnqueuer> redeliveryEnqueuers)
        : this((DbContext)context, timeProvider, configuration, redeliveryEnqueuers, signals: null, notificationTransport: null, behavior: null)
    {
    }

    // The Warp context (rule 2.14): AddWarp passes IWarpContext.Context, which is not a TContext. With the signals
    // and the transport, Warp's own enqueuer stages the executor job on this context instead (StageOrEnqueueAsync).
    internal WebhookCommandService(
        DbContext context,
        TimeProvider timeProvider,
        IOptions<WarpConfiguration> configuration,
        IEnumerable<IWebhookRedeliveryEnqueuer> redeliveryEnqueuers,
        ServerTaskSignals<TContext>? signals,
        IWarpNotificationTransport? notificationTransport,
        IOptions<WebhookSendBehavior>? behavior)
    {
        _context = context;
        _timeProvider = timeProvider;
        _configuration = configuration.Value;
        _redeliveryEnqueuers = redeliveryEnqueuers;
        _signals = signals;
        _notificationTransport = notificationTransport;
        _behavior = behavior?.Value ?? new WebhookSendBehavior();
    }

    public Task<WebhookRedeliveryResult> Redeliver(Guid deliveryId, CancellationToken ct = default)
        => RedeliverCoreAsync(deliveryId, newSecret: null, ct);

    public Task<WebhookRedeliveryResult> Redeliver(Guid deliveryId, WebhookRedeliveryOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Secret is { Length: > WebhookColumnCaps.Secret })
        {
            throw new ArgumentException(
                $"WebhookRedeliveryOptions.Secret exceeds the {WebhookColumnCaps.Secret}-character limit.",
                nameof(options));
        }

        return RedeliverCoreAsync(deliveryId, options.Secret, ct);
    }

    public async Task<WebhookCancelResult> Cancel(Guid deliveryId, CancellationToken ct = default)
    {
        var cancelled = await CancelWhere(_context.Set<WebhookDelivery>().Where(x => x.Id == deliveryId), ct);
        if (cancelled == 1)
        {
            return WebhookCancelResult.Cancelled;
        }

        var exists = await _context.Set<WebhookDelivery>()
            .AsNoTracking()
            .Where(x => x.Id == deliveryId)
            .AnyAsync(ct);

        return exists ? WebhookCancelResult.Rejected : WebhookCancelResult.NotFound;
    }

    public Task<int> CancelGroup(string groupName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);

        // Same identity as the endpoint groups view and the list filter: the group name, or the URL of deliveries
        // sent without a group.
        return CancelWhere(_context.Set<WebhookDelivery>().Where(x => (x.GroupName ?? x.Url) == groupName), ct);
    }

    public async Task<WebhookProbeResult> ProbeEndpoint(string groupName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);

        var state = await _context.Set<WebhookEndpoint>()
            .AsNoTracking()
            .Where(x => x.Name == groupName)
            .Select(x => (WebhookEndpointState?)x.State)
            .FirstOrDefaultAsync(ct);

        if (state is null)
        {
            return WebhookProbeResult.NotFound;
        }

        if (state == WebhookEndpointState.Healthy)
        {
            return WebhookProbeResult.NotFailing;
        }

        // Same reason as Redeliver: without an enqueuer the released delivery would be Pending with no job.
        var enqueuer = _redeliveryEnqueuers.FirstOrDefault();
        if (enqueuer is null)
        {
            return WebhookProbeResult.Unavailable;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        await using var transaction = await _context.Database.BeginTransactionAsync(ct);

        // One attempt left: on failure the delivery runs out of retries again and, the endpoint still failing, parks.
        var probed = await WebhookParkedRelease.ProbeOldestAsync(_context, groupName, now, now + _configuration.WebhookDeliveryRetention, ct);
        if (probed is not { } delivery)
        {
            await transaction.RollbackAsync(ct);

            return WebhookProbeResult.NothingParked;
        }

        await WebhookParkedRelease.DeferNextProbeAsync(_context, groupName, now + _configuration.WebhookEndpointProbeInterval, ct);
        var wake = await StageOrEnqueueAsync(enqueuer, delivery.Id, delivery.Queue, now, "endpoint probe", ct);
        await transaction.CommitAsync(ct);
        await WakeAsync(wake);

        return WebhookProbeResult.Probing;
    }

    // Pending or Parked -> Cancelled in one guarded statement: a row that settled in the meantime is left alone.
    private static Task<int> CancelWhere(IQueryable<WebhookDelivery> deliveries, CancellationToken ct)
        => deliveries
            .Where(x => x.Status == WebhookDeliveryStatus.Pending || x.Status == WebhookDeliveryStatus.Parked)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.Status, WebhookDeliveryStatus.Cancelled)
                    .SetProperty(x => x.NextAttemptAt, (DateTime?)null),
                ct);

    private async Task<WebhookRedeliveryResult> RedeliverCoreAsync(Guid deliveryId, string? newSecret, CancellationToken ct)
    {
        var current = await _context.Set<WebhookDelivery>()
            .AsNoTracking()
            .Where(x => x.Id == deliveryId)
            .Select(x =>
                new
                {
                    x.Status,
                    x.SigningMode,
                    x.Queue,
                })
            .FirstOrDefaultAsync(ct);

        if (current is null)
        {
            return WebhookRedeliveryResult.NotFound;
        }

        // A Pending delivery already has a live executor job — requeuing would double-attempt. Only settled
        // (Delivered / Exhausted) deliveries are redeliverable; reject without side effects.
        if (current.Status == WebhookDeliveryStatus.Pending)
        {
            return WebhookRedeliveryResult.Rejected;
        }

        if (newSecret is not null)
        {
            ValidateNewSecret(newSecret, current.SigningMode, nameof(WebhookRedeliveryOptions));
        }

        // No worker in this process to run an executor job, and nothing scans NextAttemptAt: a Pending reset
        // here would strand the delivery. Reject without mutating — redeliver from a server host instead.
        var enqueuer = _redeliveryEnqueuers.FirstOrDefault();
        if (enqueuer is null)
        {
            return WebhookRedeliveryResult.Unavailable;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var expireAt = now + _configuration.WebhookDeliveryRetention;

        await using var transaction = await _context.Database.BeginTransactionAsync(ct);

        // Guarded settled→Pending transition: the status guard lives in the WHERE, so exactly one of two
        // racing redelivers flips the row (rowcount 1) and the other matches zero rows. A rotated secret is
        // written by the same statement, so the row is never Pending with the old key or settled with the new.
        var updated = await _context.Set<WebhookDelivery>()
            .Where(x => x.Id == deliveryId)
            .Where(x => x.Status != WebhookDeliveryStatus.Pending)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.Status, WebhookDeliveryStatus.Pending)
                    .SetProperty(x => x.AttemptCount, 0)
                    .SetProperty(x => x.NextAttemptAt, now)
                    .SetProperty(x => x.ExpireAt, expireAt)
                    .SetProperty(x => x.Secret, x => newSecret ?? x.Secret),
                ct);

        if (updated == 0)
        {
            // A concurrent redeliver already flipped the row and owns the live executor job.
            await transaction.RollbackAsync(ct);

            return WebhookRedeliveryResult.Rejected;
        }

        // Refresh the existing attempt rows (AdapterCallLog keyed by CorrelationId = delivery id) to the same
        // ExpireAt in the same transaction. Otherwise the redelivered delivery outlives its old attempt rows,
        // which keep their original ExpireAt and are swept early — leaving a truncated attempt timeline.
        var correlationId = deliveryId.ToString();
        await _context.Set<AdapterCallLog>()
            .Where(x => x.AdapterName == WebhookConstants.AdapterName)
            .Where(x => x.CorrelationId == correlationId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpireAt, expireAt), ct);

        // Stages the executor job and commits it in the same transaction as the flip, so the delivery is
        // never left Pending without a live executor job.
        var wake = await StageOrEnqueueAsync(enqueuer, deliveryId, current.Queue, now, "redelivery", ct);
        await transaction.CommitAsync(ct);
        await WakeAsync(wake);

        WarpTelemetry.WebhookRedeliveries.Add(1);

        return WebhookRedeliveryResult.Enqueued;
    }

    // The enqueue must commit in the caller's transaction (see the class remarks). Warp's own enqueuer publishes on
    // TContext, which is another connection when this service runs on the Warp context (#344): the job could commit
    // without the flip, or run before it. So on the Warp context Warp's enqueuer is bypassed and the executor job is
    // staged here, on this context, like the server tasks do; its wake is returned to fire after the commit (§6.3).
    // A host's own enqueuer is still called: it decides where its job is staged.
    private async Task<IReadOnlyList<Notification>> StageOrEnqueueAsync(
        IWebhookRedeliveryEnqueuer enqueuer,
        Guid deliveryId,
        string? deliveryQueue,
        DateTime now,
        string createdBy,
        CancellationToken ct)
    {
        if (_signals is null || enqueuer is not WebhookRedeliveryEnqueuer<TContext>)
        {
            await enqueuer.EnqueueAsync(deliveryId, ct);

            return [];
        }

        var callerTrace = Activity.Current?.TraceId;
        WebhookExecutorJobs.Stage(
            _context,
            deliveryId,
            _behavior.ResolveQueue(deliveryQueue, _configuration),
            now,
            now,
            _configuration.ApplicationName,
            createdBy,
            callerTrace is { } trace ? new Guid(trace.ToHexString()) : null);

        var wake = NotificationDispatch.CapturePending(_context);
        await _context.SaveChangesAsync(ct);

        return wake;
    }

    // After the commit, never with the caller's token: the job is committed and must not wait out a backoff.
    private async Task WakeAsync(IReadOnlyList<Notification> wake)
    {
        if (wake.Count == 0 || _signals is null || _notificationTransport is null)
        {
            return;
        }

        await NotificationDispatch.DispatchAsync(wake, _signals, _notificationTransport, CancellationToken.None);
    }

    // Validated before anything is written: the attempt reads the secret only at send time, so a bad value
    // would otherwise surface as failed attempts long after this call returned Enqueued.
    private static void ValidateNewSecret(string secret, WebhookSigning signingMode, string paramName)
    {
        if (signingMode == WebhookSigning.None)
        {
            throw new ArgumentException(
                "The delivery is not signed (WebhookSigning.None), so a new secret would never be used.",
                paramName);
        }

        if (signingMode == WebhookSigning.StandardWebhooks)
        {
            WebhookSecretValidation.ValidateStandardWebhooksSecret(secret, paramName);
        }
    }
}

/// <summary>
/// Seam that enqueues the webhook executor job for a redelivery, keeping the enqueue behind an interface so
/// <see cref="WebhookCommandService{TContext}"/> drives it after applying the redeliver state reset (both
/// commit on the same scoped context in one <c>SaveChanges</c>). The implementation and the executor job
/// both live in <c>Warp.Core.Webhooks</c> and are registered unconditionally by <c>AddWarp</c>, so
/// redelivery works in any process — the webhooks engine is part of Core (§8.20), not an opt-in addon.
/// </summary>
public interface IWebhookRedeliveryEnqueuer
{
    /// <summary>
    /// Stages an executor job for the delivery on the webhooks queue and commits it together with any
    /// pending changes on the shared scoped context (outbox). The caller runs it inside an explicit
    /// transaction that also carries the settled→Pending flip, so the reset and the new job land together.
    /// </summary>
    Task EnqueueAsync(Guid deliveryId, CancellationToken ct = default);
}
