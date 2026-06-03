namespace Warp.Core.Handlers;

/// <summary>
/// In-process dispatcher for <see cref="IRequest{TResponse}"/> and <see cref="IStreamRequest{TResponse}"/>.
/// Runs the same <c>IPipelineBehavior</c> chain as jobs and messages, but synchronously and with no
/// database persistence, worker, or retries — exceptions bubble straight back to the caller.
/// Resolved as a scoped service; inject <c>IMediator</c>.
/// </summary>
public interface IMediator
{
    /// <summary>Dispatches <paramref name="request"/> to its single
    /// <c>IRequestHandler&lt;TRequest, TResponse&gt;</c> and returns the response.</summary>
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);

    /// <summary>Dispatches <paramref name="request"/> to its
    /// <c>IStreamRequestHandler&lt;TRequest, TResponse&gt;</c>, returning an
    /// <see cref="IAsyncEnumerable{T}"/> that yields items lazily as it is enumerated.</summary>
    IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default);
}
