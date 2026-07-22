namespace Warp.Core.Webhooks;

/// <summary>Shared identifiers and defaults for the webhooks feature.</summary>
internal static class WebhookDefaults
{
    /// <summary>The dedicated queue the executor jobs run on.</summary>
    internal const string Queue = WebhookConstants.Queue;

    /// <summary>The auto-registered adapter every attempt is recorded under.</summary>
    internal const string AdapterName = WebhookConstants.AdapterName;

    /// <summary>Library built-in retry schedule when a send does not specify one.</summary>
    internal static readonly IReadOnlyList<TimeSpan> RetrySchedule =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(6),
    ];
}

/// <summary>
/// Persisted string-column length caps mirrored from the <c>WebhookDelivery</c> EF configuration. The
/// dispatcher (the single build choke point) clamps caller input to these before insert so an over-long
/// value never fails the row write.
/// </summary>
internal static class WebhookColumnCaps
{
    internal const int EventType = 200;
    internal const int EventId = 200;
    internal const int Url = 2048;
    internal const int GroupName = 200;
    internal const int Reference = 200;
    internal const int Secret = 512;
}
