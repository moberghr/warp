using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Webhooks;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Webhooks;

/// <summary>
/// Webhook attempts run on their own bounded worker group (8.1, <c>WebhookWorkerCount</c>): a receiver that hangs holds
/// webhook workers only, so jobs keep running, and no more attempts run at once than the group has workers.
/// </summary>
[GenerateDatabaseTests]
public abstract class WebhookWorkerIsolationTestsBase : IntegrationTestBase
{
    protected WebhookWorkerIsolationTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task HangingReceiver_HoldsOnlyTheWebhookWorkers_JobsKeepRunning()
    {
        var signal = new BarrierSignal();
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg =>
            {
                cfg.Queues = ["default"];
                cfg.WebhookWorkerCount = 1;
            },
            configureServices: services =>
                services.AddHttpClient(WebhookConstants.AdapterName)
                    .ConfigurePrimaryHttpMessageHandler(() => new PinnedReceiver(signal, HttpStatusCode.OK)));

        var first = await SendAsync(server);
        var second = await SendAsync(server);

        // The single webhook worker is now inside the first attempt, held at the receiver.
        (await signal.Running.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue();

        // A job still runs: the default group never polls warp:webhooks.
        var publisher = server.CreatePublisher();
        await publisher.Enqueue(new CounterRequest());
        await publisher.SaveChangesAsync(Ct);
        await WarpTestServer.WaitUntil(
            async () => await server.CreateContext().Set<Job>()
                .Where(x => x.Queue == "default")
                .Where(x => x.CurrentState == State.Completed)
                .AnyAsync(Ct),
            timeout: TimeSpan.FromSeconds(5),
            ct: Ct);

        // And the second delivery waits for the one webhook worker instead of taking a job worker.
        (await GetStatusAsync(second)).ShouldBe(WebhookDeliveryStatus.Pending);
        (await server.CreateContext().Set<Job>()
            .Where(x => x.Queue == WebhookConstants.Queue)
            .Where(x => x.CurrentState == State.Processing)
            .CountAsync(Ct)).ShouldBe(1);

        signal.CanFinish.Release(2);

        await WaitForDeliveredAsync(first);
        await WaitForDeliveredAsync(second);
    }

    private static async Task<Guid> SendAsync(WarpTestServer server)
    {
        using var scope = server.GetService<IServiceScopeFactory>().CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IWebhookDispatcher>().SendAsync(
            new WebhookSend
            {
                Url = "https://example.test/hook",
                EventType = "order.created",
                RetrySchedule = [],
            },
            Ct);
    }

    private async Task<WebhookDeliveryStatus> GetStatusAsync(Guid id)
        => await Fixture.CreateContext().Set<WebhookDelivery>()
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => x.Status)
            .FirstAsync(Ct);

    private Task WaitForDeliveredAsync(Guid id)
        => WarpTestServer.WaitUntil(
            async () => await GetStatusAsync(id) == WebhookDeliveryStatus.Delivered,
            timeout: TimeSpan.FromSeconds(5),
            ct: Ct);
}
