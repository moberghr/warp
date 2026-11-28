namespace Warp.Core.Webhooks;

/// <summary>
/// Core-visible shared identifiers for the webhooks feature. Lives in Core (unlike the addon's internal
/// <c>WebhookDefaults</c>) so query paths that must run in dashboard-only / publisher-only processes — which
/// never reference the executor addon — can still pin the adapter name. The addon's <c>WebhookDefaults</c>
/// references <see cref="AdapterName"/> so there is a single source of truth.
/// </summary>
public static class WebhookConstants
{
    /// <summary>
    /// The adapter name every webhook attempt is recorded under (<c>AdapterCallLog.AdapterName</c>). Used to
    /// lead the attempt-timeline query with the composite index's leading column instead of scanning on
    /// <c>CorrelationId</c> alone.
    /// </summary>
    public const string AdapterName = "warp-webhooks";
}
