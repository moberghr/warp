using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Warp.Core;

/// <summary>
/// A single-process <see cref="IWarpLockProvider"/> backed by one named <see cref="SemaphoreSlim"/>
/// per lock name. It satisfies the provider requirement of <c>opt.AddSagas()</c> (and any other
/// Warp feature that only needs mutual exclusion, e.g. recurring-job registration) for
/// <b>tests and single-process / mediator-only hosts</b> that never register a database provider.
/// </summary>
/// <remarks>
/// <para>
/// <b>Single-process only.</b> This provider serializes lock acquisition <em>within one process</em>.
/// It provides <b>no cross-process serialization</b>: two processes each holding their own
/// <see cref="InProcessLockProvider"/> will both acquire a lock of the same name simultaneously.
/// A multi-server deployment — anything where more than one process fetches and executes the same
/// jobs — <b>MUST</b> use the database-backed provider (<c>opt.UsePostgreSql()</c> /
/// <c>opt.UseSqlServer()</c>), whose lock is <c>pg_try_advisory_lock</c> / <c>sp_getapplock</c>
/// scoped to the shared database.
/// </para>
/// <para>
/// <see cref="TryAcquireAsync"/> honours the timeout exactly like the DB-backed providers: a
/// <see cref="TimeSpan.Zero"/> timeout fast-fails (returns <c>null</c>) when the lock is already
/// held, a positive timeout waits up to that long, and the returned handle releases the lock once
/// on <see cref="IAsyncDisposable.DisposeAsync"/>.
/// </para>
/// </remarks>
public sealed class InProcessLockProvider : IWarpLockProvider, IDisposable
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private int _disposed;

    /// <inheritdoc />
    public async Task<IAsyncDisposable?> TryAcquireAsync(string name, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);

        var gate = _gates.GetOrAdd(name, static _ => new SemaphoreSlim(1, 1));

        var acquired = await gate.WaitAsync(timeout, ct).ConfigureAwait(false);
        if (!acquired)
        {
            return null;
        }

        return new Handle(gate);
    }

    /// <summary>
    /// Disposes every named semaphore. The DI container calls this when the owning provider is
    /// disposed; there is nothing to release because handles are disposed by their callers.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var gate in _gates.Values)
        {
            gate.Dispose();
        }

        _gates.Clear();
    }

    private sealed class Handle(SemaphoreSlim gate) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Builder extension that registers the single-process <see cref="InProcessLockProvider"/> as the
/// <see cref="IWarpLockProvider"/>. Call it inside the <c>AddWarp</c> / <c>AddWarpServer</c> lambda
/// <b>before</b> <c>opt.AddSagas()</c> (mirroring the provider-first ordering of
/// <c>opt.UsePostgreSql()</c>) when the host has no database provider — the common shape for saga
/// unit tests and mediator-only processes.
/// </summary>
public static class InProcessLockProviderExtensions
{
    /// <summary>
    /// Registers <see cref="InProcessLockProvider"/> as the process-wide <see cref="IWarpLockProvider"/>.
    /// No-op if an <see cref="IWarpLockProvider"/> is already registered (e.g. a DB provider was
    /// configured first), so it composes safely.
    /// </summary>
    public static IWarpBuilder<TContext> UseInProcessLock<TContext>(this IWarpBuilder<TContext> builder)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<IWarpLockProvider, InProcessLockProvider>();

        return builder;
    }
}
