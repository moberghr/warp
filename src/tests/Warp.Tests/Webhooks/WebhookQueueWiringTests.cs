using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Worker;

namespace Warp.Tests.Webhooks;

/// <summary>
/// Shape coverage for the webhook queue wiring. Webhook delivery is a Core feature (§8.20), so every server with a
/// worker drains <c>warp:webhooks</c> <b>unconditionally</b>, with no <c>AddWebhooks</c> opt-in. Since 8.1 it does so
/// through a dedicated, bounded worker group (<c>WebhookWorkerCount</c>, default 4) so slow receivers cannot take the
/// job workers; <c>0</c> restores the shared shape, and explicit routing wins. The executor tests prove the runtime
/// effect end-to-end; these prove the builder-shape contract directly and fast (NoDb).
/// </summary>
[Trait("Category", "NoDb")]
public class WebhookQueueWiringTests
{
    [TimedFact]
    public void EffectiveWorkerGroups_ByDefault_AddsADedicatedWebhookGroupAndKeepsItOutOfTheDefaultGroup()
    {
        var builder = new WarpServerBuilder<TestContext>(new ServiceCollection()) { Queues = ["default"], WorkerCount = 10 };

        var groups = builder.GetEffectiveWorkerGroups();

        groups.Count.ShouldBe(2);
        groups[0].Queues.ShouldBe(["default"]);
        groups[0].WorkerCount.ShouldBe(10);
        groups[1].Queues.ShouldBe(["warp:webhooks"]);
        groups[1].WorkerCount.ShouldBe(4);
        groups[1].PrefetchCount.ShouldBe(0);
    }

    [TimedFact]
    public void EffectiveWorkerGroups_DedicatedGroup_FollowsTheTopLevelPollingSettings()
    {
        var builder = new WarpServerBuilder<TestContext>(new ServiceCollection())
        {
            WebhookWorkerCount = 2,
            PollingInterval = TimeSpan.FromSeconds(3),
            MaxPollingInterval = TimeSpan.FromSeconds(40),
            PollingIntervalFactor = 1.5,
        };

        var webhooks = builder.GetEffectiveWorkerGroups().Single(x => x.Queues.Contains("warp:webhooks"));

        webhooks.WorkerCount.ShouldBe(2);
        webhooks.PollingInterval.ShouldBe(TimeSpan.FromSeconds(3));
        webhooks.MaxPollingInterval.ShouldBe(TimeSpan.FromSeconds(40));
        webhooks.PollingIntervalFactor.ShouldBe(1.5);
    }

    [TimedFact]
    public void EffectiveWorkerGroups_WebhookWorkerCountZero_DefaultGroupDrainsTheWebhookQueue()
    {
        // The pre-8.1 shape: no dedicated group, the default group polls warp:webhooks alongside its queues.
        var builder = new WarpServerBuilder<TestContext>(new ServiceCollection()) { Queues = ["default"], WebhookWorkerCount = 0 };

        var groups = builder.GetEffectiveWorkerGroups();

        groups.ShouldHaveSingleItem().Queues.ShouldBe(["default", "warp:webhooks"]);
    }

    [TimedFact]
    public void EffectiveWorkerGroups_QueuesListTheWebhookQueue_ExplicitRoutingWinsAndStaysSingle()
    {
        var builder = new WarpServerBuilder<TestContext>(new ServiceCollection()) { Queues = ["default", "warp:webhooks"] };

        var groups = builder.GetEffectiveWorkerGroups();

        groups.ShouldHaveSingleItem().Queues.Count(x => string.Equals(x, "warp:webhooks", StringComparison.Ordinal)).ShouldBe(1);
    }

    [TimedFact]
    public void EffectiveWorkerGroups_AnExplicitGroupListsTheWebhookQueue_NoDedicatedGroupIsAdded()
    {
        var builder = new WarpServerBuilder<TestContext>(new ServiceCollection()) { Queues = ["default"] };
        builder.AddWorkerGroup(x =>
        {
            x.WorkerCount = 8;
            x.Queues = ["warp:webhooks"];
        });

        var groups = builder.GetEffectiveWorkerGroups();

        groups.Count.ShouldBe(2);
        groups[0].Queues.ShouldBe(["default"]);
        groups[1].WorkerCount.ShouldBe(8);
    }

    [TimedFact]
    public void EffectiveWorkerGroups_AZeroWorkerGroupListsTheWebhookQueue_DoesNotStrandIt()
    {
        // A group with no workers drains nothing, so it does not count as routing warp:webhooks.
        var builder = new WarpServerBuilder<TestContext>(new ServiceCollection()) { Queues = ["default"] };
        builder.AddWorkerGroup(x =>
        {
            x.WorkerCount = 0;
            x.Queues = ["warp:webhooks"];
        });

        var groups = builder.GetEffectiveWorkerGroups();

        groups.ShouldContain(x => x.WorkerCount > 0 && x.Queues.Contains("warp:webhooks"));
    }

    [TimedFact]
    public void TotalWorkerCount_DoesNotCountTheDedicatedWebhookGroup()
    {
        // The "worker enabled but zero workers" validation is about JOB capacity: webhook workers alone would leave
        // every job Enqueued forever.
        var builder = new WarpServerBuilder<TestContext>(new ServiceCollection()) { WorkerCount = 0 };

        builder.TotalWorkerCount.ShouldBe(0);
    }

    [TimedFact]
    public void AddWarpServer_NegativeWebhookWorkerCount_Throws()
    {
        var services = new ServiceCollection();

        var ex = Should.Throw<InvalidOperationException>(() =>
            services.AddWarpServer<TestContext>(x => x.WebhookWorkerCount = -1));

        ex.Message.ShouldContain("WebhookWorkerCount");
    }

    [TimedFact]
    public void EffectiveWorkerGroups_DoesNotMutateConfiguredQueues()
    {
        var builder = new WarpServerBuilder<TestContext>(new ServiceCollection()) { Queues = ["default"], WebhookWorkerCount = 0 };

        _ = builder.GetEffectiveWorkerGroups();

        builder.Queues.ShouldBe(["default"]);
    }
}
