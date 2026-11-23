namespace Warp.Core.Handlers;

/// <summary>
/// A source-generated, typed execution of one job or message type: resolves the handler named by
/// <paramref name="handlerType"/> and runs it through its pipeline behaviors without reflection.
/// </summary>
public delegate Task GeneratedJobDispatch(object message, Type handlerType, IServiceProvider provider, CancellationToken cancellationToken);
