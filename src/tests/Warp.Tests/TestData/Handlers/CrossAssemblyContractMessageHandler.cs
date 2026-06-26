using Warp.Core.Handlers;
using Warp.Tests.Contracts;

namespace Warp.Tests.TestData.Handlers;

/// <summary>
/// Handler for <see cref="CrossAssemblyContractMessage"/>, which is declared in the separate
/// <c>Warp.Tests.Contracts</c> assembly. Pins the source generator's handler-driven discovery:
/// the handler is local to Warp.Tests, the message contract is not, so only handler-keyed
/// discovery can register and route it.
/// </summary>
public sealed class CrossAssemblyContractMessageHandler : IMessageHandler<CrossAssemblyContractMessage>
{
    private readonly CrossAssemblyContractCounter _counter;

    public CrossAssemblyContractMessageHandler(CrossAssemblyContractCounter counter)
    {
        _counter = counter;
    }

    public Task HandleAsync(CrossAssemblyContractMessage message, CancellationToken cancellationToken)
    {
        _counter.Record(message.Value);
        return Task.CompletedTask;
    }
}

public sealed class CrossAssemblyContractCounter
{
    private int _count;

    public int Count => _count;

    public string? LastValue { get; private set; }

    public void Record(string value)
    {
        Interlocked.Increment(ref _count);
        LastValue = value;
    }
}
