using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Warp.Core.Handlers;

/// <summary>
/// The mediator installed by <see cref="WarpMediatorOptions.ScopePerSend"/>: each send creates an async
/// scope, runs the registered <see cref="ISendScopeInitializer"/>s with (parent, child), dispatches in
/// the child and disposes it when the send returns. A handler that resolves <see cref="IMediator"/>
/// gets another instance of this class bound to the child scope, so nested sends get their own scope.
/// </summary>
internal sealed class ScopePerSendMediator : IMediator, ISendScopeBypass
{
    private readonly IServiceProvider _parent;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISendScopeInitializer[] _initializers;

    public ScopePerSendMediator(IServiceProvider parent, IServiceScopeFactory scopeFactory, IEnumerable<ISendScopeInitializer> initializers)
    {
        _parent = parent;
        _scopeFactory = scopeFactory;
        _initializers = [.. initializers];
    }

    public IMediator InCurrentScope => new Mediator(_parent);

    public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        // A stream request sent through Send returns its enumerable before anything is enumerated, so a
        // scope created here would be disposed under the stream. It runs in the caller's scope, like
        // InCurrentScope; CreateStream is the way to get a scope that lives as long as the enumeration.
        if (typeof(TResponse).IsGenericType && typeof(TResponse).GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
        {
            return await InCurrentScope.Send(request, cancellationToken);
        }

        await using var scope = CreateScope();

        return await MediatorDispatcher.ExecuteRequestHandler<TResponse>(request, request.GetType(), scope.ServiceProvider, cancellationToken);
    }

    // An async iterator, so the scope is created when enumeration starts, not when CreateStream is
    // called: a stream that is never enumerated creates nothing, and one abandoned mid-way disposes its
    // scope when the enumerator is disposed.
    public async IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var scope = CreateScope();

        var stream = MediatorDispatcher.ExecuteStreamHandler<TResponse>(request, request.GetType(), scope.ServiceProvider, cancellationToken);
        await foreach (var item in stream.WithCancellation(cancellationToken))
        {
            yield return item;
        }
    }

    private AsyncServiceScope CreateScope()
    {
        var scope = _scopeFactory.CreateAsyncScope();
        try
        {
            for (var i = 0; i < _initializers.Length; i++)
            {
                _initializers[i].Initialize(_parent, scope.ServiceProvider);
            }
        }
        catch
        {
            scope.Dispose();

            throw;
        }

        return scope;
    }
}
