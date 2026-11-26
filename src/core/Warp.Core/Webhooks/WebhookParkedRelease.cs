using Microsoft.EntityFrameworkCore;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;

namespace Warp.Core.Webhooks;

// Moves a Parked delivery back to Pending (8.1), shared by the endpoint monitor (server context) and the "Retry now"
// command (TContext). Guarded on Parked, so a delivery cancelled or released meanwhile is left alone. The caller
// stages the executor job in the same transaction. AttemptCount 0 is a full new attempt budget (a release after
// recovery); RetrySchedule.Count leaves exactly one attempt (a probe).
internal static class WebhookParkedRelease
{
    internal static async Task<bool> ReleaseAsync(DbContext context, Guid deliveryId, int attemptCount, DateTime now, DateTime expireAt, CancellationToken ct)
    {
        var released = await context.Set<WebhookDelivery>()
            .Where(x => x.Id == deliveryId)
            .Where(x => x.Status == WebhookDeliveryStatus.Parked)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.Status, WebhookDeliveryStatus.Pending)
                    .SetProperty(x => x.AttemptCount, attemptCount)
                    .SetProperty(x => x.NextAttemptAt, now)
                    .SetProperty(x => x.ExpireAt, expireAt),
                ct);

        return released == 1;
    }

    // One probe of a failing group: its oldest parked delivery back to Pending with exactly one attempt left. Returns
    // the delivery id to stage a job for, or null when the group has nothing parked (or it moved meanwhile). Shared by
    // the monitor's scheduled probes and "Retry now", so both probe the same way.
    internal static async Task<Guid?> ProbeOldestAsync(DbContext context, string groupName, DateTime now, DateTime expireAt, CancellationToken ct)
    {
        var oldest = await context.Set<WebhookDelivery>()
            .AsNoTracking()
            .Where(x => x.GroupName == groupName)
            .Where(x => x.Status == WebhookDeliveryStatus.Parked)
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Select(x =>
                new
                {
                    x.Id,
                    x.RetrySchedule,
                })
            .FirstOrDefaultAsync(ct);

        if (oldest is null)
        {
            return null;
        }

        return await ReleaseAsync(context, oldest.Id, oldest.RetrySchedule.Count, now, expireAt, ct) ? oldest.Id : null;
    }

    // Pushes a failing group's next scheduled probe one interval out.
    internal static Task<int> DeferNextProbeAsync(DbContext context, string groupName, DateTime nextProbeAt, CancellationToken ct)
        => context.Set<WebhookEndpoint>()
            .Where(x => x.Name == groupName)
            .Where(x => x.State == WebhookEndpointState.Failing)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextProbeAt, nextProbeAt), ct);
}
