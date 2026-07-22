using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Worker;

namespace Warp.Tests.Webhooks;

/// <summary>
/// Shape coverage for the webhook queue wiring. Webhook delivery is a Core feature (§8.20), so the
/// implicit default worker group subscribes to the dedicated <c>warp:webhooks</c> queue
/// <b>unconditionally</b> — with no <c>AddWebhooks</c> opt-in. This is what closes the two-process
/// footgun: any server with a worker drains deliveries staged by any process. The executor tests prove
/// the runtime effect end-to-end; these prove the builder-shape contract directly and fast (NoDb).
/// </summary>
[Trait("Category", "NoDb")]
public class WebhookQueueWiringTests
{
    [TimedFact]
    public void EffectiveWorkerGroups_WithoutAddWebhooks_DefaultGroupPollsWebhookQueue()
    {
        // No AddWebhooks call — a plain server still drains warp:webhooks.
        var builder = new WarpServerBuilder<TestContext>(new ServiceCollection()) { Queues = ["default"] };

        var groups = builder.GetEffectiveWorkerGroups();

        groups[0].Queues.ShouldContain("warp:webhooks");
        groups[0].Queues.ShouldContain("default");
    }

    [TimedFact]
    public void EffectiveWorkerGroups_WhenQueueAlreadyListed_IsDeduped()
    {
        var builder = new WarpServerBuilder<TestContext>(new ServiceCollection()) { Queues = ["default", "warp:webhooks"] };

        var groups = builder.GetEffectiveWorkerGroups();

        groups[0].Queues.Count(x => string.Equals(x, "warp:webhooks", StringComparison.Ordinal)).ShouldBe(1);
    }

    [TimedFact]
    public void EffectiveWorkerGroups_DoesNotMutateConfiguredQueues()
    {
        // The webhook queue is added to the effective group only — the caller's configured Queues stay as-is.
        var builder = new WarpServerBuilder<TestContext>(new ServiceCollection()) { Queues = ["default"] };

        _ = builder.GetEffectiveWorkerGroups();

        builder.Queues.ShouldBe(["default"]);
    }
}
