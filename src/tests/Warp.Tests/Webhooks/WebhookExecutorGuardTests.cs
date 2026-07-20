using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Warp.Adapters.Webhooks;

namespace Warp.Tests.Webhooks;

/// <summary>
/// Guard coverage for the webhook executor handler (BUG-2), NoDb. The executor job must ALWAYS complete:
/// the WHOLE <c>HandleAsync</c> body — including the initial delivery read — is guarded, so a transient DB
/// fault on that read is logged and the job completes instead of surfacing a <c>Failed</c> job in the Jobs
/// UI (and, with a host-level <c>AddRetry</c>, re-running uncoordinated). Before the fix only the persistence
/// block was guarded, so a fault on the initial <c>FirstOrDefaultAsync</c> escaped the handler.
/// </summary>
[Trait("Category", "NoDb")]
public class WebhookExecutorGuardTests
{
    [TimedFact]
    public async Task HandleAsync_InitialReadThrows_CompletesWithoutThrowing()
    {
        // A context whose first Set<WebhookDelivery>() throws stands in for a transient DB fault on the
        // guarded initial read. The build/HTTP deps are never reached on this path, so they can stay null.
        var handler = new ExecuteWebhookDeliveryHandler<FaultingContext>(
            new FaultingContext(),
            publisher: null!,
            httpClientFactory: null!,
            timeProvider: TimeProvider.System,
            standardSigner: new StandardWebhooksSigner(),
            exhaustedHandlers: [],
            customSigners: [],
            adapters: null!,
            logger: NullLogger<ExecuteWebhookDeliveryHandler<FaultingContext>>.Instance);

        await Should.NotThrowAsync(async () =>
            await handler.HandleAsync(
                new ExecuteWebhookDelivery { DeliveryId = Guid.NewGuid() },
                Xunit.TestContext.Current.CancellationToken));
    }

    // A DbContext whose very first Set<WebhookDelivery>() throws — the cheapest, provider-free way to make
    // the guarded initial read fault. Set<TEntity>() is virtual on DbContext.
    private sealed class FaultingContext : DbContext
    {
        public override DbSet<TEntity> Set<TEntity>()
            => throw new InvalidOperationException("simulated transient DB fault on the initial read");
    }
}
