using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Warp.Core.Data;

/// <summary>
/// Warp's own context for the addon pipeline stores (concurrency, rate limit, circuit breaker), opened on
/// first use and disposed with the job's scope (rule 6.17). Never the application's context: those stores
/// read and write Warp's tables only, and a job that carries no policy must not build any context at all.
/// The behaviours are constructed for every job and for in-memory sends, so nothing here may touch the
/// database or resolve a context until a store is actually asked for something; in a process without a
/// server context (an <c>AddWarp</c>-only host) the handle is never opened, because only an executing job
/// reaches a store.
/// </summary>
internal sealed class DeferredWarpContext : IDisposable, IAsyncDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private AsyncServiceScope? _scope;
    private DbContext? _context;

    public DeferredWarpContext(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public DbContext Context
    {
        get
        {
            if (_context is null)
            {
                _scope = _scopeFactory.CreateAsyncScope();
                _context = _scope.Value.ServiceProvider.GetRequiredService<IWarpServerContext>().Context;
            }

            return _context;
        }
    }

    /// <summary>
    /// A separate, short-lived context for a write that must not leave a failed entity tracked on
    /// <see cref="Context"/> (an insert that may lose a unique-key race).
    /// </summary>
    public AsyncServiceScope CreateIsolatedScope(out DbContext context)
    {
        var scope = _scopeFactory.CreateAsyncScope();
        context = scope.ServiceProvider.GetRequiredService<IWarpServerContext>().Context;

        return scope;
    }

    public void Dispose() => _scope?.Dispose();

    public ValueTask DisposeAsync() => _scope?.DisposeAsync() ?? ValueTask.CompletedTask;
}
