using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Warp.Core.Handlers;

internal static class JobDispatcher
{
    private static readonly ConcurrentDictionary<Type, Type> _handlerInterfaceCache = new();
    private static readonly ConcurrentDictionary<(Type HandlerType, Type MessageType), MethodInfo> _handleMethodCache = new();
    private static readonly ConcurrentDictionary<Type, Type> _jobHandlerTypeCache = new();
    private static readonly ConcurrentDictionary<Type, Type> _messageHandlerTypeCache = new();
    private static readonly ConcurrentDictionary<Type, MethodInfo> _typedHandlerCoreMethodCache = new();

    // IEnumerable<handler interface>, per interface. provider.GetServices(Type) builds it with MakeGenericType
    // on every call, which a worker profile showed on every job.
    private static readonly ConcurrentDictionary<Type, Type> _handlerEnumerableCache = new();

    private static readonly MethodInfo ExecuteTypedHandlerCoreMethodInfo =
        typeof(JobDispatcher).GetMethod(nameof(ExecuteTypedHandlerCore), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>
    /// Discovers all registered IMessageHandler&lt;T&gt; implementation types for a message type.
    /// </summary>
    public static List<Type> DiscoverMessageHandlers(Type messageType, IServiceProvider provider)
    {
        var handlerInterfaceType = _messageHandlerTypeCache.GetOrAdd(
            messageType,
            t => typeof(IMessageHandler<>).MakeGenericType(t));
        var handlers = provider.GetServices(handlerInterfaceType);
        return [.. handlers.Select(h => h!.GetType()).Distinct()];
    }

    /// <summary>
    /// Discovers the single IJobHandler&lt;T&gt; for a job type.
    /// </summary>
    public static Type? DiscoverJobHandler(Type jobType, IServiceProvider provider)
    {
        var handlerInterfaceType = _jobHandlerTypeCache.GetOrAdd(
            jobType,
            t => typeof(IJobHandler<>).MakeGenericType(t));
        var handler = provider.GetService(handlerInterfaceType);

        return handler?.GetType();
    }

    /// <summary>
    /// Gets the handler interface (IMessageHandler or IJobHandler) that a concrete handler type implements.
    /// Cached for performance.
    /// </summary>
    public static Type GetHandlerInterface(Type handlerType)
    {
        return _handlerInterfaceCache.GetOrAdd(handlerType, t => t.GetInterfaces()
            .First(i => i.IsGenericType &&
                (i.GetGenericTypeDefinition() == typeof(IMessageHandler<>) ||
                 i.GetGenericTypeDefinition() == typeof(IJobHandler<>))));
    }

    /// <summary>
    /// Executes a specific handler through the pipeline behavior chain.
    /// Derives the handler interface automatically from the handler type.
    /// </summary>
    public static Task ExecuteHandler(
        object message,
        Type messageType,
        Type handlerType,
        IServiceProvider provider,
        CancellationToken cancellationToken)
    {
        var handlerInterfaceType = GetHandlerInterface(handlerType);

        return ExecuteHandlerCore(message, messageType, handlerType, handlerInterfaceType, provider, cancellationToken);
    }

    /// <summary>
    /// Executes a job/message handler through the pipeline.
    /// Wraps void-returning handlers to return Unit.
    /// Uses MakeGenericMethod to enter a fully-typed pipeline where direct calls replace reflection.
    /// </summary>
    public static Task ExecuteHandlerCore(
        object message,
        Type messageType,
        Type handlerType,
        Type handlerInterfaceType,
        IServiceProvider provider,
        CancellationToken cancellationToken)
    {
        // The generator's typed dispatch when it saw this type (the common case); reflection for a type
        // registered by hand, or an assembly built without the generator.
        if (WarpGeneratedHandlerRegistry.TryGetJobDispatch(messageType, out var dispatch))
        {
            return dispatch(message, handlerType, provider, cancellationToken);
        }

        var typedMethod = _typedHandlerCoreMethodCache.GetOrAdd(
            messageType,
            t => ExecuteTypedHandlerCoreMethodInfo.MakeGenericMethod(t));

        return (Task)typedMethod.Invoke(null, [message, handlerType, handlerInterfaceType, provider, cancellationToken])!;
    }

    /// <summary>
    /// Fully-typed handler core for job/message types. Uses direct calls instead of MethodInfo.Invoke.
    /// </summary>
    private static async Task ExecuteTypedHandlerCore<TRequest>(
        object message,
        Type handlerType,
        Type handlerInterfaceType,
        IServiceProvider provider,
        CancellationToken cancellationToken)
        where TRequest : IRequest<Unit>
    {
        // Resolve handler via its interface to get the DI-registered instance
        var enumerableType = _handlerEnumerableCache.GetOrAdd(
            handlerInterfaceType,
            t => typeof(IEnumerable<>).MakeGenericType(t));
        var allHandlers = (IEnumerable<object?>)provider.GetRequiredService(enumerableType);
        var handler = allHandlers.First(h => h!.GetType() == handlerType);

        // Find the HandleAsync method on the handler (cached)
        var handleMethod = _handleMethodCache.GetOrAdd(
            (handlerType, typeof(TRequest)),
            key => key.HandlerType.GetMethod(
                "HandleAsync",
                [key.MessageType, typeof(CancellationToken)])!);

        // Build pipeline with typed delegate
        var typedMessage = (TRequest)message;
        var behaviors = provider.GetServices<IPipelineBehavior<TRequest, Unit>>().ToArray();
        RequestHandlerDelegate<TRequest, Unit> chain = Innermost;
        for (var i = behaviors.Length - 1; i >= 0; i--)
        {
            var b = behaviors[i];
            var next = chain;
            chain = (req, ct) => b.HandleAsync(req, next, ct);
        }

        await chain(typedMessage, cancellationToken);

        async Task<Unit> Innermost(TRequest req, CancellationToken ct)
        {
            await ((Task)handleMethod.Invoke(handler, [req, ct])!);

            return Unit.Value;
        }
    }

    /// <summary>
    /// Execute a message handler (IMessageHandler&lt;T&gt;).
    /// </summary>
    public static Task ExecuteMessageHandler(
        object message,
        Type messageType,
        Type handlerType,
        IServiceProvider provider,
        CancellationToken ct)
    {
        var handlerInterfaceType = _messageHandlerTypeCache.GetOrAdd(
            messageType,
            t => typeof(IMessageHandler<>).MakeGenericType(t));

        return ExecuteHandlerCore(message, messageType, handlerType, handlerInterfaceType, provider, ct);
    }

    /// <summary>
    /// Execute a job handler (IJobHandler&lt;T&gt;).
    /// </summary>
    public static Task ExecuteJobHandler(
        object message,
        Type messageType,
        Type handlerType,
        IServiceProvider provider,
        CancellationToken ct)
    {
        var handlerInterfaceType = _jobHandlerTypeCache.GetOrAdd(
            messageType,
            t => typeof(IJobHandler<>).MakeGenericType(t));

        return ExecuteHandlerCore(message, messageType, handlerType, handlerInterfaceType, provider, ct);
    }
}
