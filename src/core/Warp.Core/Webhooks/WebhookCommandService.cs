using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Logging;

namespace Warp.Core.Webhooks;

/// <summary>
/// <see cref="IWebhookCommandService"/> over the user's <typeparamref name="TContext"/>. Applies the status
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
    private readonly TContext _context;
    private readonly TimeProvider _timeProvider;
    private readonly WarpConfiguration _configuration;
    private readonly IEnumerable<IWebhookRedeliveryEnqueuer> _redeliveryEnqueuers;

    public WebhookCommandService(
        TContext context,
        TimeProvider timeProvider,
        IOptions<WarpConfiguration> configuration,
        IEnumerable<IWebhookRedeliveryEnqueuer> redeliveryEnqueuers)
    {
        _context = context;
        _timeProvider = timeProvider;
        _configuration = configuration.Value;
        _redeliveryEnqueuers = redeliveryEnqueuers;
    }

    public async Task<WebhookRedeliveryResult> Redeliver(Guid deliveryId, CancellationToken ct = default)
    {
        var status = await _context.Set<WebhookDelivery>()
            .AsNoTracking()
            .Where(x => x.Id == deliveryId)
            .Select(x => (WebhookDeliveryStatus?)x.Status)
            .FirstOrDefaultAsync(ct);

        if (status is null)
        {
            return WebhookRedeliveryResult.NotFound;
        }

        // A Pending delivery already has a live executor job — requeuing would double-attempt. Only settled
        // (Delivered / Exhausted) deliveries are redeliverable; reject without side effects.
        if (status == WebhookDeliveryStatus.Pending)
        {
            return WebhookRedeliveryResult.Rejected;
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
        // racing redelivers flips the row (rowcount 1) and the other matches zero rows.
        var updated = await _context.Set<WebhookDelivery>()
            .Where(x => x.Id == deliveryId)
            .Where(x => x.Status != WebhookDeliveryStatus.Pending)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.Status, WebhookDeliveryStatus.Pending)
                    .SetProperty(x => x.AttemptCount, 0)
                    .SetProperty(x => x.NextAttemptAt, now)
                    .SetProperty(x => x.ExpireAt, expireAt),
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
        await enqueuer.EnqueueAsync(deliveryId, ct);
        await transaction.CommitAsync(ct);

        WarpTelemetry.WebhookRedeliveries.Add(1);

        return WebhookRedeliveryResult.Enqueued;
    }
}

/// <summary>
/// Seam that enqueues the webhook executor job for a redelivery. The executor job type lives in the
/// webhooks addon (<c>Warp.Adapters.Webhooks</c>) because it needs <c>IHttpClientFactory</c> — the same
/// dependency line that keeps HTTP out of Core — so Core cannot construct or enqueue it directly. The
/// addon registers the implementation inside <c>AddWebhooks()</c>; <see cref="WebhookCommandService{TContext}"/>
/// drives it after applying the redeliver state reset.
/// <para>
/// Registered <b>only</b> by <c>AddWebhooks()</c>, so its presence doubles as the dashboard "webhooks"
/// addon marker (the pattern <c>IWarpAdapters</c> follows for adapters): <c>GET /api/addons</c> reports
/// the <c>webhooks</c> flag from whether this service resolves, gating the dashboard nav.
/// </para>
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
