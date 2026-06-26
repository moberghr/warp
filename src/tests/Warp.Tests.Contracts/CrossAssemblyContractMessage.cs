using Warp.Core.Handlers;

namespace Warp.Tests.Contracts;

/// <summary>
/// A message contract whose handler is declared in a *different* assembly (Warp.Tests). Exercises
/// the source generator's handler-driven discovery: the message type is never a local candidate in
/// the worker assembly, so before the fix no handler was registered and the job failed with
/// "No handlers registered for message type CrossAssemblyContractMessage".
/// </summary>
public sealed class CrossAssemblyContractMessage : IMessage
{
    public string Value { get; init; } = string.Empty;
}
