namespace Warp.Core.Handlers;

/// <summary>
/// Implemented by the mediator that <see cref="WarpMediatorOptions.ScopePerSend"/> installs. Lets a
/// caller that already owns a per-operation scope (Warp's HTTP binding: one scope per request)
/// dispatch in that scope instead of a new one. Public because <c>Warp.Http</c> composes against
/// Core's public API only.
/// </summary>
public interface ISendScopeBypass
{
    /// <summary>A mediator that dispatches in the scope this one was resolved from.</summary>
    IMediator InCurrentScope { get; }
}
