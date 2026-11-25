namespace Warp.Core.Webhooks;

// Registration-time checks for the Core webhook settings on WarpConfiguration. Called by AddWarp and
// AddWarpServer after the two-builder merge (§2.5), so the check sees the value the runtime will use.
internal static class WebhookConfigurationValidation
{
    internal static void Validate(WarpConfiguration configuration)
    {
        var timeout = configuration.WebhookAttemptTimeout;

        // CancellationTokenSource works in whole milliseconds: below 1 ms it cancels at once, and it accepts at
        // most uint.MaxValue - 1 ms (about 49 days).
        var outOfRange = timeout != System.Threading.Timeout.InfiniteTimeSpan
            && (timeout < TimeSpan.FromMilliseconds(1) || timeout.TotalMilliseconds > uint.MaxValue - 1);

        if (outOfRange)
        {
            throw new InvalidOperationException(
                $"WebhookAttemptTimeout must be at least 1 ms and at most 49 days (was {timeout}); use Timeout.InfiniteTimeSpan "
                + "to disable the per-attempt timeout.");
        }
    }
}
