using Microsoft.Extensions.DependencyInjection;
using Warp.Core;
using Warp.Core.BackgroundServices;
using Warp.Core.Handlers;

namespace Warp.Test.Shared.Shop;

// A few realistic "buggy" handlers so the Issues dashboard (§8.29 error grouping) has genuine errors to
// group and diagnose in the demo. Each throws a DISTINCT exception type from a DISTINCT handler, so error
// grouping fingerprints them into separate issues; messages carry an order/sku id to show the message
// normalization in the group title (e.g. "order <num>"). These fail terminally on purpose.
public sealed class ChargeOrderRequest : IJob
{
    public int OrderId { get; set; }
}

public sealed class ChargeOrderHandler : IJobHandler<ChargeOrderRequest>
{
    public Task HandleAsync(ChargeOrderRequest message, CancellationToken cancellationToken)
        => throw new TimeoutException($"Payment gateway did not respond for order {message.OrderId} within 30s.");
}

public sealed class ReserveInventoryRequest : IJob
{
    public int OrderId { get; set; }

    public string Sku { get; set; } = string.Empty;
}

public sealed class ReserveInventoryHandler : IJobHandler<ReserveInventoryRequest>
{
    public Task HandleAsync(ReserveInventoryRequest message, CancellationToken cancellationToken)
        => throw new InvalidOperationException($"SKU {message.Sku} oversold on order {message.OrderId}: reserved 1 but 0 in stock.");
}

public sealed class EnrichCustomerRequest : IJob
{
    public int CustomerId { get; set; }
}

public sealed class EnrichCustomerHandler : IJobHandler<EnrichCustomerRequest>
{
    private static readonly Dictionary<int, string> Profiles = [];

    public Task HandleAsync(EnrichCustomerRequest message, CancellationToken cancellationToken)
    {
        // Bug: an unknown customer's profile comes back null and is dereferenced below → NullReferenceException.
        var profile = Profiles.GetValueOrDefault(message.CustomerId);
        _ = profile!.Length;

        return Task.CompletedTask;
    }
}

/// <summary>
/// Demo-only: periodically stages a mix of failing jobs so the Issues page has live, grouped errors to
/// diagnose. Cluster-singleton so exactly one host injects. Not representative of production — it exists to
/// show error grouping working end-to-end.
/// </summary>
public sealed class FaultInjectorService : WarpBackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private int _sequence;

    public FaultInjectorService(IServiceScopeFactory scopes) => _scopes = scopes;

    public override ServiceScope Scope => ServiceScope.Singleton;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await InjectAsync();

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task InjectAsync()
    {
        using var scope = _scopes.CreateScope();
        var publisher = scope.ServiceProvider.GetRequiredService<IPublisher>();
        var context = scope.ServiceProvider.GetRequiredService<TestContext>();

        var n = Interlocked.Increment(ref _sequence);

        await publisher.Enqueue(new ChargeOrderRequest { OrderId = 1000 + n });
        await publisher.Enqueue(new ReserveInventoryRequest { OrderId = 2000 + n, Sku = $"SKU-{100 + (n % 5)}" });

        // The customer-enrichment bug fires less often — a lower-volume issue in the mix.
        if (n % 3 == 0)
        {
            await publisher.Enqueue(new EnrichCustomerRequest { CustomerId = 5000 + n });
        }

        // Enqueue stages jobs in the outbox on this scope's context; the caller commits (§5.8).
        await context.SaveChangesAsync();
    }
}
