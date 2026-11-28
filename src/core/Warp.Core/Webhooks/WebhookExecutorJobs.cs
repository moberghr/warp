using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Helper;
using Warp.Core.NoRestart;

namespace Warp.Core.Webhooks;

// Stages an ExecuteWebhookDelivery job directly on one of Warp's own contexts (§2.14, §8.20), never through the
// TContext-bound IPublisher. Used by the stuck-delivery sweep (StaleJobRecovery), the endpoint monitor, and a
// redelivery or probe from WebhookCommandService on the Warp context, whose transaction the job must commit in.
// The caller captures the wake (NotificationDispatch.CapturePending) and saves in its own transaction.
internal static class WebhookExecutorJobs
{
    // [Restart] on ExecuteWebhookDelivery is applied by NoRestartPublishBehavior, which only runs when the host
    // called AddNoRestart() and is bypassed entirely by staging a job directly. Stamped on the staged job so an
    // executor that itself crashes mid-attempt is always re-run: the delivery-completes guarantee must not depend
    // on an addon being registered or on RestartStaleJobsByDefault (§8.20).
    private static readonly string? RestartMetadata = BuildRestartMetadata();

    // queue is the delivery's resolved queue (WebhookSendBehavior.ResolveQueue): a delivery stays on its queue for life.
    internal static void Stage(DbContext context, Guid deliveryId, string queue, DateTime scheduleTime, DateTime now, string? application, string createdBy, Guid? traceId = null)
    {
        var job = JobHelper.CreateJob(
            message: JsonSerializer.Serialize(new ExecuteWebhookDelivery { DeliveryId = deliveryId }),
            type: typeof(ExecuteWebhookDelivery).AssemblyQualifiedName!,
            scheduleTime: scheduleTime,
            queue: queue,
            parentId: null,
            state: State.Enqueued,
            now: now,
            metadata: RestartMetadata,
            application: application);

        // A server task's work has no caller trace to inherit, so the job roots a fresh trace — the same fallback
        // Publisher lands on from here (mirrors RecurringJobScheduler). A dashboard redelivery passes its caller's.
        job.TraceId = traceId ?? job.Id;

        context.Set<Job>().Add(job);
        context.Set<JobLog>().Add(new JobLog
        {
            JobId = job.Id,
            EventType = "Created",
            Timestamp = now,
            Level = "Information",
            Message = $"Job {job.Id} created by {createdBy} for webhook delivery {deliveryId}",
        });
    }

    private static string? BuildRestartMetadata()
    {
        var metadata = MetadataFactory.Create<ICanBeRestartedMetadata>([]);
        metadata.CanBeRestarted = true;

        return MetadataSerializer.Serialize((Dictionary<string, object>)(object)metadata);
    }
}
