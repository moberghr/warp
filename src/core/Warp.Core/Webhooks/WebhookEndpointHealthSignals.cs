using Microsoft.Extensions.Logging;
using Warp.Core.Enums;
using Warp.Core.Logging;
using Warp.Core.Notifiers;

namespace Warp.Core.Webhooks;

// One endpoint health change to announce after its transition commits (8.1).
internal sealed record PendingEndpointHealthSignal(WarpEventType Type, string GroupName, DateTime? FailingSince, DateTime OccurredAt);

// Raises an endpoint health change to the host's IWebhookEndpointHealthHandler set and to the IWarpNotifier
// fan-out, the same way from every site (executor, endpoint monitor, command service). Always called after the
// transition committed; a throwing handler is logged and never propagated (§8.25).
internal static class WebhookEndpointHealthSignals
{
    internal static async Task RaiseAsync(
        PendingEndpointHealthSignal signal,
        IEnumerable<IWebhookEndpointHealthHandler> handlers,
        WarpNotifierDispatcher notifier,
        ILogger logger)
    {
        var change = new WebhookEndpointHealthChange
        {
            GroupName = signal.GroupName,
            FailingSince = signal.FailingSince,
            OccurredAt = signal.OccurredAt,
        };

        foreach (var handler in handlers)
        {
            try
            {
                var task = signal.Type switch
                {
                    WarpEventType.WebhookEndpointFailing => handler.OnEndpointFailingAsync(change, CancellationToken.None),
                    WarpEventType.WebhookEndpointRecovered => handler.OnEndpointRecoveredAsync(change, CancellationToken.None),
                    _ => handler.OnEndpointStillFailingAsync(change, CancellationToken.None),
                };

                await task;
            }
#pragma warning disable CA1031 // host callback: a throwing handler is logged and never propagated.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                logger.LogWarning(ex, "IWebhookEndpointHealthHandler threw for endpoint {GroupName} ({EventType}).", signal.GroupName, signal.Type);
            }
        }

        // CancellationToken.None: the transition is committed; a shutdown must not abandon its alert.
        await notifier.DispatchAsync(
            new WebhookEndpointHealthEvent
            {
                Type = signal.Type,
                Severity = signal.Type == WarpEventType.WebhookEndpointRecovered ? WarpEventSeverity.Info : WarpEventSeverity.Warning,
                TimestampUtc = signal.OccurredAt,
                MachineName = Environment.MachineName,
                Application = WarpTelemetry.ApplicationName,
                Message = Describe(signal),
                GroupName = signal.GroupName,
                FailingSince = signal.FailingSince,
            },
            CancellationToken.None);
    }

    private static string Describe(PendingEndpointHealthSignal signal) => signal.Type switch
    {
        WarpEventType.WebhookEndpointFailing => $"Webhook endpoint {signal.GroupName} is failing: a delivery exhausted its retries.",
        WarpEventType.WebhookEndpointRecovered => $"Webhook endpoint {signal.GroupName} recovered; its parked deliveries are being released.",
        _ => $"Webhook endpoint {signal.GroupName} has been failing since {signal.FailingSince:u}.",
    };
}
