using Warp.Core.Enums;

namespace Warp.Core.Data.Entities;

/// <summary>
/// Health of one webhook endpoint (8.1). An endpoint is a delivery <c>GroupName</c>: the host decides what a group
/// is (one subscription, one customer's URL), and one group's success never marks another healthy, even when they
/// share a URL. Only deliveries sent with endpoint health tracking (and a group) touch this table; the row is
/// created the first time one of them runs out of retries. Parked deliveries are counted by query, not stored here.
/// </summary>
public class WebhookEndpoint
{
    /// <summary>The delivery group name (primary key).</summary>
    public string Name { get; set; } = string.Empty;

    public WebhookEndpointState State { get; set; } = WebhookEndpointState.Healthy;

    /// <summary>When the current failing period began; null while healthy.</summary>
    public DateTime? FailingSince { get; set; }

    /// <summary>The last time a tracked delivery to this endpoint succeeded, once the row exists.</summary>
    public DateTime? LastSuccessAt { get; set; }

    /// <summary>The last time a tracked delivery attempt to this endpoint failed.</summary>
    public DateTime? LastFailureAt { get; set; }

    /// <summary>When the endpoint monitor next probes this failing endpoint; null while healthy.</summary>
    public DateTime? NextProbeAt { get; set; }

    /// <summary>When the "still failing" alert was raised for the current failing period; cleared on recovery.</summary>
    public DateTime? StillFailingNotifiedAt { get; set; }

    public DateTime CreatedAt { get; set; }
}
