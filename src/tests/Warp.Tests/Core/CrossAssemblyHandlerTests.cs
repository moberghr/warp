using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Core.Handlers;
using Warp.Core.Handlers.Generated;
using Warp.Provider.PostgreSql;
using Warp.Tests.Contracts;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Core;

/// <summary>
/// Pins the source generator's handler-driven discovery for the shared-contract layout: the
/// message contract (<see cref="CrossAssemblyContractMessage"/>) lives in the separate
/// <c>Warp.Tests.Contracts</c> assembly while its handler
/// (<see cref="CrossAssemblyContractMessageHandler"/>) is local to this assembly. Before the fix
/// the generator only discovered message types declared locally, so the handler was never
/// DI-registered or routed and the worker failed the job with "No handlers registered".
/// </summary>
[Trait("Category", "NoDb")]
public sealed class CrossAssemblyHandlerTests
{
    private const string DummyConnectionString = "Host=x;Database=x;Username=x;Password=x";

    [TimedFact]
    public void CrossAssemblyMessage_RegistersHandlerExactlyOnce_ViaHandlerDrivenDiscovery()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestContext>(x => x.UseNpgsql(DummyConnectionString));
        services.AddWarp<TestContext>(opt => opt.UsePostgreSql());

        var registrations = services
            .Where(x => x.ServiceType == typeof(IMessageHandler<CrossAssemblyContractMessage>))
            .Where(x => x.ImplementationType == typeof(CrossAssemblyContractMessageHandler))
            .ToList();

        // Exactly one: the generator must register the handler for the referenced-assembly
        // contract (the fix) without the handler-driven pass double-emitting it (dedup, §detail 3).
        registrations.Count.ShouldBe(1);
    }

    [TimedFact]
    public void CrossAssemblyMessage_HandlerIsDiscoverable_AtRuntime()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestContext>(x => x.UseNpgsql(DummyConnectionString));
        services.AddSingleton<CrossAssemblyContractCounter>();
        services.AddWarp<TestContext>(opt => opt.UsePostgreSql());

        using var provider = services.BuildServiceProvider();

        // Mirrors JobDispatcher.DiscoverMessageHandlers — the exact resolution whose empty result
        // produced the "No handlers registered for message type ..." failure in MessageRouter.
        var handlers = provider.GetServices<IMessageHandler<CrossAssemblyContractMessage>>().ToList();

        handlers.ShouldHaveSingleItem().ShouldBeOfType<CrossAssemblyContractMessageHandler>();
    }

    [TimedFact]
    public async Task CrossAssemblyMessage_Dispatcher_RoutesAndInvokesHandler()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestContext>(x => x.UseNpgsql(DummyConnectionString));
        services.AddSingleton<CrossAssemblyContractCounter>();
        services.AddWarp<TestContext>(opt => opt.UsePostgreSql());

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var message = new CrossAssemblyContractMessage { Value = "cross-asm" };

        // The generated dispatch map must contain an entry for the referenced-assembly contract
        // (§detail 2). TryExecute returns null for unknown types, so a non-null Task proves routing.
        var task = GeneratedJobDispatcher.TryExecute(
            message,
            typeof(CrossAssemblyContractMessage),
            typeof(CrossAssemblyContractMessageHandler),
            scope.ServiceProvider,
            Xunit.TestContext.Current.CancellationToken);

        await task.ShouldNotBeNull();

        var counter = provider.GetRequiredService<CrossAssemblyContractCounter>();
        counter.Count.ShouldBe(1);
        counter.LastValue.ShouldBe("cross-asm");
    }
}
