using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Warp.Core;
using Warp.Core.BackgroundServices;
using Warp.Core.Handlers;

namespace Warp.Test.Shared.Shop;

/// <summary>Recurring job — a periodic sales summary over the orders table (registered on a cron).</summary>
public sealed class GenerateSalesReportRequest : IJob;

public sealed class GenerateSalesReportHandler : IJobHandler<GenerateSalesReportRequest>
{
    private readonly TestContext _context;
    private readonly ILogger<GenerateSalesReportHandler> _logger;

    public GenerateSalesReportHandler(TestContext context, ILogger<GenerateSalesReportHandler> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task HandleAsync(GenerateSalesReportRequest message, CancellationToken cancellationToken)
    {
        var byStatus = await _context.Orders
            .GroupBy(x => x.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var shippedRevenue = await _context.Orders
            .Where(x => x.Status == "Shipped")
            .SumAsync(x => (decimal?)x.Amount, cancellationToken) ?? 0m;

        var summary = string.Join(", ", byStatus.Select(x => $"{x.Status}={x.Count}"));
        _logger.LogInformation("Sales report — orders [{Summary}], shipped revenue {Revenue:C}.", summary, shippedRevenue);
    }
}

/// <summary>Recurring job — re-enqueues orders stuck in Pending/Failed (e.g. a declined charge) so a
/// transient decline eventually clears without any failed jobs in the UI.</summary>
public sealed class RetryPendingPaymentsRequest : IJob;

public sealed class RetryPendingPaymentsHandler : IJobHandler<RetryPendingPaymentsRequest>
{
    private readonly TestContext _context;
    private readonly IPublisher _publisher;
    private readonly ILogger<RetryPendingPaymentsHandler> _logger;

    public RetryPendingPaymentsHandler(TestContext context, IPublisher publisher, ILogger<RetryPendingPaymentsHandler> logger)
    {
        _context = context;
        _publisher = publisher;
        _logger = logger;
    }

    public async Task HandleAsync(RetryPendingPaymentsRequest message, CancellationToken cancellationToken)
    {
        var stuck = await _context.Orders
            .Where(x => x.Status == "Failed" || x.Status == "Pending")
            .OrderBy(x => x.CreatedAt)
            .Take(25)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        if (stuck.Count == 0)
        {
            return;
        }

        foreach (var id in stuck)
        {
            await _publisher.Enqueue(new PlaceOrderRequest { OrderId = id }, ShopQueues.Fulfillment);
        }

        await _publisher.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Re-enqueued {Count} pending/failed orders for another fulfillment attempt.", stuck.Count);
    }
}

/// <summary>
/// Cluster-singleton background service — periodically logs SKUs below the reorder threshold as orders
/// deplete stock. Injects <see cref="IServiceScopeFactory"/> (singleton → no captive DbContext).
/// </summary>
public sealed class LowStockMonitor : WarpBackgroundService
{
    private const int ReorderThreshold = 5;

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<LowStockMonitor> _logger;

    public LowStockMonitor(IServiceScopeFactory scopes, ILogger<LowStockMonitor> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    public override ServiceScope Scope => ServiceScope.Singleton;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("LowStockMonitor acquired the lease; watching stock every 15s (threshold {Threshold}).", ReorderThreshold);

        while (!ct.IsCancellationRequested)
        {
            await ReportLowStockAsync(ct);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ReportLowStockAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TestContext>();

        var low = await context.Products
            .Where(x => x.Stock < ReorderThreshold)
            .OrderBy(x => x.Stock)
            .Select(x => new { x.Sku, x.Stock })
            .ToListAsync(ct);

        if (low.Count == 0)
        {
            _logger.LogInformation("Stock healthy — no SKUs below the reorder threshold.");

            return;
        }

        var list = string.Join(", ", low.Select(x => $"{x.Sku}:{x.Stock}"));
        _logger.LogWarning("Low stock — reorder needed for {Count} SKU(s): {List}", low.Count, list);
    }
}

/// <summary>Queue names the shop uses. Fulfillment jobs run only where the adapters are registered.</summary>
public static class ShopQueues
{
    public const string Fulfillment = "fulfillment";
}
