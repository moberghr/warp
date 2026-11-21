namespace Warp.Core.Handlers;

/// <summary>
/// Settings for the in-memory mediator, set through <c>opt.Mediator</c> in <c>AddWarp</c> /
/// <c>AddWarpServer</c>. A record, so two builders that configure it the same way compare equal when
/// their settings are merged.
/// </summary>
public sealed record WarpMediatorOptions
{
    /// <summary>
    /// When true, every <see cref="IMediator.Send{TResponse}"/> and
    /// <see cref="IMediator.CreateStream{TResponse}"/> runs in its own DI scope, disposed when the send
    /// returns or the stream finishes. Default false: the handler runs in the scope the mediator was
    /// resolved from.
    /// <para>
    /// For hosts whose ambient scope is long-lived, typically Blazor Server, where the scope is the
    /// circuit and lasts as long as the browser tab: without this, every send from a page shares one
    /// scoped <c>DbContext</c>, so concurrent component work throws and tracked entities pile up.
    /// State bound to the outer scope (tenant, acting user) is carried into the new one by registered
    /// <see cref="ISendScopeInitializer"/>s. A send made from inside a handler gets its own scope too.
    /// Warp HTTP endpoints keep using the request's scope, which is already one per request.
    /// </para>
    /// </summary>
    public bool ScopePerSend { get; set; }
}
