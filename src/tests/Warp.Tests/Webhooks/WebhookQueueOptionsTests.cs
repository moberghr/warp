using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Webhooks;

namespace Warp.Tests.Webhooks;

/// <summary>
/// <c>AddWebhooks(w => w.Queue)</c>: this process's webhook queue, resolved after <c>WebhookSend.Queue</c> and before
/// <c>DefaultQueue</c> (the dispatcher side is <c>WebhookDispatcherValidationTestsBase</c>).
/// </summary>
[Trait("Category", "NoDb")]
public class WebhookQueueOptionsTests
{
    [TimedFact]
    public void AddWebhooks_NoQueue_LeavesTheProcessQueueUnset()
    {
        var services = NewServices();
        services.AddWarp<TestContext>(x => x.AddWebhooks());

        Resolve(services).Queue.ShouldBeNull();
    }

    [TimedFact]
    public void AddWebhooks_LaterCallWithoutAQueue_KeepsTheEarlierQueue()
    {
        var services = NewServices();
        services.AddWarp<TestContext>(x =>
        {
            x.AddWebhooks(w => w.Queue = "webhooks");
            x.AddWebhooks(w => w.TrackEndpointHealth = true);
        });

        Resolve(services).Queue.ShouldBe("webhooks");
    }

    [TimedTheory]
    [InlineData("")]
    [InlineData(" ")]
    public void AddWebhooks_BlankQueue_Throws(string queue)
    {
        var services = NewServices();

        var ex = Should.Throw<InvalidOperationException>(() => services.AddWarp<TestContext>(x => x.AddWebhooks(w => w.Queue = queue)));

        ex.Message.ShouldContain("w.Queue");
    }

    [TimedTheory]
    [InlineData("tenant-a", "webhooks", "tenant-a")]
    [InlineData(null, "webhooks", "webhooks")]
    [InlineData(null, null, "orders")]
    public void ResolveQueue_DeliveryQueueWinsOverTheProcessQueueWhichWinsOverDefaultQueue(string? deliveryQueue, string? processQueue, string expected)
    {
        // A row from before 8.2 carries no queue and takes the webhook queue of the process that enqueues its job.
        var behavior = new WebhookSendBehavior { Queue = processQueue };

        behavior.ResolveQueue(deliveryQueue, new WarpConfiguration { DefaultQueue = "orders" }).ShouldBe(expected);
    }

    private static ServiceCollection NewServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TestContext>(o => o.UseInMemoryDatabase($"wq-{Guid.NewGuid():N}"));

        return services;
    }

    private static WebhookSendBehavior Resolve(ServiceCollection services)
        => services.BuildServiceProvider().GetRequiredService<IOptions<WebhookSendBehavior>>().Value;
}
