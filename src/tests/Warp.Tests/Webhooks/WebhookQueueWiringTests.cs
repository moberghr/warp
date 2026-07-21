using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Adapters.Webhooks;
using Warp.Core;
using Warp.Worker;

namespace Warp.Tests.Webhooks;

/// <summary>
/// Shape coverage for the <c>AddWebhooks</c> queue wiring (CRITICAL-2): a server host that calls
/// <c>AddWebhooks</c> must poll the dedicated <c>warp:webhooks</c> queue or deliveries never drain. The
/// executor tests prove the runtime effect end-to-end; these prove the builder-shape contract directly and
/// fast (NoDb) across the three builder shapes.
/// </summary>
[Trait("Category", "NoDb")]
public class WebhookQueueWiringTests
{
    [TimedFact]
    public void AddWebhooks_OnServerBuilderWithWorker_AppendsWebhookQueueToDefaultGroup()
    {
        var builder = new WarpServerBuilder<TestContext>(new ServiceCollection()) { Queues = ["default"] };

        builder.AddWebhooks();

        builder.Queues.ShouldContain("warp:webhooks");
        builder.Queues.ShouldContain("default");
    }

    [TimedFact]
    public void AddWebhooks_WhenQueueAlreadyPresent_IsIdempotent()
    {
        var builder = new WarpServerBuilder<TestContext>(new ServiceCollection()) { Queues = ["default", "warp:webhooks"] };

        builder.AddWebhooks();

        builder.Queues.Count(x => string.Equals(x, "warp:webhooks", StringComparison.Ordinal)).ShouldBe(1);
    }

    [TimedFact]
    public void AddWebhooks_OnServiceOnlyServer_DoesNotWireQueue()
    {
        var builder = new WarpServerBuilder<TestContext>(new ServiceCollection());
        builder.DisableWorker();

        builder.AddWebhooks();

        builder.Queues.ShouldNotContain("warp:webhooks");
    }

    [TimedFact]
    public void AddWebhooks_OnPublisherOnlyBuilder_SkipsQueueWiringWithoutThrowing()
    {
        var builder = new WarpBuilder<TestContext>(new ServiceCollection());

        // AddWarp-only (no worker, no Queues property): the queue probe finds no server shape and skips.
        Should.NotThrow(() => builder.AddWebhooks());
    }

    [TimedFact]
    public void AddWebhooks_CalledTwice_Throws()
    {
        // A second AddWebhooks would double-register options-dependent services (exhausted handler,
        // custom signer) with conflicting configuration — rejected up front at registration time.
        var builder = new WarpBuilder<TestContext>(new ServiceCollection());
        builder.AddWebhooks();

        Should.Throw<InvalidOperationException>(() => builder.AddWebhooks());
    }
}
