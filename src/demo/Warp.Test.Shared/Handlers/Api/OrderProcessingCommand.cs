using Microsoft.Extensions.Logging;
using Warp.Core.Handlers;

namespace Warp.Core.Handlers;

/// <summary>
/// Demonstrates a complex flow:
/// Message (OrderPlaced) → 2 handlers (OrderEmailHandler, OrderSlackHandler)
/// Job (ProcessOrderRequest) → spawns a batch of ShipItemRequest → continuation PublishInvoiceRequest
/// PublishInvoiceRequest → publishes InvoiceNotification message → 2 handlers
/// </summary>
public class ProcessOrderRequest : IJob
{
    public string OrderId { get; set; } = "ORD-001";
}

public class ProcessOrderHandler : IJobHandler<ProcessOrderRequest>
{
    private readonly IBatchPublisher _batchPublisher;
    private readonly ILogger<ProcessOrderHandler> _logger;

    public ProcessOrderHandler(IBatchPublisher batchPublisher, ILogger<ProcessOrderHandler> logger)
    {
        _batchPublisher = batchPublisher;
        _logger = logger;
    }

    public async Task HandleAsync(ProcessOrderRequest message, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing order {OrderId}", message.OrderId);

        // Create a batch of 5 ship items
        var shipItems = Enumerable.Range(1, 5)
            .Select(i => new ShipItemRequest { OrderId = message.OrderId, ItemIndex = i })
            .ToList();

        var batchId = await _batchPublisher.StartNew(shipItems);

        // Continuation after batch completes: publish invoice
        var invoiceJobs = new List<PublishInvoiceRequest>
        {
            new() { OrderId = message.OrderId },
        };
        await _batchPublisher.ContinueBatchWith(invoiceJobs, batchId);

        // The handler saves what it stages: the worker does not save a handler's context.
        await _batchPublisher.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created batch of {Count} shipments with invoice continuation", shipItems.Count);
    }
}

public class ShipItemRequest : IJob
{
    public string OrderId { get; set; } = string.Empty;

    public int ItemIndex { get; set; }
}

public class ShipItemHandler : IJobHandler<ShipItemRequest>
{
    private readonly ILogger<ShipItemHandler> _logger;

    public ShipItemHandler(ILogger<ShipItemHandler> logger)
    {
        _logger = logger;
    }

    public async Task HandleAsync(ShipItemRequest message, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Shipping item {Index} for order {OrderId}", message.ItemIndex, message.OrderId);
        await Task.Delay(100, cancellationToken); // Simulate work
    }
}

public class PublishInvoiceRequest : IJob
{
    public string OrderId { get; set; } = string.Empty;
}

public class PublishInvoiceHandler : IJobHandler<PublishInvoiceRequest>
{
    private readonly IPublisher _publisher;
    private readonly ILogger<PublishInvoiceHandler> _logger;
    private readonly TestContext _context;

    public PublishInvoiceHandler(IPublisher publisher, ILogger<PublishInvoiceHandler> logger, TestContext context)
    {
        _publisher = publisher;
        _logger = logger;
        _context = context;
    }

    public async Task HandleAsync(PublishInvoiceRequest message, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Publishing invoice notification for order {OrderId}", message.OrderId);
        await _publisher.Publish(new InvoiceNotification { OrderId = message.OrderId });
        await _context.SaveChangesAsync(cancellationToken);
    }
}

public class InvoiceNotification : IMessage
{
    public string OrderId { get; set; } = string.Empty;
}

public class InvoiceEmailHandler : IMessageHandler<InvoiceNotification>
{
    private readonly ILogger<InvoiceEmailHandler> _logger;

    public InvoiceEmailHandler(ILogger<InvoiceEmailHandler> logger)
    {
        _logger = logger;
    }

    public Task HandleAsync(InvoiceNotification message, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Sending invoice email for order {OrderId}", message.OrderId);
        return Task.CompletedTask;
    }
}

public class InvoiceWebhookHandler : IMessageHandler<InvoiceNotification>
{
    private readonly ILogger<InvoiceWebhookHandler> _logger;

    public InvoiceWebhookHandler(ILogger<InvoiceWebhookHandler> logger)
    {
        _logger = logger;
    }

    public Task HandleAsync(InvoiceNotification message, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Sending invoice webhook for order {OrderId}", message.OrderId);
        return Task.CompletedTask;
    }
}
