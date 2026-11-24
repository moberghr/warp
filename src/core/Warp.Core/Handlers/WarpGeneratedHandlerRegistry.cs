using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Warp.Core.Handlers;

/// <summary>
/// Drop-off point for the Warp source generator. Each consumer assembly with
/// <c>Warp.SourceGenerator</c> referenced emits a <c>[ModuleInitializer]</c> that
/// pushes its handler/behavior DI registrations here at assembly load, and
/// <c>AddWarp</c> replays them onto the user's <see cref="IServiceCollection"/>.
/// </summary>
public static class WarpGeneratedHandlerRegistry
{
    private static readonly Lock _gate = new();
    private static readonly List<Action<IServiceCollection>> _registrations = [];
    private static readonly ConcurrentDictionary<Type, GeneratedJobDispatch> _jobDispatches = new();

    public static void Add(Action<IServiceCollection> register)
    {
        ArgumentNullException.ThrowIfNull(register);

        lock (_gate)
        {
            _registrations.Add(register);
        }
    }

    /// <summary>
    /// Registers the generated, typed dispatch of one job or message type. Several assemblies may
    /// declare handlers for one message type; their dispatches are equivalent, so the first is kept.
    /// </summary>
    public static void AddJobDispatch(Type messageType, GeneratedJobDispatch dispatch)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(dispatch);

        _jobDispatches.TryAdd(messageType, dispatch);
    }

    internal static bool TryGetJobDispatch(Type messageType, out GeneratedJobDispatch dispatch) =>
        _jobDispatches.TryGetValue(messageType, out dispatch!);

    internal static void ApplyAll(IServiceCollection services)
    {
        Action<IServiceCollection>[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _registrations];
        }

        foreach (var register in snapshot)
        {
            register(services);
        }
    }
}
