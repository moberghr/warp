using Warp.Core.Handlers;

namespace Warp.Core.Handlers;

/// <summary>
/// Lightweight flow handler for trace visualization testing.
/// Spawns: 3 emails + batch of 4 + parent with 2 continuations (one publishes a message with 2 handlers).
/// </summary>
public class LightFlowRequest : IJob;

public class LightFlowHandler : IJobHandler<LightFlowRequest>
{
    private readonly IPublisher _publisher;
    private readonly IBatchPublisher _batchPublisher;
    private readonly TestContext _context;

    public LightFlowHandler(IPublisher publisher, IBatchPublisher batchPublisher, TestContext context)
    {
        _publisher = publisher;
        _batchPublisher = batchPublisher;
        _context = context;
    }

    public async Task HandleAsync(LightFlowRequest message, CancellationToken cancellationToken)
    {
        var email = new OrderConfirmationRequest { EmailLogId = 1 };

        // 3 spawned emails
        for (var i = 0; i < 3; i++)
        {
            await _publisher.Enqueue(email);
        }

        // Batch of 4
        var batch = Enumerable.Range(0, 4).Select(_ => new OrderConfirmationRequest { EmailLogId = 1 }).ToList();
        await _batchPublisher.StartNew(batch);

        // Parent email with 2 continuations:
        // - one regular email
        // - one PublishInvoice (its handler publishes InvoiceNotification message → 2 handlers)
        var parentId = await _publisher.Enqueue(email);
        await _publisher.Enqueue(email, parentId);
        await _publisher.Enqueue(new PublishInvoiceRequest { OrderId = "LIGHT-001" }, parentId);

        await _context.SaveChangesAsync(cancellationToken);
    }
}
